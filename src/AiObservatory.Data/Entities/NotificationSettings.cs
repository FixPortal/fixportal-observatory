using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// Singleton row (no per-user/per-tenant scoping exists anywhere else in this app) holding
/// where budget-threshold alerts are delivered. SMTP server credentials are NOT here -- they
/// stay env-var (infra config, not a per-preference setting).
/// </summary>
public sealed class NotificationSettings
{
    // Fixed well-known id: this is a singleton row (see class doc), so a stable PK
    // avoids the entity minting a fresh random one every time it is constructed, and
    // every read/write filters explicitly on this id rather than trusting "there's only
    // ever one row" -- defense in depth in case a stray extra row is ever created.
    public static readonly Guid SingletonId = Guid.Parse("33333333-3333-3333-3333-333333333301");

    public Guid Id { get; init; } = SingletonId;
    public string? AlertEmailTo { get; set; }

    // Bearer credential: possession alone permits posting to the alerts channel. Encrypted at
    // rest by the EF value converter in AiObservatoryDbContext when the
    // SLACK_WEBHOOK_PROTECTION_KEY env var is set (Security/SlackWebhookProtector); without the
    // key it passes through as plaintext so pre-existing deployments keep working.
    public string? SlackWebhookUrl { get; set; }
    public Instant UpdatedAt { get; set; }
}
