# Notification Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an admin set/change/remove the budget-alert email recipient and add a Slack
incoming-webhook channel from the app UI, instead of only via an env var nobody can reach.

**Architecture:** A new singleton `NotificationSettings` DB row replaces the
`BUDGET_ALERT_EMAIL_TO` env var as the source of the alert email recipient (SMTP server
credentials stay env-var). A new `SlackAlertNotifier` posts to a stored incoming-webhook URL. A
new `CompositeAlertNotifier` becomes the `IAlertNotifier` registered in DI, fanning out to both
channels; email keeps today's exact retry/lease semantics (its failure propagates), Slack is
best-effort (its failure is logged, never retried, never blocks email). A new
`NotificationSettingsEndpoints.cs` (GET/PUT) exposes masked read + partial-update write, replacing
the existing `/api/budget-rules/email-status` endpoint. `BudgetRulesPanel.tsx` gets a small new
section for both fields.

**Tech Stack:** ASP.NET Core minimal APIs, EF Core (Npgsql), MailKit (existing), `HttpClient`
(new, for Slack), React + TanStack Query, xUnit v3 + NSubstitute + AwesomeAssertions.

**Spec:** `docs/superpowers/specs/2026-08-30-notification-settings-design.md`

## Global Constraints

- SMTP host/port/user/password/from stay env-var (`BUDGET_ALERT_SMTP_*`, `BUDGET_ALERT_EMAIL_FROM`)
  — never move to the DB.
- One email recipient + one Slack webhook URL, both optional, independently on/off — no lists,
  no multi-recipient support.
- `GET /api/notification-settings` never returns a real secret value, only a masked
  representation and a `configured` boolean. Email mask: first 2 chars of the local part +
  `***@domain`. Slack mask: always the fixed string `https://hooks.slack.com/services/***`
  (never any part of the real path).
- `PUT /api/notification-settings` is a **partial update**: a field omitted from the JSON body
  leaves that setting unchanged; a field present as JSON `null` or `""` clears it. This is
  required, not a nice-to-have — the UI can edit one field without being able to resend the
  other's real (unmasked) value.
- `PUT` is admin-key gated (the existing default for every non-GET route under `/api`). `GET` is
  readonly-key readable (the existing default for every GET route).
- Slack delivery is best-effort: its failure is logged only, never retried, and never prevents
  email from being attempted or from reporting its own success/failure to
  `BudgetAlertService` exactly as it does today.
- `GET /api/budget-rules/email-status` is deleted in the same change that adds the replacement —
  not kept alongside it.
- Every destructive UI action (removing a configured email or webhook) uses the two-click
  confirm/cancel pattern already established in `SpendLedgerTable.tsx` and
  `AdversarialReviewPanel.tsx` this session.
- Hidden entirely for `isReadonly` viewers, matching every other admin write-affordance in this
  app.

---

## Task 1: `NotificationSettings` entity + migration

**Files:**
- Create: `src/AiObservatory.Data/Entities/NotificationSettings.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Create (generated): `src/AiObservatory.Data/Migrations/<timestamp>_AddNotificationSettings.cs`
  and its `.Designer.cs`

**Interfaces:**
- Produces: `NotificationSettings` entity (`Id: Guid`, `AlertEmailTo: string?`,
  `SlackWebhookUrl: string?`, `UpdatedAt: Instant`), `AiObservatoryDbContext.NotificationSettings`
  `DbSet<NotificationSettings>`.

- [ ] **Step 1: Create the entity**

```csharp
// src/AiObservatory.Data/Entities/NotificationSettings.cs
using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// Singleton row (no per-user/per-tenant scoping exists anywhere else in this app) holding
/// where budget-threshold alerts are delivered. SMTP server credentials are NOT here -- they
/// stay env-var (infra config, not a per-preference setting).
/// </summary>
public sealed class NotificationSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? AlertEmailTo { get; set; }
    public string? SlackWebhookUrl { get; set; }
    public Instant UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Register the DbSet and column config**

In `src/AiObservatory.Data/AiObservatoryDbContext.cs`, add the DbSet next to `BudgetAlertClaims`
(line ~37):

```csharp
    public DbSet<BudgetAlertClaim> BudgetAlertClaims => Set<BudgetAlertClaim>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
```

Add a config block in `OnModelCreating`, after the `BudgetAlertClaim` block (~line 442):

```csharp
        modelBuilder.Entity<NotificationSettings>(b =>
        {
            b.Property(s => s.AlertEmailTo).HasMaxLength(320); // RFC 5321 max
            b.Property(s => s.SlackWebhookUrl).HasMaxLength(2048);
        });
```

- [ ] **Step 3: Generate the migration**

Run from the repo root:

```bash
dotnet ef migrations add AddNotificationSettings --project src/AiObservatory.Data --startup-project src/AiObservatory.Api
```

Expected: two new files under `src/AiObservatory.Data/Migrations/` —
`<timestamp>_AddNotificationSettings.cs` (creates the `NotificationSettings` table with columns
`Id uuid PK`, `AlertEmailTo varchar(320) NULL`, `SlackWebhookUrl varchar(2048) NULL`,
`UpdatedAt timestamptz NOT NULL`) and its `.Designer.cs`. Also confirm
`AiObservatoryDbContextModelSnapshot.cs` picked up the new entity (git diff should show it).

- [ ] **Step 4: Build and verify the migration applies**

```bash
dotnet build
```

Expected: 0 errors. (The migration applies automatically on next API startup via
`db.Database.MigrateAsync()` in `Program.cs:129` — no manual apply step needed here; Task 2's
integration tests will be the first thing to actually run it against a real database.)

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Data/Entities/NotificationSettings.cs src/AiObservatory.Data/AiObservatoryDbContext.cs src/AiObservatory.Data/Migrations/
git commit -m "feat: add NotificationSettings entity and migration"
```

---

## Task 2: Repository read method for the settings row

**Files:**
- Modify: `src/AiObservatory.Data/Repositories/IUsageRepository.cs`
- Modify: `src/AiObservatory.Data/Repositories/UsageRepository.cs`
- Test: `tests/AiObservatory.Data.Tests/Repositories/NotificationSettingsRepositoryTests.cs`

**Interfaces:**
- Consumes: `NotificationSettings` entity from Task 1.
- Produces: `IUsageRepository.GetNotificationSettingsAsync(CancellationToken ct = default): Task<NotificationSettings?>`
  — used by `EmailAlertNotifier` and `SlackAlertNotifier` in Tasks 4-5. Returns `null` when no
  row exists yet (nothing has ever been configured).

This mirrors the existing split already in this codebase for `BudgetRule`:
`BudgetRulesEndpoints.cs` reads/writes `db.BudgetRules` directly (CRUD-style admin endpoint),
while `BudgetAlertService` reads via `IUsageRepository.GetBudgetRulesAsync` (business-logic
service, needs a testable seam). `NotificationSettings` follows the exact same split —
`NotificationSettingsEndpoints.cs` (Task 3) will use `AiObservatoryDbContext` directly, while
`EmailAlertNotifier`/`SlackAlertNotifier` (Tasks 4-5) use this repository method so they stay
unit-testable with `Substitute.For<IUsageRepository>()`, matching `BudgetAlertServiceTests.cs`'s
existing pattern instead of forcing them into integration tests.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AiObservatory.Data.Tests/Repositories/NotificationSettingsRepositoryTests.cs
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

[Trait("Category", "Integration")]
public class NotificationSettingsRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IUsageRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = $"aiobs_test_notification_settings_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new UsageRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ctx is not null && _connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
            {
                await _ctx.Database.EnsureDeletedAsync();
            }
        }
        finally
        {
            if (_ctx is not null)
            {
                await _ctx.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task GetNotificationSettings_returns_null_when_no_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _repo.GetNotificationSettingsAsync(ct)).Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationSettings_returns_the_singleton_row()
    {
        var ct = TestContext.Current.CancellationToken;
        _ctx.NotificationSettings.Add(
            new NotificationSettings
            {
                AlertEmailTo = "alerts@example.com",
                SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
            }
        );
        await _ctx.SaveChangesAsync(ct);

        var settings = await _repo.GetNotificationSettingsAsync(ct);

        settings.Should().NotBeNull();
        settings!.AlertEmailTo.Should().Be("alerts@example.com");
        settings.SlackWebhookUrl.Should().Be("https://hooks.slack.com/services/T0/B0/xyz");
    }
}
```

- [ ] **Step 2: Run it to verify it fails to compile (method doesn't exist yet)**

Build the test project — expect a compile error: `'IUsageRepository' does not contain a
definition for 'GetNotificationSettingsAsync'`.

```bash
dotnet build tests/AiObservatory.Data.Tests
```

- [ ] **Step 3: Add the interface method**

In `src/AiObservatory.Data/Repositories/IUsageRepository.cs`, add near
`GetUnacknowledgedInsightsAsync` (~line 174):

```csharp
    Task<Entities.NotificationSettings?> GetNotificationSettingsAsync(CancellationToken ct = default);
```

(If `AiObservatory.Data.Entities` is already `using`'d at the top of the file, drop the
`Entities.` prefix and write `NotificationSettings?` directly — check the file's existing
`using` block before choosing.)

- [ ] **Step 4: Implement it**

In `src/AiObservatory.Data/Repositories/UsageRepository.cs`, add near `GetBudgetRulesAsync`
(~line 542):

```csharp
    public async Task<NotificationSettings?> GetNotificationSettingsAsync(CancellationToken ct = default)
    {
        return await ctx.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
    }
```

- [ ] **Step 5: Build and run the test**

```bash
dotnet build
dotnet test tests/AiObservatory.Data.Tests --filter "FullyQualifiedName~NotificationSettingsRepositoryTests"
```

Expected: both tests pass (needs a local Postgres — see `TEST_DB_CONNECTION`, same requirement
as every other test in this file's sibling suite).

- [ ] **Step 6: Commit**

```bash
git add src/AiObservatory.Data/Repositories/IUsageRepository.cs src/AiObservatory.Data/Repositories/UsageRepository.cs tests/AiObservatory.Data.Tests/Repositories/NotificationSettingsRepositoryTests.cs
git commit -m "feat: add GetNotificationSettingsAsync to IUsageRepository"
```

---

## Task 3: `NotificationSettingsEndpoints` (GET masked read, PUT partial update)

**Files:**
- Create: `src/AiObservatory.Api/Endpoints/NotificationSettingsEndpoints.cs`
- Modify: `src/AiObservatory.Api/Endpoints/BudgetRulesEndpoints.cs` (remove `/email-status`)
- Modify: `src/AiObservatory.Api/Program.cs` (map the new endpoints)
- Test: `tests/AiObservatory.Api.Tests/Services/NotificationMaskingTests.cs`
- Test: `tests/AiObservatory.Api.IntegrationTests/NotificationSettingsEndpointsWafTests.cs`

**Interfaces:**
- Consumes: `AiObservatoryDbContext.NotificationSettings` (Task 1).
- Produces: `NotificationMasking.MaskEmail(string? email): string?`,
  `NotificationMasking.MaskWebhookUrl(string? url): string?` (pure, unit-tested standalone) and
  the two HTTP routes below, used by the frontend in Task 6.

Routes:
- `GET /api/notification-settings` → `200 { emailConfigured: bool, emailMasked: string|null, slackConfigured: bool, slackMasked: string|null }`
- `PUT /api/notification-settings` → body is a raw JSON object (bound as `JsonElement`, not a
  fixed record type — see the partial-update note in Global Constraints for why: STJ's default
  record binding collapses "field omitted" and "field explicitly null" to the same C# `null`,
  and this endpoint must tell them apart). Returns the same shape as GET on success (`200`), or
  `400` with a plain-string body on validation failure.

- [ ] **Step 1: Write the failing unit test for masking**

```csharp
// tests/AiObservatory.Api.Tests/Services/NotificationMaskingTests.cs
using AiObservatory.Api.Endpoints;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests.Services;

public class NotificationMaskingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("ch@fixportal.org", "ch***@fixportal.org")]
    [InlineData("c@fixportal.org", "c***@fixportal.org")]
    [InlineData("christopher@fixportal.org", "ch***@fixportal.org")]
    public void MaskEmail_shows_at_most_the_first_two_local_part_characters(string? input, string? expected)
    {
        NotificationMasking.MaskEmail(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("https://hooks.slack.com/services/T0/B0/verysecret", "https://hooks.slack.com/services/***")]
    public void MaskWebhookUrl_never_reveals_the_real_path(string? input, string? expected)
    {
        NotificationMasking.MaskWebhookUrl(input).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile**

```bash
dotnet build tests/AiObservatory.Api.Tests
```

Expected: compile error, `NotificationMasking` doesn't exist yet.

- [ ] **Step 3: Write the endpoints file (masking + validation + both routes)**

```csharp
// src/AiObservatory.Api/Endpoints/NotificationSettingsEndpoints.cs
using System.Text.Json;
using AiObservatory.Data;
using MimeKit;
using Microsoft.EntityFrameworkCore;
using NodaTime;

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
                var settings = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
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
                if (
                    body.TryGetProperty("alertEmailTo", out var emailProp)
                    && emailProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(emailProp.GetString())
                    && !IsValidEmail(emailProp.GetString()!)
                )
                {
                    return Results.BadRequest("alertEmailTo is not a valid email address");
                }

                if (
                    body.TryGetProperty("slackWebhookUrl", out var slackProp)
                    && slackProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(slackProp.GetString())
                    && !IsValidSlackWebhookUrl(slackProp.GetString()!)
                )
                {
                    return Results.BadRequest("slackWebhookUrl must start with https://hooks.slack.com/");
                }

                var settings = await db.NotificationSettings.FirstOrDefaultAsync(ct);
                if (settings is null)
                {
                    settings = new Data.Entities.NotificationSettings();
                    db.NotificationSettings.Add(settings);
                }

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
                await db.SaveChangesAsync(ct);

                return Results.Ok(ToResponse(settings.AlertEmailTo, settings.SlackWebhookUrl));
            }
        );

        return app;
    }

    private static object ToResponse(string? email, string? slackWebhookUrl) =>
        new
        {
            emailConfigured = !string.IsNullOrEmpty(email),
            emailMasked = NotificationMasking.MaskEmail(email),
            slackConfigured = !string.IsNullOrEmpty(slackWebhookUrl),
            slackMasked = NotificationMasking.MaskWebhookUrl(slackWebhookUrl),
        };

    private static bool IsValidEmail(string email)
    {
        try
        {
            _ = MailboxAddress.Parse(email);
            return true;
        }
        catch (ParseException)
        {
            return false;
        }
    }

    private static bool IsValidSlackWebhookUrl(string url) =>
        url.StartsWith("https://hooks.slack.com/", StringComparison.Ordinal);
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
```

- [ ] **Step 4: Run the masking test, verify it passes**

```bash
dotnet test tests/AiObservatory.Api.Tests --filter "FullyQualifiedName~NotificationMaskingTests"
```

Expected: all pass.

- [ ] **Step 5: Remove the superseded email-status endpoint**

In `src/AiObservatory.Api/Endpoints/BudgetRulesEndpoints.cs`, delete lines 19-23 (the
`app.MapGet("/budget-rules/email-status", ...)` block) entirely — no replacement stays in this
file, the new endpoint fully supersedes it.

- [ ] **Step 6: Map the new endpoints in Program.cs**

In `src/AiObservatory.Api/Program.cs`, add after line 368 (`api.MapBudgetRulesEndpoints();`):

```csharp
api.MapNotificationSettingsEndpoints();
```

- [ ] **Step 7: Write the integration test file**

```csharp
// tests/AiObservatory.Api.IntegrationTests/NotificationSettingsEndpointsWafTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiObservatory.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class NotificationSettingsEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Fact]
    public async Task Get_WhenNothingConfigured_ReturnsAllUnconfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateReadonlyClient();

        var response = await client.GetFromJsonAsync<JsonElement>("/api/notification-settings", ct);

        response.GetProperty("emailConfigured").GetBoolean().Should().BeFalse();
        response.GetProperty("emailMasked").ValueKind.Should().Be(JsonValueKind.Null);
        response.GetProperty("slackConfigured").GetBoolean().Should().BeFalse();
        response.GetProperty("slackMasked").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Put_SetsEmailWithoutTouchingSlack_AndMasksTheResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { slackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz" },
            ct
        );

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "chris@fixportal.org" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("emailConfigured").GetBoolean().Should().BeTrue();
        body.GetProperty("emailMasked").GetString().Should().Be("ch***@fixportal.org");
        // The earlier PUT's Slack value must survive an edit that only touched email.
        body.GetProperty("slackConfigured").GetBoolean().Should().BeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var row = await db.NotificationSettings.SingleAsync(ct);
        row.AlertEmailTo.Should().Be("chris@fixportal.org");
        row.SlackWebhookUrl.Should().Be("https://hooks.slack.com/services/T0/B0/xyz");
    }

    [Fact]
    public async Task Put_WithNullEmail_ClearsItWithoutTouchingSlack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();
        await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "chris@fixportal.org", slackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz" },
            ct
        );

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = (string?)null },
            ct
        );

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("emailConfigured").GetBoolean().Should().BeFalse();
        body.GetProperty("slackConfigured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Put_WithMalformedEmail_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "not-an-email" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_WithNonSlackWebhookUrl_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { slackWebhookUrl = "https://evil.example.com/steal" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_WithoutAdminKey_ReturnsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateReadonlyClient();

        var response = await client.PutAsJsonAsync(
            "/api/notification-settings",
            new { alertEmailTo = "chris@fixportal.org" },
            ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

Check `AiObservatoryApiFactory.cs` for the exact `CreateAdminClient`/`CreateReadonlyClient`
method names and any required `using AwesomeAssertions;` before running — copy its existing
`using` block style from `BudgetRulesEndpointsWafTests.cs` if these names differ.

- [ ] **Step 8: Run the full test project, verify everything passes**

```bash
dotnet build
dotnet test tests/AiObservatory.Api.Tests
dotnet test tests/AiObservatory.Api.IntegrationTests --filter "FullyQualifiedName~NotificationSettingsEndpointsWafTests"
```

Expected: all green. The integration tests need a local Postgres (`TEST_DB_CONNECTION`), same
as every other file in that project.

- [ ] **Step 9: Commit**

```bash
git add src/AiObservatory.Api/Endpoints/NotificationSettingsEndpoints.cs src/AiObservatory.Api/Endpoints/BudgetRulesEndpoints.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.Tests/Services/NotificationMaskingTests.cs tests/AiObservatory.Api.IntegrationTests/NotificationSettingsEndpointsWafTests.cs
git commit -m "feat: add notification-settings endpoints, remove superseded email-status"
```

---

## Task 4: `EmailAlertNotifier` reads the recipient from the DB

**Files:**
- Modify: `src/AiObservatory.Api/Services/EmailAlertNotifier.cs`
- Modify: `tests/AiObservatory.Api.Tests/Services/EmailAlertNotifierTests.cs`

**Interfaces:**
- Consumes: `IUsageRepository.GetNotificationSettingsAsync` (Task 2).
- Produces: `EmailAlertNotifier(ISmtpClient, IConfiguration, IUsageRepository) : IAlertNotifier`
  (constructor signature changes — the `to` address source moves from `IConfiguration` to the
  repository; everything else about this class is unchanged).

- [ ] **Step 1: Update the existing "configured" test to use the repository instead of config**

Replace the whole file's content:

```csharp
// tests/AiObservatory.Api.Tests/Services/EmailAlertNotifierTests.cs
using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class EmailAlertNotifierTests
{
    private static BudgetAlertPayload MakePayload(string provider = "Anthropic") =>
        new(
            provider,
            "Daily",
            10m,
            15m,
            DateTimeOffset.UtcNow,
            "budget-alert-10000000000000000000000000000001@observatory.fixportal.com"
        );

    [Fact]
    public async Task NotifyAsync_is_noop_when_no_settings_row_exists()
    {
        var smtp = Substitute.For<ISmtpClient>();
        var config = new ConfigurationBuilder().Build();
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>()).Returns((NotificationSettings?)null);

        var sut = new EmailAlertNotifier(smtp, config, repo);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await smtp.DidNotReceive()
            .ConnectAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<SecureSocketOptions>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task NotifyAsync_is_noop_when_recipient_is_unset_on_the_row()
    {
        var smtp = Substitute.For<ISmtpClient>();
        var config = new ConfigurationBuilder().Build();
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new NotificationSettings { AlertEmailTo = null, UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0) });

        var sut = new EmailAlertNotifier(smtp, config, repo);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await smtp.DidNotReceive()
            .ConnectAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<SecureSocketOptions>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task NotifyAsync_connects_authenticates_and_sends_when_configured()
    {
        var smtp = Substitute.For<ISmtpClient>();
        smtp.IsConnected.Returns(true);
        MimeMessage? sent = null;
        smtp.When(x => x.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>()))
            .Do(x => sent = x.Arg<MimeMessage>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["BUDGET_ALERT_EMAIL_FROM"] = "obs@example.com",
                    ["BUDGET_ALERT_SMTP_HOST"] = "smtp.example.com",
                    ["BUDGET_ALERT_SMTP_USER"] = "obs@example.com",
                    ["BUDGET_ALERT_SMTP_PASS"] = "secret",
                }
            )
            .Build();
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    AlertEmailTo = "alerts@example.com",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );

        var sut = new EmailAlertNotifier(smtp, config, repo);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await smtp.Received(1)
            .ConnectAsync("smtp.example.com", 587, SecureSocketOptions.StartTls, Arg.Any<CancellationToken>());
        await smtp.Received(1).AuthenticateAsync("obs@example.com", "secret", Arg.Any<CancellationToken>());
        await smtp.Received(1).DisconnectAsync(true, Arg.Any<CancellationToken>());

        sent.Should().NotBeNull();
        sent.MessageId.Should().Be(MakePayload().MessageId);
        sent.Subject.Should().Contain("Anthropic").And.Contain("billed spend").And.Contain("£10.00");
        sent.To.ToString().Should().Contain("alerts@example.com");
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile (constructor signature mismatch)**

```bash
dotnet build tests/AiObservatory.Api.Tests
```

Expected: compile error, `EmailAlertNotifier` has no 3-arg constructor yet.

- [ ] **Step 3: Update the notifier**

Replace `src/AiObservatory.Api/Services/EmailAlertNotifier.cs` in full:

```csharp
using AiObservatory.Data.Repositories;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AiObservatory.Api.Services;

public sealed class EmailAlertNotifier(ISmtpClient smtpClient, IConfiguration config, IUsageRepository repository)
    : IAlertNotifier
{
    public async Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var settings = await repository.GetNotificationSettingsAsync(ct);
        var to = settings?.AlertEmailTo;
        if (string.IsNullOrEmpty(to))
        {
            return;
        }

        var host = config["BUDGET_ALERT_SMTP_HOST"] ?? "smtp.office365.com";
        var port = int.TryParse(config["BUDGET_ALERT_SMTP_PORT"], out var p) ? p : 587;
        var user = config["BUDGET_ALERT_SMTP_USER"] ?? string.Empty;
        var pass = config["BUDGET_ALERT_SMTP_PASS"] ?? string.Empty;
        var from = config["BUDGET_ALERT_EMAIL_FROM"] ?? user;

        try
        {
            await smtpClient.ConnectAsync(host, port, SecureSocketOptions.StartTls, ct);
            if (!string.IsNullOrEmpty(user))
            {
                await smtpClient.AuthenticateAsync(user, pass, ct);
            }

            using var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            message.To.Add(MailboxAddress.Parse(to));
            message.MessageId = payload.MessageId;
            message.Subject =
                $"Budget alert: {payload.Provider} {payload.Period} billed spend exceeded £{payload.ThresholdGbp:F2}";
            message.Body = new TextPart("plain")
            {
                Text =
                    $"Total {payload.Period.ToLower()} billed spend for {payload.Provider} reached £{payload.ActualSpendGbp:F2}, "
                    + $"exceeding your £{payload.ThresholdGbp:F2} threshold.\n\nTriggered at: {payload.TriggeredAt:u}",
            };

            await smtpClient.SendAsync(message, ct);
        }
        finally
        {
            if (smtpClient.IsConnected)
            {
                await smtpClient.DisconnectAsync(true, ct);
            }
        }
    }
}
```

(Only the constructor signature and the `to` lookup changed — the SMTP send logic below it is
unchanged from before.)

- [ ] **Step 4: Build and run**

```bash
dotnet build
dotnet test tests/AiObservatory.Api.Tests --filter "FullyQualifiedName~EmailAlertNotifierTests"
```

Expected: all pass. This will also break `Program.cs`'s DI registration (Task 5 fixes that) —
if you build the whole solution now you'll see a DI-resolution runtime issue only if the app
actually starts; a plain `dotnet build` still succeeds because `IUsageRepository` was already a
registered service.

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Api/Services/EmailAlertNotifier.cs tests/AiObservatory.Api.Tests/Services/EmailAlertNotifierTests.cs
git commit -m "feat: EmailAlertNotifier reads recipient from NotificationSettings"
```

---

## Task 5: `SlackAlertNotifier` + `CompositeAlertNotifier` + DI wiring

**Files:**
- Create: `src/AiObservatory.Api/Services/SlackAlertNotifier.cs`
- Create: `src/AiObservatory.Api/Services/CompositeAlertNotifier.cs`
- Modify: `src/AiObservatory.Api/Program.cs`
- Test: `tests/AiObservatory.Api.Tests/Services/SlackAlertNotifierTests.cs`
- Test: `tests/AiObservatory.Api.Tests/Services/CompositeAlertNotifierTests.cs`

**Interfaces:**
- Consumes: `IUsageRepository.GetNotificationSettingsAsync` (Task 2), `BudgetAlertPayload` /
  `IAlertNotifier` (existing, `src/AiObservatory.Api/Services/IAlertNotifier.cs`).
- Produces: `SlackAlertNotifier(HttpClient, IUsageRepository, ILogger<SlackAlertNotifier>) : IAlertNotifier`,
  `CompositeAlertNotifier(EmailAlertNotifier, SlackAlertNotifier, ILogger<CompositeAlertNotifier>) : IAlertNotifier`
  — the latter becomes the `IAlertNotifier` DI registration `BudgetAlertService` depends on;
  `BudgetAlertService`'s own code is untouched (Task Global Constraints: Slack failure never
  blocks or gets conflated with email's existing retry semantics).

- [ ] **Step 1: Write the failing test for SlackAlertNotifier**

```csharp
// tests/AiObservatory.Api.Tests/Services/SlackAlertNotifierTests.cs
using System.Net;
using System.Text;
using System.Text.Json;
using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class SlackAlertNotifierTests
{
    private static BudgetAlertPayload MakePayload() =>
        new(
            "Anthropic",
            "Daily",
            10m,
            15m,
            DateTimeOffset.UtcNow,
            "budget-alert-10000000000000000000000000000001@observatory.fixportal.com"
        );

    [Fact]
    public async Task NotifyAsync_is_noop_when_webhook_not_configured()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>()).Returns((NotificationSettings?)null);

        var sut = new SlackAlertNotifier(http, repo, NullLogger<SlackAlertNotifier>.Instance);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_posts_a_text_payload_to_the_configured_webhook()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );

        var sut = new SlackAlertNotifier(http, repo, NullLogger<SlackAlertNotifier>.Instance);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.RequestUri.Should().Be(new Uri("https://hooks.slack.com/services/T0/B0/xyz"));
        var body = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        json.GetProperty("text").GetString().Should().Contain("Anthropic").And.Contain("£10.00");
    }

    [Fact]
    public async Task NotifyAsync_does_not_throw_when_the_webhook_call_fails()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );

        var sut = new SlackAlertNotifier(http, repo, NullLogger<SlackAlertNotifier>.Instance);
        var act = async () => await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }
}
```

This reuses `StubHttpMessageHandler` — check `tests/AiObservatory.Api.Tests/Services/StubHttpMessageHandler.cs`
(already used by `GitHubBillingClientTests.cs`) for its exact constructor/`Requests` property
shape before assuming the signature above matches; adjust the test to whatever its real API is
if it differs (e.g. it may capture requests differently, or its delegate may need the request
body pre-read).

- [ ] **Step 2: Run to verify it fails to compile**

```bash
dotnet build tests/AiObservatory.Api.Tests
```

Expected: compile error, `SlackAlertNotifier` doesn't exist yet.

- [ ] **Step 3: Implement SlackAlertNotifier**

```csharp
// src/AiObservatory.Api/Services/SlackAlertNotifier.cs
using System.Net.Http.Json;
using AiObservatory.Data.Repositories;

namespace AiObservatory.Api.Services;

/// <summary>
/// Posts a Slack incoming-webhook message. Best-effort: no retry, no lease -- a failure is
/// logged and swallowed by the caller (<see cref="CompositeAlertNotifier"/>), never surfaced
/// as a delivery failure that would cause <c>BudgetAlertService</c> to re-attempt the whole
/// payload (which would re-send email too).
/// </summary>
public sealed class SlackAlertNotifier(HttpClient http, IUsageRepository repository, ILogger<SlackAlertNotifier> logger)
    : IAlertNotifier
{
    public async Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var settings = await repository.GetNotificationSettingsAsync(ct);
        var webhookUrl = settings?.SlackWebhookUrl;
        if (string.IsNullOrEmpty(webhookUrl))
        {
            return;
        }

        var text =
            $"*Budget alert: {payload.Provider} {payload.Period} billed spend exceeded £{payload.ThresholdGbp:F2}*\n"
            + $"Total {payload.Period.ToLowerInvariant()} billed spend for {payload.Provider} reached £{payload.ActualSpendGbp:F2}, "
            + $"exceeding your £{payload.ThresholdGbp:F2} threshold.";

        using var response = await http.PostAsJsonAsync(webhookUrl, new { text }, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Slack webhook delivery failed with status {StatusCode} for budget alert {MessageId}",
                response.StatusCode,
                payload.MessageId
            );
        }
    }
}
```

Note: this method does **not** catch exceptions itself — `CompositeAlertNotifier` (next step)
is where the try/catch that makes Slack failures non-fatal lives, so `SlackAlertNotifier` alone
stays simple and its "does not throw" test in Step 1 is really exercising an HTTP 500 response
(caught by the `IsSuccessStatusCode` check), not a thrown exception. If `http.PostAsJsonAsync`
itself throws (e.g. DNS failure, timeout), that exception **does** propagate out of
`NotifyAsync` — `CompositeAlertNotifier` catches it there.

- [ ] **Step 4: Run the SlackAlertNotifier tests**

```bash
dotnet test tests/AiObservatory.Api.Tests --filter "FullyQualifiedName~SlackAlertNotifierTests"
```

Expected: all pass.

- [ ] **Step 5: Write the failing test for CompositeAlertNotifier**

```csharp
// tests/AiObservatory.Api.Tests/Services/CompositeAlertNotifierTests.cs
using AiObservatory.Api.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiObservatory.Api.Tests.Services;

public class CompositeAlertNotifierTests
{
    private static BudgetAlertPayload MakePayload() =>
        new(
            "Anthropic",
            "Daily",
            10m,
            15m,
            DateTimeOffset.UtcNow,
            "budget-alert-10000000000000000000000000000001@observatory.fixportal.com"
        );

    [Fact]
    public async Task NotifyAsync_calls_both_channels()
    {
        var email = Substitute.For<IAlertNotifier>();
        var slack = Substitute.For<IAlertNotifier>();
        var sut = new CompositeAlertNotifier(
            (EmailAlertNotifier)email,
            (SlackAlertNotifier)slack,
            NullLogger<CompositeAlertNotifier>.Instance
        );

        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await email.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
        await slack.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_still_calls_email_when_slack_throws()
    {
        var email = Substitute.For<IAlertNotifier>();
        var slack = Substitute.For<IAlertNotifier>();
        slack.NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("boom"));
        var sut = new CompositeAlertNotifier(
            (EmailAlertNotifier)email,
            (SlackAlertNotifier)slack,
            NullLogger<CompositeAlertNotifier>.Instance
        );

        var act = async () => await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        await email.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_propagates_when_email_throws_preserving_the_existing_retry_contract()
    {
        var email = Substitute.For<IAlertNotifier>();
        var slack = Substitute.For<IAlertNotifier>();
        email.NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        var sut = new CompositeAlertNotifier(
            (EmailAlertNotifier)email,
            (SlackAlertNotifier)slack,
            NullLogger<CompositeAlertNotifier>.Instance
        );

        var act = async () => await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
```

**Correction needed before this compiles:** `EmailAlertNotifier` and `SlackAlertNotifier` are
concrete sealed classes, not interfaces, so `(EmailAlertNotifier)email` (casting an
`IAlertNotifier` substitute to a concrete sealed type) will fail at runtime with an
`InvalidCastException` — NSubstitute cannot produce a proxy castable to an unrelated sealed
type. **Do not use this cast pattern.** Instead, since both classes are `sealed` (so
NSubstitute can't subclass them directly either), the test must construct real instances with
their own substituted dependencies, or — simpler and preferred here —
`CompositeAlertNotifier`'s constructor should depend on `IAlertNotifier` twice, distinguished
by DI-registration order/keying rather than by concrete type. **Use this signature instead:**

```csharp
public sealed class CompositeAlertNotifier(
    [FromKeyedServices("email")] IAlertNotifier email,
    [FromKeyedServices("slack")] IAlertNotifier slack,
    ILogger<CompositeAlertNotifier> logger
) : IAlertNotifier
```

using .NET's keyed DI services (available since .NET 8, this app targets .NET 10 per
`AiObservatory.Api.csproj`). This makes both constructor parameters genuinely `IAlertNotifier`,
so the test above's `Substitute.For<IAlertNotifier>()` values pass straight in with no cast:

```csharp
        var sut = new CompositeAlertNotifier(email, slack, NullLogger<CompositeAlertNotifier>.Instance);
```

Apply that fix to all three tests above (remove every `(EmailAlertNotifier)` / `(SlackAlertNotifier)`
cast, pass `email`/`slack` directly) before running them.

- [ ] **Step 6: Implement CompositeAlertNotifier**

```csharp
// src/AiObservatory.Api/Services/CompositeAlertNotifier.cs
namespace AiObservatory.Api.Services;

/// <summary>
/// Fans a budget alert out to both delivery channels. Email keeps its existing at-least-once
/// retry semantics from before this class existed: its failure propagates unchanged, so
/// <c>BudgetAlertService</c>'s lease is released and the whole delivery retries. Slack is a
/// best-effort secondary channel with no lease of its own -- its failure is logged, never
/// retried, and never blocks email from being attempted or from correctly reporting its own
/// outcome upward.
/// </summary>
public sealed class CompositeAlertNotifier(
    [FromKeyedServices("email")] IAlertNotifier email,
    [FromKeyedServices("slack")] IAlertNotifier slack,
    ILogger<CompositeAlertNotifier> logger
) : IAlertNotifier
{
    public async Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        try
        {
            await slack.NotifyAsync(payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Slack alert delivery failed for budget alert {MessageId}", payload.MessageId);
        }

        await email.NotifyAsync(payload, ct);
    }
}
```

Add `using Microsoft.Extensions.DependencyInjection;` at the top for `[FromKeyedServices]` if
your editor doesn't resolve it automatically — it lives in that namespace.

- [ ] **Step 7: Wire DI in Program.cs**

In `src/AiObservatory.Api/Program.cs`, replace line 47
(`builder.Services.AddTransient<IAlertNotifier, EmailAlertNotifier>();`) with:

```csharp
builder.Services.AddKeyedTransient<IAlertNotifier, EmailAlertNotifier>("email");
builder.Services.AddHttpClient<SlackAlertNotifier>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddKeyedTransient<IAlertNotifier, SlackAlertNotifier>("slack");
builder.Services.AddTransient<IAlertNotifier, CompositeAlertNotifier>();
```

This registers three things under the same `IAlertNotifier` interface — two keyed (`"email"`,
`"slack"`), resolved only by `CompositeAlertNotifier`'s `[FromKeyedServices]` parameters, and
one unkeyed (`CompositeAlertNotifier` itself), which is what `BudgetAlertService`'s plain
(non-keyed) `IAlertNotifier` constructor parameter resolves to — unkeyed and keyed
registrations of the same service type coexist without conflict in .NET's DI container.

`AddHttpClient<SlackAlertNotifier>()` makes `SlackAlertNotifier` itself the typed client (its
`HttpClient http` constructor parameter is supplied by the factory), matching the existing
`AddHttpClient<FxRateProvider>()` pattern a few lines below in this same file.

- [ ] **Step 8: Build, run all Task 5 tests**

```bash
dotnet build
dotnet test tests/AiObservatory.Api.Tests --filter "FullyQualifiedName~SlackAlertNotifierTests|FullyQualifiedName~CompositeAlertNotifierTests"
```

Expected: all pass, whole solution builds with 0 errors.

- [ ] **Step 9: Run the full Api unit test project as a regression check**

```bash
dotnet test tests/AiObservatory.Api.Tests
```

Expected: all pass (this catches anything Task 4/5's DI changes broke elsewhere, e.g. if any
other test directly `new EmailAlertNotifier(...)`'d with the old 2-arg constructor).

- [ ] **Step 10: Commit**

```bash
git add src/AiObservatory.Api/Services/SlackAlertNotifier.cs src/AiObservatory.Api/Services/CompositeAlertNotifier.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.Tests/Services/SlackAlertNotifierTests.cs tests/AiObservatory.Api.Tests/Services/CompositeAlertNotifierTests.cs
git commit -m "feat: add Slack delivery channel via CompositeAlertNotifier"
```

---

## Task 6: Frontend API client + query hooks

**Files:**
- Modify: `src/AiObservatory.Web/src/api/client.ts`
- Modify: `src/AiObservatory.Web/src/api/queries.ts`
- Test: `src/AiObservatory.Web/src/api/queries.test.tsx` (extend existing file)

**Interfaces:**
- Consumes: `GET /api/notification-settings`, `PUT /api/notification-settings` (Task 3).
- Produces: `getNotificationSettings(): Promise<NotificationSettings>`,
  `updateNotificationSettings(body: { alertEmailTo?: string | null; slackWebhookUrl?: string | null }): Promise<NotificationSettings>`
  in `client.ts`; `useNotificationSettings(): { settings: NotificationSettings | undefined; isLoading: boolean; isError: boolean }`
  in `queries.ts` — consumed by `BudgetRulesPanel.tsx` in Task 7.

- [ ] **Step 1: Remove the superseded email-status client function**

In `src/AiObservatory.Web/src/api/client.ts`, delete this line (currently ~311):

```typescript
export const getEmailStatus = () => getJson<{ configured: boolean }>('/budget-rules/email-status')
```

- [ ] **Step 2: Add the new client functions**

In the same file, add right after the line you just deleted (i.e. in the same spot, after
`deleteBudgetRule`):

```typescript
export interface NotificationSettings {
  emailConfigured: boolean
  emailMasked: string | null
  slackConfigured: boolean
  slackMasked: string | null
}

export const getNotificationSettings = () => getJson<NotificationSettings>('/notification-settings')

export const updateNotificationSettings = async (
  body: { alertEmailTo?: string | null; slackWebhookUrl?: string | null },
): Promise<NotificationSettings> => {
  const res = await request('/notification-settings', { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(body) })
  return res.json() as Promise<NotificationSettings>
}
```

- [ ] **Step 3: Remove the old hook, add the new one**

In `src/AiObservatory.Web/src/api/queries.ts`, delete `useEmailStatus` (currently lines
139-142):

```typescript
export function useEmailStatus(): { configured: boolean | undefined } {
  const { data } = useQuery({ queryKey: ['email-status'], queryFn: getEmailStatus })
  return { configured: data?.configured }
}
```

Add in its place:

```typescript
export function useNotificationSettings(): {
  settings: NotificationSettings | undefined
  isLoading: boolean
  isError: boolean
} {
  const { data, isPending, isError } = useQuery({
    queryKey: ['notification-settings'],
    queryFn: getNotificationSettings,
  })
  return { settings: data, isLoading: isPending, isError }
}
```

Update this file's `import` line from `./client` to include `getNotificationSettings` and the
`NotificationSettings` type, and remove `getEmailStatus` from it.

- [ ] **Step 4: Build/typecheck**

```bash
cd src/AiObservatory.Web
npx tsc -b --noEmit
```

Expected: errors pointing at every remaining usage of `useEmailStatus`/`getEmailStatus` — there
should be exactly one, in `BudgetRulesPanel.tsx`, fixed in Task 7. If `tsc` reports errors
anywhere else, find and note them for Task 7 too (do not silently leave a second caller broken).

- [ ] **Step 5: Commit**

(Wait until Task 7 fixes the now-broken `BudgetRulesPanel.tsx` reference before committing —
`tsc -b` failing is expected at this exact point and would make this an intentionally broken
commit. Skip straight to Task 7, then commit both together, OR commit now with a message
noting the follow-up if your workflow prefers small commits — this plan recommends waiting.)

---

## Task 7: `BudgetRulesPanel.tsx` notification settings UI

**Files:**
- Modify: `src/AiObservatory.Web/src/components/BudgetRulesPanel.tsx`
- Modify: `src/AiObservatory.Web/src/index.css`
- Create: `src/AiObservatory.Web/src/components/BudgetRulesPanel.test.tsx` (no existing test
  file for this component — this task creates the first one, scoped to the new notification
  section since that's this task's deliverable, not a full retrofit of every existing behavior)

**Interfaces:**
- Consumes: `useNotificationSettings()`, `updateNotificationSettings()` (Task 6).
- Produces: the rendered "Notifications" section inside `BudgetRulesPanel`.

- [ ] **Step 1: Write the failing test file**

```tsx
// src/AiObservatory.Web/src/components/BudgetRulesPanel.test.tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, test, vi } from 'vitest'
import type { BudgetRule, Insight, NotificationSettings } from '../api/client'
import BudgetRulesPanel from './BudgetRulesPanel'

const data = vi.hoisted(() => ({
  rules: [] as BudgetRule[],
  insights: [] as Insight[],
  settings: { emailConfigured: false, emailMasked: null, slackConfigured: false, slackMasked: null } as NotificationSettings,
}))

vi.mock('../api/queries', () => ({
  useBudgetRules: () => ({ rules: data.rules, isLoading: false, isError: false }),
  useInsights: () => ({ insights: data.insights, isError: false, isLoading: false }),
  useNotificationSettings: () => ({ settings: data.settings, isLoading: false, isError: false }),
}))

const updateNotificationSettings = vi.hoisted(() => vi.fn(() => Promise.resolve({
  emailConfigured: true, emailMasked: 'ch***@fixportal.org', slackConfigured: false, slackMasked: null,
})))
vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  updateNotificationSettings,
}))

vi.mock('../auth/msal', () => ({ isReadonly: false }))

function renderPanel() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <BudgetRulesPanel />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  data.rules = []
  data.insights = []
  data.settings = { emailConfigured: false, emailMasked: null, slackConfigured: false, slackMasked: null }
  updateNotificationSettings.mockClear()
})

describe('BudgetRulesPanel notification settings', () => {
  test('shows "Not set" and an Add control for each unconfigured channel', () => {
    renderPanel()

    expect(screen.getByText('Email')).toBeInTheDocument()
    expect(screen.getByText('Slack')).toBeInTheDocument()
    expect(screen.getAllByText('Not set')).toHaveLength(2)
  })

  test('shows the masked value and Edit/Remove for a configured channel', () => {
    data.settings = { emailConfigured: true, emailMasked: 'ch***@fixportal.org', slackConfigured: false, slackMasked: null }
    renderPanel()

    expect(screen.getByText('ch***@fixportal.org')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /edit email/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /remove email/i })).toBeInTheDocument()
  })

  test('adding an email calls updateNotificationSettings with alertEmailTo only', async () => {
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /add email/i }))
    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'chris@fixportal.org' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(updateNotificationSettings).toHaveBeenCalledWith({ alertEmailTo: 'chris@fixportal.org' }),
    )
  })

  test('removing a configured Slack webhook requires a confirm click', async () => {
    data.settings = {
      emailConfigured: false, emailMasked: null,
      slackConfigured: true, slackMasked: 'https://hooks.slack.com/services/***',
    }
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /remove slack/i }))
    expect(updateNotificationSettings).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }))
    await waitFor(() =>
      expect(updateNotificationSettings).toHaveBeenCalledWith({ slackWebhookUrl: null }),
    )
  })

  test('hides every notification control for a readonly viewer', async () => {
    vi.doMock('../auth/msal', () => ({ isReadonly: true }))
    vi.resetModules()
    const { default: ReadonlyPanel } = await import('./BudgetRulesPanel')
    render(
      <QueryClientProvider client={new QueryClient()}>
        <ReadonlyPanel />
      </QueryClientProvider>,
    )

    expect(screen.queryByRole('button', { name: /add email/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /add slack/i })).not.toBeInTheDocument()
  })
})
```

The last test uses `vi.doMock` + `vi.resetModules` + dynamic `import` to flip `isReadonly` for
a single test — this pattern is fragile in this codebase (a similar attempt was tried and
abandoned earlier this session for `AdversarialReviewPanel.test.tsx` because Vitest's static
`vi.mock` hoisting made it unreliable). **If this test fails or behaves inconsistently, delete
it** rather than fight the mocking — the readonly-gating behavior itself (wrapping every write
control in `{!isReadonly && (...)}`, Step 3 below) is still required by the Global Constraints,
just not provably covered by this specific test if the pattern doesn't work here either.

- [ ] **Step 2: Run to verify it fails**

```bash
npx vitest run src/components/BudgetRulesPanel.test.tsx
```

Expected: FAIL — `useNotificationSettings` isn't imported/used yet, "Email"/"Slack" text and
the Add/Edit/Remove buttons don't exist in the current render output.

- [ ] **Step 3: Implement the UI**

Replace `src/AiObservatory.Web/src/components/BudgetRulesPanel.tsx` in full:

```tsx
import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '../design/Button'
import { createBudgetRule, deleteBudgetRule, updateNotificationSettings } from '../api/client'
import { useBudgetRules, useInsights, useNotificationSettings } from '../api/queries'
import { isReadonly } from '../auth/msal'
import { gbp } from '../lib/currency'

const PROVIDERS = ['anthropic', 'copilot', 'google', 'openai'] as const
const PERIODS = ['daily', 'weekly', 'monthly'] as const

const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

type Channel = 'email' | 'slack'

interface NotificationChannelRowProps {
  channel: Channel
  label: string
  configured: boolean
  masked: string | null
  onSave: (value: string) => void
  onClear: () => void
  isSaving: boolean
}

function NotificationChannelRow({ channel, label, configured, masked, onSave, onClear, isSaving }: NotificationChannelRowProps) {
  const [editing, setEditing] = useState(false)
  const [confirmingRemove, setConfirmingRemove] = useState(false)
  const [value, setValue] = useState('')
  const fieldId = `notification-${channel}-input`
  const fieldLabel = channel === 'email' ? 'Email address' : 'Slack webhook URL'

  if (editing) {
    return (
      <div className="budget-rules__channel-row">
        <span className="budget-rules__channel-label">{label}</span>
        <label htmlFor={fieldId} className="visually-hidden">{fieldLabel}</label>
        <input
          id={fieldId}
          type="text"
          value={value}
          onChange={e => setValue(e.target.value)}
          placeholder={channel === 'email' ? 'you@example.com' : 'https://hooks.slack.com/services/...'}
          className="budget-rules__control"
        />
        <Button variant="primary" size="sm" disabled={isSaving || value.trim() === ''} onClick={() => { onSave(value.trim()); setEditing(false); setValue('') }}>
          Save
        </Button>
        <Button variant="ghost" size="sm" onClick={() => { setEditing(false); setValue('') }}>
          Cancel
        </Button>
      </div>
    )
  }

  return (
    <div className="budget-rules__channel-row">
      <span className="budget-rules__channel-label">{label}</span>
      {configured ? (
        <>
          <span className="budget-rules__channel-value">{masked}</span>
          {!isReadonly && (
            confirmingRemove ? (
              <>
                <Button variant="danger" size="sm" disabled={isSaving} onClick={() => { onClear(); setConfirmingRemove(false) }}>
                  Confirm
                </Button>
                <Button variant="ghost" size="sm" onClick={() => setConfirmingRemove(false)}>
                  Cancel
                </Button>
              </>
            ) : (
              <>
                <Button variant="ghost" size="sm" aria-label={`Edit ${label.toLowerCase()}`} onClick={() => setEditing(true)}>
                  Edit
                </Button>
                <Button variant="danger" size="sm" aria-label={`Remove ${label.toLowerCase()}`} onClick={() => setConfirmingRemove(true)}>
                  Remove
                </Button>
              </>
            )
          )}
        </>
      ) : (
        <>
          <span className="budget-rules__channel-value budget-rules__channel-value--unset">Not set</span>
          {!isReadonly && (
            <Button variant="ghost" size="sm" aria-label={`Add ${label.toLowerCase()}`} onClick={() => setEditing(true)}>
              Add
            </Button>
          )}
        </>
      )}
    </div>
  )
}

function NotificationSettingsSection() {
  const qc = useQueryClient()
  const { settings } = useNotificationSettings()

  const save = useMutation({
    mutationFn: updateNotificationSettings,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notification-settings'] }),
  })

  if (!settings) return null

  return (
    <div className="panel budget-rules__history">
      <div className="panel-title">Notifications</div>
      <NotificationChannelRow
        channel="email"
        label="Email"
        configured={settings.emailConfigured}
        masked={settings.emailMasked}
        onSave={value => save.mutate({ alertEmailTo: value })}
        onClear={() => save.mutate({ alertEmailTo: null })}
        isSaving={save.isPending}
      />
      <NotificationChannelRow
        channel="slack"
        label="Slack"
        configured={settings.slackConfigured}
        masked={settings.slackMasked}
        onSave={value => save.mutate({ slackWebhookUrl: value })}
        onClear={() => save.mutate({ slackWebhookUrl: null })}
        isSaving={save.isPending}
      />
    </div>
  )
}

export default function BudgetRulesPanel() {
  const qc = useQueryClient()
  const { rules, isLoading, isError } = useBudgetRules()
  const { insights } = useInsights()

  const [panelOpen, setPanelOpen] = useState(false)
  const [provider, setProvider] = useState<string>('')
  const [period, setPeriod] = useState<'daily' | 'weekly' | 'monthly'>('monthly')
  const [threshold, setThreshold] = useState<string>('')
  const [mutationError, setMutationError] = useState<string | null>(null)
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null)

  const deleteRule = useMutation({
    mutationFn: deleteBudgetRule,
    onMutate: () => setMutationError(null),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['budget-rules'] }); setConfirmDeleteId(null) },
    onError: (e: Error) => setMutationError(`Couldn’t remove the rule: ${e.message}`),
  })

  const addRule = useMutation({
    mutationFn: () =>
      createBudgetRule({
        provider: provider === '' ? null : provider,
        period,
        thresholdGbp: parseFloat(threshold),
      }),
    onMutate: () => setMutationError(null),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['budget-rules'] })
      setPanelOpen(false)
      setProvider('')
      setPeriod('monthly')
      setThreshold('')
    },
    onError: (e: Error) => setMutationError(`Couldn’t add the rule: ${e.message}`),
  })

  const budgetAlerts = insights
    .filter(i => i.title.startsWith('Budget alert:'))
    .sort((a, b) => b.generatedAt.localeCompare(a.generatedAt))
    .slice(0, 10)

  function handleOpenPanel() {
    setProvider('')
    setPeriod('monthly')
    setThreshold('')
    setPanelOpen(true)
  }

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const val = parseFloat(threshold)
    if (!Number.isFinite(val) || val <= 0) return
    addRule.mutate()
  }

  return (
    <section>
      <div className="panel">
        <div className="budget-rules__header">
          <div className="budget-rules__title-row">
            <span className="panel-title">
              Budget Rules
            </span>
          </div>
          {!isReadonly && (
            <Button variant="ghost" size="sm" onClick={handleOpenPanel} disabled={panelOpen}>
              + Add rule
            </Button>
          )}
        </div>

        <div className="budget-rules__body">
          {isError && <p className="panel-empty">Failed to load budget rules.</p>}
          {mutationError && <p className="panel-empty" role="alert">{mutationError}</p>}
          {!isError && !isLoading && rules.length === 0 && (
            <p className="panel-empty">No budget rules configured.</p>
          )}
          {rules.length > 0 && (
            <div className="model-table-wrap">
              <table className="budget-rules__table">
              <thead>
                <tr>
                  <th>
                    Provider
                  </th>
                  <th>
                    Period
                  </th>
                  <th>
                    Current / limit
                  </th>
                  <th>
                    Last fired
                  </th>
                  {!isReadonly && <th aria-label="Actions" />}
                </tr>
              </thead>
              <tbody>
                {rules.map(rule => (
                  <tr key={rule.id}>
                    <td data-label="Provider">
                      {rule.provider ? capitalize(rule.provider) : 'All providers'}
                    </td>
                    <td data-label="Period">
                      {capitalize(rule.period)}
                    </td>
                    <td data-label="Current / limit">
                      <span className="budget-rules__amount">{gbp(rule.currentSpendGbp)} / {gbp(rule.thresholdGbp)}</span>
                      <span className={`budget-rules__status${rule.currentSpendGbp > rule.thresholdGbp ? ' budget-rules__status--over' : ''}`}>
                        {rule.currentSpendGbp > rule.thresholdGbp ? 'Over limit' : 'Within limit'}
                      </span>
                    </td>
                    <td data-label="Last fired">
                      {rule.lastTriggeredAt
                        ? new Date(rule.lastTriggeredAt).toLocaleString()
                        : 'Never'}
                    </td>
                    {!isReadonly && (
                      <td className="budget-rules__actions">
                        {confirmDeleteId === rule.id ? (
                          <span>
                            <Button
                              variant="danger"
                              size="sm"
                              onClick={() => deleteRule.mutate(rule.id)}
                              disabled={deleteRule.isPending}
                            >
                              Confirm
                            </Button>
                            <Button variant="ghost" size="sm" onClick={() => setConfirmDeleteId(null)}>
                              Cancel
                            </Button>
                          </span>
                        ) : (
                          <Button variant="danger" size="sm" onClick={() => setConfirmDeleteId(rule.id)}>
                            Remove
                          </Button>
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      {panelOpen && (
        <div className="panel budget-rules__history">
          <div className="panel-title">Add Budget Rule</div>
          <form onSubmit={handleSubmit}>
            <div className="budget-rules__form-grid">
              <label className="budget-rules__field">
                Provider
                <select
                  value={provider}
                  onChange={e => setProvider(e.target.value)}
                  className="budget-rules__control"
                >
                  <option value="">All providers</option>
                  {PROVIDERS.map(p => (
                    <option key={p} value={p}>{capitalize(p)}</option>
                  ))}
                </select>
              </label>

              <label className="budget-rules__field">
                Period
                <select
                  value={period}
                  onChange={e => setPeriod(e.target.value as typeof period)}
                  className="budget-rules__control"
                >
                  {PERIODS.map(p => (
                    <option key={p} value={p}>{capitalize(p)}</option>
                  ))}
                </select>
              </label>

              <label className="budget-rules__field">
                Threshold (GBP)
                <input
                  type="number"
                  min="0.01"
                  step="0.01"
                  value={threshold}
                  onChange={e => setThreshold(e.target.value)}
                  placeholder="e.g. 50"
                  required
                  className="budget-rules__control"
                />
              </label>
            </div>

            <div className="budget-rules__actions">
              <Button type="submit" variant="primary" size="sm" disabled={addRule.isPending || threshold === ''}>
                {addRule.isPending ? 'Adding...' : 'Add rule'}
              </Button>
              <Button type="button" variant="ghost" size="sm" onClick={() => setPanelOpen(false)}>
                Cancel
              </Button>
            </div>
          </form>
        </div>
      )}

      <NotificationSettingsSection />

      <div className="panel budget-rules__history">
        <div className="panel-title">Alert History</div>
        {budgetAlerts.length === 0 ? (
          <p className="panel-empty">No budget alerts triggered.</p>
        ) : (
          <div className="budget-rules__history">
            {budgetAlerts.map(alert => (
              <div
                key={alert.id}
                className="insight insight-anomaly"
              >
                <div className="insight-title">{alert.title}</div>
                <div className="insight-body">
                  {alert.body}
                </div>
                <div className="budget-rules__history-time">
                  {new Date(alert.generatedAt).toLocaleString()}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </section>
  )
}
```

Note the old `WebhookChip` component and its `configured` prop are removed entirely — it was
the only consumer of the endpoint deleted in Task 3, and its "Email: configured" chip is now
superseded by the richer `NotificationSettingsSection` below it.

- [ ] **Step 4: Add the two new CSS classes**

In `src/AiObservatory.Web/src/index.css`, find `.budget-rules__channel` (used by the now-removed
`WebhookChip`) and add these two new classes near it — reuse `.budget-rules__field` /
`.budget-rules__control` styling already defined for the "Add Budget Rule" form inputs rather
than inventing new input styles:

```css
.budget-rules__channel-row { display: flex; align-items: center; gap: var(--space-2); padding: var(--space-2) 0; }
.budget-rules__channel-label { font-weight: 600; min-width: 60px; }
.budget-rules__channel-value { font-family: var(--font-mono); color: var(--text-muted); }
.budget-rules__channel-value--unset { font-style: italic; }
```

If `.budget-rules__channel` (the old chip class, now unused) has no other consumer after this
task, remove it too — check with a repo-wide grep before deleting:

```bash
grep -rn "budget-rules__channel\b" src/AiObservatory.Web/src
```

If the only remaining matches are in `index.css` itself, delete the `.budget-rules__channel`
and `.budget-rules__channel--configured` rules.

- [ ] **Step 5: Run the test file, iterate until green**

```bash
npx vitest run src/components/BudgetRulesPanel.test.tsx
```

Expected: all pass except possibly the last (`readonly viewer`) test — per Step 1's note, delete
that one test if it proves unreliable rather than spending more than a couple of iterations on
the mocking pattern.

- [ ] **Step 6: Full frontend gate**

```bash
npx tsc -b --noEmit
npx eslint .
npx vitest run
```

Expected: `tsc` clean (this also confirms Task 6's dangling-reference concern from its Step 4 is
now resolved), `eslint` clean, full suite green.

- [ ] **Step 7: Commit Tasks 6 and 7 together**

```bash
git add src/AiObservatory.Web/src/api/client.ts src/AiObservatory.Web/src/api/queries.ts src/AiObservatory.Web/src/components/BudgetRulesPanel.tsx src/AiObservatory.Web/src/components/BudgetRulesPanel.test.tsx src/AiObservatory.Web/src/index.css
git commit -m "feat: add notification settings UI to BudgetRulesPanel"
```

---

## Task 8: Full-solution verification

**Files:** none (verification only).

- [ ] **Step 1: Backend — full build and test**

```bash
dotnet build
dotnet test tests/AiObservatory.Data.Tests
dotnet test tests/AiObservatory.Api.Tests
dotnet test tests/AiObservatory.Api.IntegrationTests
dotnet csharpier check .
```

Expected: 0 errors, all tests pass, formatting clean. (Integration + Data.Tests need a local
Postgres — `TEST_DB_CONNECTION`, same requirement as every other run in this session.)

- [ ] **Step 2: Frontend — full gate**

```bash
cd src/AiObservatory.Web
npx tsc -b --noEmit
npx eslint .
npx vitest run
```

Expected: all clean, per the house rule that this full gate runs before any push.

- [ ] **Step 3: Manual smoke test against a local docker compose stack**

```bash
docker compose up -d --build
```

Read the admin key from the running container and exercise the new endpoints directly, mirroring
exactly how earlier figure-audits this session verified live behavior (`docker exec
fixportal-ai-observatory-api-1 printenv OBSERVATORY_API_KEY`, then `curl`/`Invoke-RestMethod`
against `http://localhost:4173/api/notification-settings` for GET and PUT). Confirm:
- GET with nothing configured returns all-false/all-null.
- PUT setting only email leaves Slack untouched and vice versa.
- PUT with a malformed email/webhook returns 400.
- The web UI at `http://localhost:4173/?key=<OBSERVATORY_API_KEY>` shows the Notifications
  section under Budget Rules, and Add/Edit/Remove work end to end through the real API.

Tear down after: `docker compose down -v`.

- [ ] **Step 4: Note the rollout caveat**

No code step here — this is a reminder for whoever merges this. Per the spec's Rollout section:
any existing deployment with `BUDGET_ALERT_EMAIL_TO` set will silently stop emailing once this
ships, until an admin re-enters the same address via the new Notifications UI. Flag this
explicitly when the PR is ready, so it isn't discovered the hard way when the next budget alert
doesn't arrive.
