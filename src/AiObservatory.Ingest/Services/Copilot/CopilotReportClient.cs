using System.Buffers;
using System.Text;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.Copilot;

public sealed class CopilotReportClient(HttpClient descriptorHttp, HttpClient downloadHttp, string organization)
    : ICopilotReportClient
{
    private const long MaxDownloadBytes = 50L * 1024 * 1024;
    private const int MaxDescriptorBytes = 2 * 1024 * 1024;

    public async Task<IReadOnlyList<CopilotDailyReportRecord>> GetLatestOrganizationReportAsync(
        CancellationToken cancellationToken = default
    )
    {
        var descriptor = await GetDescriptorAsync(cancellationToken);
        var records = new List<CopilotDailyReportRecord>();
        var identities = new HashSet<(string OrganizationId, LocalDate Day)>();
        var budget = new ResponseBudget();
        long declaredBytes = 0;

        foreach (var link in descriptor.DownloadLinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            using var response = await downloadHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength)
            {
                if (contentLength > MaxDownloadBytes - declaredBytes)
                {
                    throw new InvalidDataException("Copilot report downloads exceed the 50 MiB aggregate limit.");
                }
                declaredBytes += contentLength;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var limited = new BudgetedReadStream(stream, budget);
            var lineCount = await ReadDownloadAsync(limited, descriptor, records, identities, cancellationToken);
            if (lineCount == 0)
            {
                throw new InvalidDataException("Copilot report download is empty.");
            }
        }

        return records.AsReadOnly();
    }

    private static async Task<int> ReadDownloadAsync(
        Stream stream,
        Descriptor descriptor,
        List<CopilotDailyReportRecord> records,
        HashSet<(string OrganizationId, LocalDate Day)> identities,
        CancellationToken cancellationToken
    )
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true
        );
        var lineCount = 0;
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Each shard is an independent stream, so each may open with its own BOM.
                if (lineCount == 0 && line.StartsWith('\uFEFF'))
                {
                    line = line[1..];
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    throw new InvalidDataException("Copilot report contains a blank NDJSON record.");
                }
                lineCount++;
                ParseWrapper(line, descriptor, records, identities);
            }
            return lineCount;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Copilot report is not valid UTF-8.", exception);
        }
    }

    private async Task<Descriptor> GetDescriptorAsync(CancellationToken cancellationToken)
    {
        using var response = await descriptorHttp.GetAsync(
            $"/orgs/{Uri.EscapeDataString(organization)}/copilot/metrics/reports/organization-28-day/latest",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedDescriptorAsync(response.Content, cancellationToken);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Copilot report descriptor is invalid JSON.", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            RequireObject(root, "Copilot report descriptor");
            var start = ReadDate(root, "report_start_day");
            var end = ReadDate(root, "report_end_day");
            if (start.PlusDays(27) != end)
            {
                throw new InvalidDataException("Copilot report descriptor does not declare a 28-day window.");
            }
            if (
                !root.TryGetProperty("download_links", out var linksElement)
                || linksElement.ValueKind != JsonValueKind.Array
            )
            {
                throw new InvalidDataException("Copilot report descriptor has no download_links array.");
            }

            var links = new List<Uri>();
            var unique = new HashSet<Uri>();
            foreach (var element in linksElement.EnumerateArray())
            {
                if (
                    element.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(element.GetString(), UriKind.Absolute, out var link)
                    || !string.Equals(link.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrEmpty(link.UserInfo)
                    || !unique.Add(link)
                )
                {
                    throw new InvalidDataException("Copilot descriptor contains an invalid signed download link.");
                }
                links.Add(link);
            }
            if (links.Count == 0)
            {
                throw new InvalidDataException("Copilot descriptor contains no signed download links.");
            }
            return new Descriptor(start, end, links.AsReadOnly());
        }
    }

    private static void ParseWrapper(
        string rawJson,
        Descriptor descriptor,
        List<CopilotDailyReportRecord> records,
        HashSet<(string OrganizationId, LocalDate Day)> identities
    )
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Copilot report contains invalid NDJSON.", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            RequireObject(root, "Copilot report record");
            var start = ReadDate(root, "report_start_day");
            var end = ReadDate(root, "report_end_day");
            if (start != descriptor.Start || end != descriptor.End)
            {
                throw new InvalidDataException("Copilot report window does not match its descriptor.");
            }
            var organizationId = ReadNonBlankString(root, "organization_id");
            var observedAt = ReadOptionalUtcInstant(root, "created_at");
            if (!root.TryGetProperty("day_totals", out var totals) || totals.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Copilot report record has no day_totals array.");
            }

            foreach (var total in totals.EnumerateArray())
            {
                RequireObject(total, "Copilot daily total");
                var day = ReadDate(total, "day");
                if (day < start || day > end)
                {
                    throw new InvalidDataException("Copilot daily total falls outside its report window.");
                }
                var dailyOrganizationId = ReadNonBlankString(total, "organization_id");
                if (!string.Equals(organizationId, dailyOrganizationId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Copilot daily total organization does not match its report.");
                }
                if (!identities.Add((organizationId, day)))
                {
                    throw new InvalidDataException("Copilot report contains a duplicate organization/day identity.");
                }

                records.Add(
                    new CopilotDailyReportRecord(
                        day,
                        organizationId,
                        ReadNonnegativeInt32(total, "daily_active_users"),
                        ReadNonnegativeInt32(total, "weekly_active_users"),
                        ReadNonnegativeInt32(total, "monthly_active_users"),
                        ReadNonnegativeInt64(total, "user_initiated_interaction_count"),
                        ReadNonnegativeInt64(total, "code_generation_activity_count"),
                        ReadNonnegativeInt64(total, "code_acceptance_activity_count"),
                        PerDayEvidence(root, total),
                        observedAt
                    )
                );
            }
        }
    }

    private static LocalDate ReadDate(JsonElement parent, string name)
    {
        var text = ReadNonBlankString(parent, name);
        var parsed = LocalDatePattern.Iso.Parse(text);
        if (!parsed.Success || LocalDatePattern.Iso.Format(parsed.Value) != text)
        {
            throw new InvalidDataException($"Copilot report field '{name}' is not an ISO calendar date.");
        }
        return parsed.Value;
    }

    private static Instant? ReadOptionalUtcInstant(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Copilot report field '{name}' is not a UTC timestamp.");
        }
        var text = element.GetString()!;
        var parsed = InstantPattern.ExtendedIso.Parse(text);
        if (!parsed.Success)
        {
            throw new InvalidDataException($"Copilot report field '{name}' is not a UTC timestamp.");
        }
        return parsed.Value;
    }

    private static string ReadNonBlankString(JsonElement parent, string name)
    {
        if (
            !parent.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString())
        )
        {
            throw new InvalidDataException($"Copilot report field '{name}' is missing or invalid.");
        }
        return element.GetString()!;
    }

    private static int ReadNonnegativeInt32(JsonElement parent, string name)
    {
        if (
            !parent.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < 0
        )
        {
            throw new InvalidDataException($"Copilot report field '{name}' is not a nonnegative integer.");
        }
        return value;
    }

    private static long ReadNonnegativeInt64(JsonElement parent, string name)
    {
        if (
            !parent.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var value)
            || value < 0
        )
        {
            throw new InvalidDataException($"Copilot report field '{name}' is not a nonnegative integer.");
        }
        return value;
    }

    private static void RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{description} must be a JSON object.");
        }
    }

    private static string PerDayEvidence(JsonElement wrapper, JsonElement dayTotal)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in wrapper.EnumerateObject())
            {
                if (property.NameEquals("day_totals"))
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteStartArray();
                    dayTotal.WriteTo(writer);
                    writer.WriteEndArray();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static async Task<byte[]> ReadBoundedDescriptorAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
        if (content.Headers.ContentLength > MaxDescriptorBytes)
        {
            throw new InvalidDataException("Copilot report descriptor exceeds the 2 MiB size limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return destination.ToArray();
                }
                if (destination.Length + read > MaxDescriptorBytes)
                {
                    throw new InvalidDataException("Copilot report descriptor exceeds the 2 MiB size limit.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record Descriptor(LocalDate Start, LocalDate End, IReadOnlyList<Uri> DownloadLinks);

    private sealed class ResponseBudget
    {
        private long _bytes;

        public void Add(int bytes)
        {
            if (bytes > MaxDownloadBytes - _bytes)
            {
                throw new InvalidDataException("Copilot report downloads exceed the 50 MiB aggregate limit.");
            }
            _bytes += bytes;
        }
    }

    private sealed class BudgetedReadStream(Stream inner, ResponseBudget budget) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            budget.Add(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            budget.Add(read);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
