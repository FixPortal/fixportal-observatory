using System.Globalization;
using AiObservatory.Data.Audit;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Api.Audit;

public sealed class AuditSelectedInvocationOptions
{
    public const string SectionName = "AuditSelectedInvocation";
    public bool Enabled { get; set; }
    public string? RunId { get; set; }
    public string? FullScopeDigest { get; set; }
    public string? ExpectedRevision { get; set; }
    public string? InvocationId { get; set; }
    public string? ExpiresAtUtc { get; set; }

    public SelectedInvocationAuthority? ToAuthority()
    {
        var snapshot = new AuditSelectedInvocationOptions
        {
            Enabled = Enabled,
            RunId = RunId,
            FullScopeDigest = FullScopeDigest,
            ExpectedRevision = ExpectedRevision,
            InvocationId = InvocationId,
            ExpiresAtUtc = ExpiresAtUtc,
        };
        var validation = new AuditSelectedInvocationOptionsValidator().Validate(null, snapshot);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                SectionName,
                typeof(AuditSelectedInvocationOptions),
                validation.Failures
            );
        }
        if (snapshot.IsUnbound)
        {
            return null;
        }
        AuditSelectedInvocationOptionsValidator.TryExpiry(snapshot.ExpiresAtUtc, out var expiry);
        return new(
            snapshot.Enabled,
            new SelectedRunBinding(
                Guid.Parse(snapshot.RunId!),
                snapshot.FullScopeDigest!,
                long.Parse(snapshot.ExpectedRevision!, NumberStyles.None, CultureInfo.InvariantCulture)
            ),
            Guid.Parse(snapshot.InvocationId!),
            expiry
        );
    }

    internal bool IsUnbound =>
        RunId == null
        && FullScopeDigest == null
        && ExpectedRevision == null
        && InvocationId == null
        && ExpiresAtUtc == null;
}

public sealed class AuditSelectedInvocationOptionsValidator : IValidateOptions<AuditSelectedInvocationOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditSelectedInvocationOptions options)
    {
        if (!options.Enabled && options.IsUnbound)
        {
            return ValidateOptionsResult.Success;
        }
        var errors = new List<string>();
        if (!Guid.TryParse(options.RunId, out var runId) || runId == Guid.Empty)
        {
            errors.Add(nameof(options.RunId));
        }
        if (!Guid.TryParse(options.InvocationId, out var invocationId) || invocationId == Guid.Empty)
        {
            errors.Add(nameof(options.InvocationId));
        }
        if (
            options.FullScopeDigest is not { Length: 64 }
            || !options.FullScopeDigest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        )
        {
            errors.Add(nameof(options.FullScopeDigest));
        }
        if (
            !long.TryParse(options.ExpectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision < 0
        )
        {
            errors.Add(nameof(options.ExpectedRevision));
        }
        if (!TryExpiry(options.ExpiresAtUtc, out _))
        {
            errors.Add(nameof(options.ExpiresAtUtc));
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    internal static bool TryExpiry(string? text, out Instant expiry)
    {
        expiry = default;
        if (
            text == null
            || !OffsetDateTimePattern.ExtendedIso.Parse(text).TryGetValue(default, out var parsed)
            || parsed.Offset != Offset.Zero
        )
        {
            return false;
        }
        expiry = parsed.ToInstant();
        return true;
    }
}
