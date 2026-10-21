using System.Net;
using System.Text;

namespace AiObservatory.Ingest.Pricing;

// The production client comes from IHttpClientFactory (the named client registered in
// Program), so its handler and connection pool are pooled across refresh passes instead of
// each scoped pass abandoning one to the finalizer. Only a self-built client (the internal
// handler ctor used by tests) is owned and disposed here.
public sealed class FirstPartyDocumentFetcher : IDisposable
{
    public const string HttpClientName = "FirstPartyPricingDocuments";

    private const int MaximumRedirects = 3;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ProductionRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HashSet<string> _allowedHosts;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestTimeout;
    private readonly Uri _source;
    private readonly TimeProvider _timeProvider;

    public FirstPartyDocumentFetcher(HttpClient client, Uri source, IEnumerable<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(client);
        (_allowedHosts, _source, _requestTimeout) = Validate(source, allowedHosts, null);
        _client = client;
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _timeProvider = TimeProvider.System;
    }

    internal FirstPartyDocumentFetcher(
        Uri source,
        IEnumerable<string> allowedHosts,
        HttpMessageHandler? handler,
        TimeSpan? requestTimeout = null,
        TimeProvider? timeProvider = null
    )
    {
        (_allowedHosts, _source, _requestTimeout) = Validate(source, allowedHosts, requestTimeout);
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ownsClient = true;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    public async Task<FirstPartyDocument> FetchAsync(CancellationToken cancellationToken)
    {
        // The TimeProvider seam keeps the timeout testable: production runs on the system
        // clock; tests fire the timer manually instead of racing a real short delay.
        using var timeout = new CancellationTokenSource(_requestTimeout, _timeProvider);
        using var caller = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            timeout
        );
        var current = _source;
        var redirects = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                // No conditional headers are ever sent, so a 304 is out-of-spec for this
                // client: only a non-conforming intermediary could produce one. Treat it as a
                // failed fetch rather than silently skipping the refresh.
                throw new InvalidDataException(
                    "The first-party document returned 304 Not Modified to an unconditional request."
                );
            }

            if (IsRedirect(response.StatusCode))
            {
                if (redirects == MaximumRedirects || response.Headers.Location is null)
                {
                    throw new InvalidDataException("The first-party document exceeded its redirect limit.");
                }

                var destination = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                try
                {
                    ValidateDestination(destination);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("The redirect left the HTTPS host allowlist.", exception);
                }
                current = destination;
                redirects++;
                continue;
            }

            response.EnsureSuccessStatusCode();
            var content = await ReadUtf8Async(response.Content, timeout.Token);
            return new FirstPartyDocument(current, content);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status
            is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadUtf8Async(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The first-party document exceeded the response size limit.");
        }

        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (
            charset is not null
            && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(charset, "utf8", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException("The first-party document is not UTF-8.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("The first-party document exceeded the response size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static (HashSet<string> AllowedHosts, Uri Source, TimeSpan RequestTimeout) Validate(
        Uri source,
        IEnumerable<string> allowedHosts,
        TimeSpan? requestTimeout
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        var hosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        if (hosts.Count == 0 || hosts.Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown))
        {
            throw new ArgumentException("At least one valid host is required.", nameof(allowedHosts));
        }

        var timeout = requestTimeout ?? ProductionRequestTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        if (
            !source.IsAbsoluteUri
            || source.Scheme != Uri.UriSchemeHttps
            || !source.IsDefaultPort
            || source.UserInfo.Length != 0
            || source.Query.Length != 0
            || source.Fragment.Length != 0
            || !hosts.Contains(source.Host)
        )
        {
            throw new ArgumentException("The first-party document URI is outside the HTTPS host allowlist.");
        }

        return (hosts, source, timeout);
    }

    private void ValidateDestination(Uri destination)
    {
        if (
            !destination.IsAbsoluteUri
            || destination.Scheme != Uri.UriSchemeHttps
            || !destination.IsDefaultPort
            || destination.UserInfo.Length != 0
            || destination.Query.Length != 0
            || destination.Fragment.Length != 0
            || !_allowedHosts.Contains(destination.Host)
        )
        {
            throw new ArgumentException("The first-party document URI is outside the HTTPS host allowlist.");
        }
    }
}

public sealed record FirstPartyDocument(Uri FinalUri, string Content);
