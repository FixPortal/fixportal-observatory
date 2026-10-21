using System.Text.Json;
using AiObservatory.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using NodaTime;
using Npgsql;

namespace AiObservatory.Api.Endpoints;

public static class NotificationSettingsEndpoints
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapNotificationSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/notification-settings",
            async (AiObservatoryDbContext db, CancellationToken ct) =>
            {
                var settings = await db
                    .NotificationSettings.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == Data.Entities.NotificationSettings.SingletonId, ct);
                return Results.Ok(ToResponse(settings?.AlertEmailTo, settings?.SlackWebhookUrl));
            }
        );

        // Body is bound as raw JsonElement, not a fixed record: a field OMITTED from the JSON
        // body must leave that setting unchanged, while a field present as null or "" clears
        // it. A record's default binding cannot distinguish "omitted" from "present but null"
        // -- both collapse to the same C# null -- so presence is checked with TryGetProperty
        // instead. This distinction is load-bearing: the UI can only ever show a MASKED value
        // for an already-configured field, so it cannot resend that field's real value when
        // saving an edit to the OTHER field -- if the endpoint required both fields on every
        // write, editing the email would have no valid value to send for Slack (and vice
        // versa) without either corrupting or silently clearing it.
        app.MapPut(
            "/notification-settings",
            async (JsonElement body, AiObservatoryDbContext db, IClock clock, CancellationToken ct) =>
            {
                var validationError = ValidatePutBody(body);
                if (validationError is not null)
                {
                    return Results.BadRequest(validationError);
                }

                var settings = await UpsertSettingsAsync(db, body, clock, ct);

                return Results.Ok(ToResponse(settings.AlertEmailTo, settings.SlackWebhookUrl));
            }
        );

        return app;
    }

    // TryGetProperty throws InvalidOperationException on anything but a JSON object (an array,
    // string, number, or null body) -- reject that up front so malformed input returns 400 like
    // every other guard below, not a 500.
    private static string? ValidatePutBody(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return "body must be a JSON object";
        }

        if (
            body.TryGetProperty("alertEmailTo", out var emailKindProp)
            && emailKindProp.ValueKind != JsonValueKind.String
            && emailKindProp.ValueKind != JsonValueKind.Null
        )
        {
            return "alertEmailTo must be a string or null";
        }

        if (
            body.TryGetProperty("slackWebhookUrl", out var slackKindProp)
            && slackKindProp.ValueKind != JsonValueKind.String
            && slackKindProp.ValueKind != JsonValueKind.Null
        )
        {
            return "slackWebhookUrl must be a string or null";
        }

        if (
            body.TryGetProperty("alertEmailTo", out var emailProp)
            && emailProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(emailProp.GetString())
            && (emailProp.GetString()!.Length > 320 || !IsValidEmail(emailProp.GetString()!))
        )
        {
            return "alertEmailTo is not a valid email address";
        }

        if (
            body.TryGetProperty("slackWebhookUrl", out var slackProp)
            && slackProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(slackProp.GetString())
            && (slackProp.GetString()!.Length > 2048 || !IsValidSlackWebhookUrl(slackProp.GetString()!))
        )
        {
            return "slackWebhookUrl must start with https://hooks.slack.com/";
        }

        return null;
    }

    private static async Task<Data.Entities.NotificationSettings> UpsertSettingsAsync(
        AiObservatoryDbContext db,
        JsonElement body,
        IClock clock,
        CancellationToken ct
    )
    {
        var settings = await db.NotificationSettings.FirstOrDefaultAsync(
            s => s.Id == Data.Entities.NotificationSettings.SingletonId,
            ct
        );
        var inserting = settings is null;
        if (settings is null)
        {
            settings = new Data.Entities.NotificationSettings();
            db.NotificationSettings.Add(settings);
        }

        ApplyFields(settings, body, clock);

        if (!inserting)
        {
            await db.SaveChangesAsync(ct);
            return settings;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost the insert race to a concurrent first-time PUT. Reload the winner's row and
            // reapply THIS request's edits on top of it -- a bounded, single reload-and-reapply,
            // not a general retry loop.
            db.Entry(settings).State = EntityState.Detached;
            settings = await db.NotificationSettings.SingleAsync(
                s => s.Id == Data.Entities.NotificationSettings.SingletonId,
                ct
            );
            ApplyFields(settings, body, clock);
            await db.SaveChangesAsync(ct);
        }

        return settings;
    }

    private static void ApplyFields(Data.Entities.NotificationSettings settings, JsonElement body, IClock clock)
    {
        if (body.TryGetProperty("alertEmailTo", out var emailField))
        {
            var value = emailField.ValueKind == JsonValueKind.Null ? null : emailField.GetString();
            settings.AlertEmailTo = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        if (body.TryGetProperty("slackWebhookUrl", out var slackField))
        {
            var value = slackField.ValueKind == JsonValueKind.Null ? null : slackField.GetString();
            settings.SlackWebhookUrl = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        settings.UpdatedAt = clock.GetCurrentInstant();
    }

    private static object ToResponse(string? email, string? slackWebhookUrl) =>
        new
        {
            emailConfigured = !string.IsNullOrEmpty(email),
            emailMasked = NotificationMasking.MaskEmail(email),
            slackConfigured = !string.IsNullOrEmpty(slackWebhookUrl),
            slackMasked = NotificationMasking.MaskWebhookUrl(slackWebhookUrl),
        };

    // Internal so the Program.cs startup seed can apply the same validation the PUT endpoint
    // uses (S4: an invalid BUDGET_ALERT_EMAIL_TO must not reach the database unvalidated).
    internal static bool IsValidEmail(string email)
    {
        // MimeKit's default parsing is lenient: MailboxAddress.Parse("not-an-email") does NOT
        // throw -- it happily accepts a bare word as a local-part-only mailbox with no domain.
        // Requiring an '@' and a non-empty parsed domain closes that gap.
        if (!email.Contains('@'))
        {
            return false;
        }

        try
        {
            var mailbox = MailboxAddress.Parse(email);
            return !string.IsNullOrEmpty(mailbox.Domain);
        }
        catch (ParseException)
        {
            return false;
        }
    }

    // A plain StartsWith("https://hooks.slack.com/") check is bypassable with URL userinfo:
    // "https://hooks.slack.com@attacker.example/services/x" starts with that exact prefix
    // but actually targets attacker.example (the text before '@' is credentials, not host).
    // Parsing the URI and checking scheme/userinfo/host/port/path explicitly closes that.
    private static bool IsValidSlackWebhookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(uri.Host, "hooks.slack.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && uri.AbsolutePath.StartsWith("/services/", StringComparison.Ordinal);
}

public static class NotificationMasking
{
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        var local = email[..at];
        var domain = email[at..];
        var visible = local.Length <= 2 ? local : local[..2];
        return $"{visible}***{domain}";
    }

    public static string? MaskWebhookUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : "https://hooks.slack.com/services/***";
}
