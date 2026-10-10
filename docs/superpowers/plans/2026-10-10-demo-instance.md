# Public Demo Instance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A public, read-only Observatory on a separate Azure stack that serves only synthetic seed data, so the project can be announced without exposing the maintainer's real spend.

**Architecture:** A new `OBSERVATORY_DEMO_MODE` API setting (off by default) enables the seed route outside Development, adds a transactional `reset-demo` route, blocks every other write with a middleware, refuses to start against a non-demo database, and skips the background worker. The existing Bicep is parameterised so the same template builds the demo stack. A one-time bootstrap script creates what Bicep only references (Postgres, static web app), and two workflows deploy and reset it.

**Tech Stack:** ASP.NET Core minimal APIs (.NET 10), EF Core + Npgsql, xUnit v3 + AwesomeAssertions, React + Vite + Vitest, Bicep, GitHub Actions, PowerShell.

**Spec:** `docs/superpowers/specs/2026-10-10-demo-instance-design.md`

## Deviations from the spec (found while reading the code)

Read these first; each is a correction the spec did not know.

1. **Bicep does not create Postgres or the static web app.** `infra/modules/postgresql.bicep` and `swa.bicep` reference them as `existing`. The demo therefore needs a one-time bootstrap script (Task 6) that creates them before the Bicep deploy.
2. **`SWA_ORIGIN` and the SWA custom domain are hard-coded to production** (`appservice.bicep:50`, `swa.bicep:17`). They become parameters with production's current values as defaults (Task 5).
3. **The write block is a middleware, not an endpoint filter.** `/api/ide/v1/events` (POST) is a separate route group with its own filter, so a filter on the `/api` group would miss it (Task 3).
4. **The background worker is not registered in demo mode.** `IntelligenceWorkerService` calls Anthropic, sends budget alerts and syncs GitHub billing; none belongs in a synthetic instance (Task 3).
5. **The startup guard checks the database host only** (allowlist: `fpaiodemo-*`, or loopback/`db` for local and CI). The spec also named the Key Vault, but the app cannot observe its vault name from configuration.
6. **The reset workflow authenticates with a GitHub environment secret (`DEMO_ADMIN_KEY`), not OIDC plus a vault read.** Fewer moving parts; the key is demo-only.
7. **The `compose-smoke` demo profile is deferred.** The integration tests in Task 3 run the real composition root in Production plus demo mode, which covers the same behaviour. Add the Compose profile only if the demo regresses in a way those tests miss.

## Global Constraints

- Demo mode is off unless `OBSERVATORY_DEMO_MODE` equals `true` (case-insensitive). With it off, behaviour is byte-for-byte what ships today, including `/api/dev/seed` being absent outside Development.
- Production Bicep parameter defaults must equal today's hard-coded values (`swaOrigin` = `https://observatory.fixportal.org`, `swaCustomDomain` = `observatory.fixportal.org`, `deployIngest` = `true`, `demoMode` = `false`).
- .NET: xUnit v3 with `TestContext.Current.CancellationToken`, assertions via AwesomeAssertions `.Should()`, never `Assert.*`. New tests that need Postgres carry `[Trait("Category", "Integration")]` and use `AiObservatoryApiFactory`.
- No emoji anywhere (code, comments, commits, docs).
- Dates and times: inject `IClock`; no static `DateTime.UtcNow`.
- Commit messages: the `git commit -m "..."` lines below give the subject only. Write the full message (subject, blank line, body if any, blank line, `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`) to a temp file and commit with `git commit -F <file>`; never use a shell heredoc.
- Pre-push gate (run once, before the single push; PR-only repo, rebase-merge): `dotnet tool restore`, `dotnet csharpier check .`, `dotnet restore AiObservatory.slnx`, `dotnet build AiObservatory.slnx --configuration Release --no-restore`, `dotnet test --solution AiObservatory.slnx --configuration Release --no-build --report-xunit-trx --results-directory ./TestResults --timeout 5m`; in `src/AiObservatory.Web`: `npm ci`, `npm run lint`, `npm test -- --coverage`, `npm run build`; then `python -m pytest .github/scripts -q`, `python .github/scripts/assert_canonical_assets.py`, and `pwsh -File .github/ai-smells/pr-detect.ps1 -RepoPath . -Base origin/main -Head HEAD -OutFile <scratch>.json`.
- Work happens in the worktree `D:\fix-portal\fixportal-observatory\.claude\worktrees\demo-instance` on branch `demo-instance`. Grep/ripgrep skips that directory (it is gitignored); use Read and Glob there, or search the primary checkout for unchanged files.

## Review Focus

Failure modes the spec implies that a first pass would miss, most likely first. Each has a pinning test in the owning task.

1. **Concurrent resets** (nightly schedule overlapping a manual dispatch) must not duplicate rows. Pinned in Task 1 (`ResetAsync_run_twice_concurrently_leaves_one_copy_of_the_seed`).
2. **A failing seed during reset** must leave the previous data intact, not an empty dashboard. Pinned in Task 1.
3. **A multi-host or mixed-case connection string** must not slip a production host past the startup guard (`Host=fpaiodemo-db,fpaiobs-db`, `FPAIOBS-DB`). Pinned in Task 2.
4. **CORS preflight** (OPTIONS) must keep working under the write block, or the browser demo shows a blank page. Pinned in Task 3.
5. **The IDE route group** (`POST /api/ide/v1/events`, separate filter) must be blocked too. Pinned in Task 3.

---

### Task 1: Extract `DemoSeeder` and add a transactional reset

**Files:**
- Create: `src/AiObservatory.Api/DemoSeeder.cs`
- Modify: `src/AiObservatory.Api/Program.cs` (the `/dev/seed` lambda at lines ~184-406, and the `SeedBilled*` members at ~436-515)
- Test: `tests/AiObservatory.Api.IntegrationTests/DemoSeederTests.cs`

**Interfaces:**
- Produces (all `internal static` on `AiObservatory.Api.DemoSeeder`; the Api project already exposes internals to both test projects):
  - `Task<bool> HasAnyDataAsync(AiObservatoryDbContext db, CancellationToken ct)`
  - `Task ClearAsync(AiObservatoryDbContext db, CancellationToken ct)`
  - `Task SeedAsync(AiObservatoryDbContext db, IClock clock, CancellationToken ct)`
  - `Task ResetAsync(AiObservatoryDbContext db, IClock clock, CancellationToken ct)`
- Consumes: existing entities and `UsageSourceIds.DemoSeed`.

- [ ] **Step 1: Confirm the existing seed tests are green before touching anything**

Run: `dotnet test --solution AiObservatory.slnx --configuration Release --filter-class "*DevSeedEndpointTests"`
Expected: 3 passed. If the filter syntax is rejected by this SDK's test runner, run the whole `AiObservatory.Api.IntegrationTests` project instead. These tests are the safety net for the move in Step 3.

- [ ] **Step 2: Write the failing tests**

Create `tests/AiObservatory.Api.IntegrationTests/DemoSeederTests.cs`:

```csharp
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// The demo reset wipes the seeded tables and reseeds in one transaction. These tests pin the
/// three ways that could go wrong in front of a visitor: leaving catalog data behind or
/// deleting it, leaving the dashboard empty after a failed seed, and duplicating rows when two
/// resets overlap.
/// </summary>
[Trait("Category", "Integration")]
public class DemoSeederTests
{
    private sealed class ThrowingClock : IClock
    {
        public Instant GetCurrentInstant() => throw new InvalidOperationException("clock failure");
    }

    private static async Task<(int Aggregates, int Subscriptions, int BudgetRules, int Insights, int Events, int Spend)>
        CountSeededAsync(AiObservatoryApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var ct = TestContext.Current.CancellationToken;
        return (
            await db.DailyAggregates.CountAsync(ct),
            await db.Subscriptions.CountAsync(ct),
            await db.BudgetRules.CountAsync(ct),
            await db.Insights.CountAsync(ct),
            await db.UsageEvents.CountAsync(ct),
            await db.SpendEntries.CountAsync(ct)
        );
    }

    [Fact]
    public async Task ClearAsync_empties_every_seeded_table_and_keeps_the_vendor_catalog()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        (await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        var vendorsBefore = await db.SpendVendors.CountAsync(TestContext.Current.CancellationToken);

        await DemoSeeder.ClearAsync(db, TestContext.Current.CancellationToken);

        (await DemoSeeder.HasAnyDataAsync(db, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await db.SpendVendors.CountAsync(TestContext.Current.CancellationToken))
            .Should()
            .Be(vendorsBefore)
            .And.BeGreaterThan(0, "the vendor catalog is migration data, not seed data");
    }

    [Fact]
    public async Task ResetAsync_restores_the_same_seed_after_the_data_was_changed()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        (await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var seeded = await CountSeededAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            await db.Subscriptions.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await db.Insights.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            await DemoSeeder.ResetAsync(
                scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>(),
                scope.ServiceProvider.GetRequiredService<IClock>(),
                TestContext.Current.CancellationToken
            );
        }

        (await CountSeededAsync(factory)).Should().Be(seeded);
    }

    [Fact]
    public async Task ResetAsync_when_the_seed_fails_leaves_the_previous_data_untouched()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        (await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var seeded = await CountSeededAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            var act = () => DemoSeeder.ResetAsync(db, new ThrowingClock(), TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        (await CountSeededAsync(factory)).Should().Be(seeded, "a failed reset must roll back the wipe");
    }

    [Fact]
    public async Task ResetAsync_run_twice_concurrently_leaves_one_copy_of_the_seed()
    {
        await using var factory = new AiObservatoryApiFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();
        (await client.PostAsync("/api/dev/seed", content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var seeded = await CountSeededAsync(factory);

        async Task ResetOnceAsync()
        {
            using var scope = factory.Services.CreateScope();
            await DemoSeeder.ResetAsync(
                scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>(),
                scope.ServiceProvider.GetRequiredService<IClock>(),
                TestContext.Current.CancellationToken
            );
        }

        await Task.WhenAll(ResetOnceAsync(), ResetOnceAsync());

        (await CountSeededAsync(factory)).Should().Be(seeded);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet build AiObservatory.slnx --configuration Release`
Expected: FAIL to compile with `The name 'DemoSeeder' does not exist in the current context`.

- [ ] **Step 4: Create `DemoSeeder` by moving the existing seed code**

Create `src/AiObservatory.Api/DemoSeeder.cs` with this skeleton, then move code into it exactly as described under it:

```csharp
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api;

/// <summary>
/// The synthetic <c>demo-seed</c> dataset: what the local Compose quick start and the public
/// demo instance show. <see cref="HasAnyDataAsync"/> and <see cref="ClearAsync"/> share one
/// table list, so the empty-database guard and the reset cannot drift apart.
/// </summary>
internal static class DemoSeeder
{
    // Arbitrary constant; serialises overlapping resets (nightly schedule vs manual dispatch).
    private const long ResetLockKey = 7_272_700_001;

    public static async Task<bool> HasAnyDataAsync(AiObservatoryDbContext db, CancellationToken ct) =>
        await db.DailyAggregates.AnyAsync(ct)
        || await db.Subscriptions.AnyAsync(ct)
        || await db.Insights.AnyAsync(ct)
        || await db.BudgetRules.AnyAsync(ct)
        || await db.UsageEvents.AnyAsync(ct)
        || await db.SpendEntries.AnyAsync(ct);

    /// <summary>
    /// Deletes every row in the tables <see cref="SeedAsync"/> writes. The vendor and category
    /// catalog is migration data and is deliberately left alone. Only call this where the data
    /// is disposable: the demo reset does, and the dev seed route never does.
    /// </summary>
    public static async Task ClearAsync(AiObservatoryDbContext db, CancellationToken ct)
    {
        await db.DailyAggregates.ExecuteDeleteAsync(ct);
        await db.Subscriptions.ExecuteDeleteAsync(ct);
        await db.Insights.ExecuteDeleteAsync(ct);
        await db.BudgetRules.ExecuteDeleteAsync(ct);
        await db.UsageEvents.ExecuteDeleteAsync(ct);
        await db.SpendEntries.ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Wipes and reseeds atomically, so a visitor never sees an empty dashboard and a failed
    /// seed rolls the wipe back. The advisory lock makes an overlapping second reset wait for
    /// the first instead of interleaving deletes and inserts.
    /// </summary>
    public static async Task ResetAsync(AiObservatoryDbContext db, IClock clock, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ResetLockKey})", ct);
        await ClearAsync(db, ct);
        await SeedAsync(db, clock, ct);
        await transaction.CommitAsync(ct);
    }

    public static async Task SeedAsync(AiObservatoryDbContext db, IClock clock, CancellationToken ct)
    {
        // MOVE: the body of the old /dev/seed lambda, from `var today = ...` (Program.cs:210)
        // through `await db.SaveChangesAsync(ct);` (Program.cs:403). Do not retype it.
    }

    // MOVE: SeedBilledMonthlyGbp, SeedBilledVendorKeys and SeedBilledLedgerAsync
    // (Program.cs:436-515), unchanged, including their doc comments.
}
```

Move steps, in order:

1. Cut `Program.cs` lines 210-403 (from `var today = clock.GetCurrentInstant().InUtc().Date;` through `await db.SaveChangesAsync(ct);`) and paste them as the body of `SeedAsync`. Delete the two comment lines directly above `var today` (the "No TRUNCATE" note); they describe the lambda's guard, which stays in the route.
2. Cut `Program.cs` lines 436-515 (`SeedBilledMonthlyGbp` through the end of `SeedBilledLedgerAsync`) from the `Program` partial class and paste them into `DemoSeeder` as `private static` members. In `SeedAsync`, the call `SeedBilledLedgerAsync(db, today, clock.GetCurrentInstant(), ct)` now resolves locally.
3. Replace the `/dev/seed` lambda in `Program.cs` so the route reads:

```csharp
api.MapPost(
    "/dev/seed",
    async (AiObservatoryDbContext db, IClock clock, CancellationToken ct) =>
    {
        // Idempotent: only seed a genuinely empty database. The Docker compose seed service
        // calls this on every `up`; the guard must cover EVERY table the seed writes (see
        // DemoSeeder.HasAnyDataAsync) or a self-hoster's pre-existing config is destroyed.
        if (await DemoSeeder.HasAnyDataAsync(db, ct))
        {
            return Results.Ok("Already seeded — skipping (data present).");
        }

        await DemoSeeder.SeedAsync(db, clock, ct);
        return Results.Ok("Seed successful");
    }
);
```

Leave it inside the existing `if (app.Environment.IsDevelopment())` block for now; Task 3 widens the condition.

- [ ] **Step 5: Run the new and the old seed tests**

Run: `dotnet test --solution AiObservatory.slnx --configuration Release`
Expected: PASS, including the 3 pre-existing `DevSeedEndpointTests` and the 4 new `DemoSeederTests`. If a pre-existing seed test fails, the move changed behaviour: diff `Program.cs` against `origin/main` and fix the move, not the test.

- [ ] **Step 6: Format and commit**

Run: `dotnet csharpier check .`
Expected: no output. If it reports line-ending-only differences with an empty `git diff`, ignore them.

```bash
git add src/AiObservatory.Api/DemoSeeder.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.IntegrationTests/DemoSeederTests.cs
git commit -m "refactor: extract DemoSeeder and add a transactional reset"
```

---

### Task 2: Demo-mode setting and database startup guard

**Files:**
- Create: `src/AiObservatory.Api/DemoMode.cs`
- Modify: `src/AiObservatory.Api/Program.cs` (after the `DB_CONNECTION` read at line ~36-39)
- Modify: `tests/AiObservatory.Api.IntegrationTests/AiObservatoryApiFactory.cs`
- Test: `tests/AiObservatory.Api.Tests/DemoModeTests.cs`, and additions to `tests/AiObservatory.Api.IntegrationTests/StartupGuardsTests.cs`

**Interfaces:**
- Produces on `AiObservatory.Api.DemoMode` (`internal static`):
  - `const string SettingName = "OBSERVATORY_DEMO_MODE"`
  - `bool IsEnabled(IConfiguration configuration)`
  - `void EnsureSafeDatabase(string connectionString)` — throws `InvalidOperationException` naming `OBSERVATORY_DEMO_MODE` and the offending host (never the password)
- Produces on `AiObservatoryApiFactory`: `public bool DemoMode { get; set; }`, which sets `OBSERVATORY_DEMO_MODE` for the host.

- [ ] **Step 1: Write the failing unit tests**

Create `tests/AiObservatory.Api.Tests/DemoModeTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace AiObservatory.Api.Tests;

public class DemoModeTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("1", false)]
    public void IsEnabled_only_for_the_literal_word_true(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DemoMode.SettingName] = value })
            .Build();

        DemoMode.IsEnabled(configuration).Should().Be(expected);
    }

    [Theory]
    [InlineData("Host=fpaiodemo-db.postgres.database.azure.com;Database=aiobservatory;Username=u;Password=p")]
    [InlineData("Host=FPAIODEMO-DB.postgres.database.azure.com;Database=a;Username=u;Password=p")]
    [InlineData("Host=localhost;Database=a;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=a;Username=u;Password=p")]
    [InlineData("Host=db;Database=a;Username=u;Password=p")]
    public void EnsureSafeDatabase_accepts_demo_and_local_hosts(string connection)
    {
        var act = () => DemoMode.EnsureSafeDatabase(connection);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Host=fpaiobs-db.postgres.database.azure.com;Database=a;Username=u;Password=secret-pw")]
    [InlineData("Host=FPAIOBS-DB.postgres.database.azure.com;Database=a;Username=u;Password=secret-pw")]
    [InlineData("Host=fpaiodemo-db.postgres.database.azure.com,fpaiobs-db.postgres.database.azure.com;Database=a;Username=u;Password=secret-pw")]
    [InlineData("Database=a;Username=u;Password=secret-pw")]
    public void EnsureSafeDatabase_rejects_everything_else_without_leaking_the_password(string connection)
    {
        var act = () => DemoMode.EnsureSafeDatabase(connection);

        act.Should()
            .Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("OBSERVATORY_DEMO_MODE") && !e.Message.Contains("secret-pw"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build AiObservatory.slnx --configuration Release`
Expected: FAIL to compile, `The name 'DemoMode' does not exist`.

- [ ] **Step 3: Implement `DemoMode`**

Create `src/AiObservatory.Api/DemoMode.cs`:

```csharp
using Npgsql;

namespace AiObservatory.Api;

/// <summary>
/// Demo mode turns the API into a disposable, synthetic-data instance: seed and reset routes
/// exist, every other write is refused, the background worker does not run. It must never be
/// switched on against real data, so enabling it also requires the database to look like a
/// demo (or local) database.
/// </summary>
internal static class DemoMode
{
    public const string SettingName = "OBSERVATORY_DEMO_MODE";

    private const string DemoHostPrefix = "fpaiodemo-";

    // Loopback for local runs and CI Testcontainers; `db` is the Compose service name.
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "db"];

    public static bool IsEnabled(IConfiguration configuration) =>
        string.Equals(configuration[SettingName], "true", StringComparison.OrdinalIgnoreCase);

    public static void EnsureSafeDatabase(string connectionString)
    {
        var hostValue = new NpgsqlConnectionStringBuilder(connectionString).Host ?? string.Empty;
        var hosts = hostValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Every listed host must qualify: one safe host in a failover list must not vouch for
        // an unsafe one beside it. Only the host is reported, never the credentials.
        if (hosts.Length > 0 && hosts.All(IsDemoOrLocal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{SettingName}=true refused to start: database host '{hostValue}' is not a demo database. "
                + $"Demo hosts start with '{DemoHostPrefix}'; local runs may use {string.Join(", ", LocalHosts)}."
        );
    }

    private static bool IsDemoOrLocal(string host) =>
        host.StartsWith(DemoHostPrefix, StringComparison.OrdinalIgnoreCase)
        || LocalHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run the unit tests to verify they pass**

Run: `dotnet test --solution AiObservatory.slnx --configuration Release`
Expected: PASS for `DemoModeTests` (13 cases).

- [ ] **Step 5: Add factory support and the failing wiring tests**

In `AiObservatoryApiFactory.cs`, add the property next to the other overrides:

```csharp
    /// <summary>Sets OBSERVATORY_DEMO_MODE for the host (Production-style startup, demo routes).</summary>
    public bool DemoMode { get; set; }
```

and in `CreateHost`, next to the other `SetEnvironmentVariable` calls:

```csharp
        System.Environment.SetEnvironmentVariable("OBSERVATORY_DEMO_MODE", DemoMode ? "true" : null);
```

Append to `StartupGuardsTests` (inside the class, reusing its existing `CaptureServicesException` and `ExceptionChainContains` helpers):

```csharp
    [Fact]
    public async Task Startup_WhenDemoModeAndDatabaseHostIsNotADemoHost_Throws()
    {
        await using var factory = new AiObservatoryApiFactory
        {
            Environment = Environments.Production,
            DemoMode = true,
        };
        factory.SetDbConnection("Host=fpaiobs-db.postgres.database.azure.com;Database=a;Username=u;Password=p");

        var thrown = CaptureServicesException(factory);

        thrown.Should().NotBeNull();
        ExceptionChainContains(thrown, "OBSERVATORY_DEMO_MODE")
            .Should()
            .BeTrue($"the exception chain should name the demo-mode guard; got: {thrown}");
    }

    [Fact]
    public async Task Startup_WhenDemoModeIsOffAndDatabaseHostIsNotADemoHost_DoesNotApplyTheDemoGuard()
    {
        // Production uses a non-demo host and must start exactly as it does today.
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Production };

        var thrown = CaptureServicesException(factory);

        (thrown is null || !ExceptionChainContains(thrown, "OBSERVATORY_DEMO_MODE"))
            .Should()
            .BeTrue();
    }
```

- [ ] **Step 6: Run to verify the first new test fails**

Run: `dotnet test --solution AiObservatory.slnx --configuration Release`
Expected: `Startup_WhenDemoModeAndDatabaseHostIsNotADemoHost_Throws` FAILS (nothing reads the setting yet).

- [ ] **Step 7: Wire the guard into `Program.cs`**

Directly after `builder.Services.AddDataLayer(dbConnection);` (line ~39) add:

```csharp
var demoMode = DemoMode.IsEnabled(builder.Configuration);
if (demoMode)
{
    DemoMode.EnsureSafeDatabase(dbConnection);
}
```

- [ ] **Step 8: Run all tests, then commit**

Run: `dotnet test --solution AiObservatory.slnx --configuration Release`
Expected: PASS.

```bash
git add src/AiObservatory.Api/DemoMode.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.Tests/DemoModeTests.cs tests/AiObservatory.Api.IntegrationTests/AiObservatoryApiFactory.cs tests/AiObservatory.Api.IntegrationTests/StartupGuardsTests.cs
git commit -m "feat: add OBSERVATORY_DEMO_MODE with a demo-database startup guard"
```

---

### Task 3: Demo-mode routes, write block and worker skip

**Files:**
- Create: `src/AiObservatory.Api/DemoWriteBlockMiddleware.cs`
- Modify: `src/AiObservatory.Api/Program.cs` (worker registration line ~92; pipeline after `app.UseRateLimiter()` line ~172; the dev block at ~184)
- Test: `tests/AiObservatory.Api.IntegrationTests/DemoModeEndpointTests.cs`

**Interfaces:**
- Consumes: `DemoMode.IsEnabled`, `demoMode` local from Task 2, `DemoSeeder.*` from Task 1, `AiObservatoryApiFactory.DemoMode`.
- Produces: `POST /api/dev/reset-demo` (admin key), and the 403 policy for all other non-safe methods.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiObservatory.Api.IntegrationTests/DemoModeEndpointTests.cs`:

```csharp
using System.Net;
using AiObservatory.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api.IntegrationTests;

/// <summary>
/// Demo mode runs the real composition root in Production with the demo flag on: the seed and
/// reset routes exist, nothing else can write, and without the flag none of that exists.
/// </summary>
[Trait("Category", "Integration")]
public class DemoModeEndpointTests
{
    private static AiObservatoryApiFactory DemoFactory() =>
        new() { Environment = Environments.Production, DemoMode = true };

    [Fact]
    public async Task Reset_with_the_admin_key_seeds_an_empty_demo_database()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/dev/reset-demo", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        (await db.DailyAggregates.AnyAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task Reset_with_the_read_only_key_is_rejected()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateReadOnlyClient();

        var response = await client.PostAsync("/api/dev/reset-demo", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("POST", "/api/subscriptions")]
    [InlineData("PUT", "/api/subscriptions/00000000-0000-0000-0000-000000000001")]
    [InlineData("PATCH", "/api/subscriptions/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/subscriptions/00000000-0000-0000-0000-000000000001")]
    public async Task Writes_are_refused_even_with_the_admin_key(string method, string path)
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_IDE_route_group_is_blocked_too()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateIdeClient();

        var response = await client.PostAsync("/api/ide/v1/events", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reads_still_work_with_the_read_only_key()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using (var admin = factory.CreateAdminClient())
        {
            (await admin.PostAsync("/api/dev/reset-demo", content: null, TestContext.Current.CancellationToken))
                .EnsureSuccessStatusCode();
        }
        using var client = factory.CreateReadOnlyClient();

        var response = await client.GetAsync("/api/aggregates", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CORS_preflight_is_not_blocked()
    {
        await using var factory = DemoFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateAnonymousClient();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/aggregates");
        preflight.Headers.Add("Origin", "https://example.test");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-observatory-key");

        var response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Without_demo_mode_the_reset_route_does_not_exist_in_Production()
    {
        await using var factory = new AiObservatoryApiFactory { Environment = Environments.Production };
        await factory.InitializeAsync();
        using var client = factory.CreateAdminClient();

        var response = await client.PostAsync("/api/dev/reset-demo", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --solution AiObservatory.slnx --configuration Release`
Expected: the demo-mode tests FAIL (reset returns 404, writes are not 403). `Without_demo_mode_the_reset_route_does_not_exist_in_Production` and `Reads_still_work...` may already pass or fail on the missing seed route; that is fine.

- [ ] **Step 3: Create the middleware**

Create `src/AiObservatory.Api/DemoWriteBlockMiddleware.cs`:

```csharp
namespace AiObservatory.Api;

/// <summary>
/// Demo mode is read-only for everyone, including the admin key. A middleware rather than an
/// endpoint filter on purpose: the IDE route group (<c>/api/ide/v1</c>) has its own filter and
/// accepts POSTs, and a filter on the <c>/api</c> group would not see it. Seed and reset stay
/// reachable; both are still admin-key-gated by <see cref="ApiKeyEndpointFilter"/>.
/// </summary>
public sealed class DemoWriteBlockMiddleware(RequestDelegate next)
{
    private static readonly string[] ExemptPaths = ["/api/dev/seed", "/api/dev/reset-demo"];

    public Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        if (safe || ExemptPaths.Contains(context.Request.Path.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsync("This is a read-only demo instance.");
    }
}
```

- [ ] **Step 4: Wire `Program.cs`**

a. Worker registration (line ~92): replace `builder.Services.AddHostedService<IntelligenceWorkerService>();` with

```csharp
// Demo mode has no real data to analyse and must not call Anthropic, send alerts or sync
// GitHub billing.
if (!demoMode)
{
    builder.Services.AddHostedService<IntelligenceWorkerService>();
}
```

b. Pipeline: after `app.UseRateLimiter();` and before the `if (authEnabled)` block add

```csharp
if (demoMode)
{
    // After UseCors so a preflight is answered before it reaches the block.
    app.UseMiddleware<DemoWriteBlockMiddleware>();
}
```

c. Routes: change the `if (app.Environment.IsDevelopment())` block so OpenAPI stays development-only while the seed route is also available in demo mode, and add the reset route:

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (app.Environment.IsDevelopment() || demoMode)
{
    api.MapPost("/dev/seed", /* the lambda from Task 1, unchanged */);
}

if (demoMode)
{
    api.MapPost(
        "/dev/reset-demo",
        async (AiObservatoryDbContext db, IClock clock, CancellationToken ct) =>
        {
            await DemoSeeder.ResetAsync(db, clock, ct);
            return Results.Ok("Demo reset");
        }
    );
}
```

- [ ] **Step 5: Run the full .NET suite**

Run: `dotnet test --solution AiObservatory.slnx --configuration Release`
Expected: PASS, including the 10 new `DemoModeEndpointTests` cases and the existing `StartupGuardsTests` test that `/api/dev/seed` is not reachable in Production without demo mode.

- [ ] **Step 6: Format and commit**

Run: `dotnet csharpier check .`

```bash
git add src/AiObservatory.Api/DemoWriteBlockMiddleware.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.IntegrationTests/DemoModeEndpointTests.cs
git commit -m "feat: demo mode seed, reset, write block and worker skip"
```

---

### Task 4: Frontend demo banner

**Files:**
- Create: `src/AiObservatory.Web/src/components/DemoBanner.tsx`
- Modify: `src/AiObservatory.Web/src/App.tsx`, `src/AiObservatory.Web/src/vite-env.d.ts`
- Test: `src/AiObservatory.Web/src/components/DemoBanner.test.tsx`

**Interfaces:**
- Produces: default export `DemoBanner()`, renders nothing unless `import.meta.env.VITE_DEMO_BANNER === 'true'` (read at render time so tests can stub it).

- [ ] **Step 1: Write the failing test**

Create `src/AiObservatory.Web/src/components/DemoBanner.test.tsx`:

```tsx
import { afterEach, expect, test, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import DemoBanner from './DemoBanner'

afterEach(() => vi.unstubAllEnvs())

test('shows the synthetic-data notice when VITE_DEMO_BANNER is true', () => {
  vi.stubEnv('VITE_DEMO_BANNER', 'true')
  render(<DemoBanner />)
  expect(screen.getByRole('status')).toHaveTextContent(/synthetic/i)
  expect(screen.getByRole('link', { name: /github/i })).toHaveAttribute(
    'href',
    'https://github.com/FixPortal/fixportal-observatory',
  )
})

test.each(['', 'false', 'TRUE'])('renders nothing when VITE_DEMO_BANNER is %j', value => {
  vi.stubEnv('VITE_DEMO_BANNER', value)
  const { container } = render(<DemoBanner />)
  expect(container).toBeEmptyDOMElement()
})
```

- [ ] **Step 2: Run to verify failure**

Run (in `src/AiObservatory.Web`): `npx vitest run src/components/DemoBanner.test.tsx`
Expected: FAIL, `Failed to resolve import "./DemoBanner"`.

- [ ] **Step 3: Implement**

Create `src/AiObservatory.Web/src/components/DemoBanner.tsx`:

```tsx
const PROJECT_URL = 'https://github.com/FixPortal/fixportal-observatory'

// Read inside the component, not at module scope, so a test (or a rebuild) can flip it.
export default function DemoBanner() {
  if (import.meta.env.VITE_DEMO_BANNER !== 'true') return null

  return (
    <div
      role="status"
      style={{ padding: '0.5rem 1rem', textAlign: 'center', background: '#1f2a44', color: '#f3f4f6', fontSize: '0.875rem' }}
    >
      Demo instance: every figure here is synthetic and the dashboard is read-only.{' '}
      <a href={PROJECT_URL} target="_blank" rel="noopener noreferrer" style={{ color: 'inherit', textDecoration: 'underline' }}>
        Observatory on GitHub
      </a>
    </div>
  )
}
```

Add to `vite-env.d.ts` inside `ImportMetaEnv`:

```ts
  /** "true" shows the synthetic-data banner on the public demo build. */
  readonly VITE_DEMO_BANNER?: string
```

In `App.tsx` add `import DemoBanner from './components/DemoBanner'` and render `<DemoBanner />` as the first child inside `<QueryClientProvider client={queryClient}>`, above `<EmbeddedContext>`.

- [ ] **Step 4: Run the frontend gate**

Run (in `src/AiObservatory.Web`): `npm run lint`, then `npm test -- --coverage`, then `npm run build`
Expected: lint clean, all tests pass (the existing `App.test.tsx` is unaffected because the variable is unset), build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Web/src/components/DemoBanner.tsx src/AiObservatory.Web/src/components/DemoBanner.test.tsx src/AiObservatory.Web/src/App.tsx src/AiObservatory.Web/src/vite-env.d.ts
git commit -m "feat: show a synthetic-data banner on the demo build"
```

---

### Task 5: Parameterise the Bicep for a second stack

**Files:**
- Modify: `infra/main.bicep`, `infra/modules/appservice.bicep`, `infra/modules/swa.bicep`

**Interfaces:**
- Produces new `main.bicep` params (defaults preserve production exactly): `deployIngest bool = true`, `demoMode bool = false`, `swaOrigin string = 'https://observatory.fixportal.org'`, `swaCustomDomain string = 'observatory.fixportal.org'`.

- [ ] **Step 1: Capture the baseline compile of production's template**

Run: `az bicep build --file infra/main.bicep --outfile $env:TEMP\main.before.json`
Expected: exits 0. (Use the scratchpad directory if the harness prefers; the file is only for the comparison in Step 6.)

- [ ] **Step 2: `appservice.bicep` — add parameters and conditional settings**

Add near the other params:

```bicep
param demoMode bool = false
param swaOrigin string = 'https://observatory.fixportal.org'
```

Replace the `appSettings` array with `concat` of three arrays, keeping every existing entry's name and value (move, do not edit): the always-on settings (`DB_CONNECTION`, the three `OBSERVATORY_*` keys, `APPLICATIONINSIGHTS_CONNECTION_STRING`, the four `AzureAd__*`/`SWA_ORIGIN` entries), the integration settings that exist only outside demo mode (`ANTHROPIC_API_KEY`, `GITHUB_TOKEN`, `GITHUB_BILLING_ORG`, with their existing comment), and one demo setting:

```bicep
      appSettings: concat(
        coreSettings,
        demoMode ? [{ name: 'OBSERVATORY_DEMO_MODE', value: 'true' }] : integrationSettings
      )
```

with `coreSettings` and `integrationSettings` declared as `var` arrays above the resource, and `SWA_ORIGIN`'s value changed from the literal to `swaOrigin`.

- [ ] **Step 3: `swa.bicep` — make the custom domain optional**

```bicep
param swaCustomDomain string = 'observatory.fixportal.org'

resource customDomain 'Microsoft.Web/staticSites/customDomains@2023-01-01' = if (!empty(swaCustomDomain)) {
  parent: swa
  name: swaCustomDomain
}
```

- [ ] **Step 4: `main.bicep` — thread the parameters**

Add the four params with the defaults above; pass `demoMode`/`swaOrigin` to `appservice` and `swaCustomDomain` to `swa`; make ingest conditional and guard its output use:

```bicep
module ingest 'modules/ingest.bicep' = if (deployIngest) { ... existing body ... }
```

and in the `postgresql` module:

```bicep
    allowedIps: union(
      split(appservice.outputs.possibleOutboundIpAddresses, ','),
      deployIngest ? split(ingest!.outputs.possibleOutboundIpAddresses, ',') : []
    )
```

Leave the Entra defaults alone; the demo passes blank values at deploy time (Task 6).

- [ ] **Step 5: Compile both shapes**

Run: `az bicep build --file infra/main.bicep --outfile $env:TEMP\main.after.json`
Expected: exits 0 with no new warnings relative to Step 1 (warnings the baseline already had are acceptable; read the output rather than assuming).

- [ ] **Step 6: Compare production's appSettings before and after**

Run: `pwsh -NoProfile -Command "$b=(Get-Content $env:TEMP\main.before.json -Raw|ConvertFrom-Json); $a=(Get-Content $env:TEMP\main.after.json -Raw|ConvertFrom-Json); ($b.resources|ConvertTo-Json -Depth 40 -Compress).Length; ($a.resources|ConvertTo-Json -Depth 40 -Compress).Length"`
Expected: the sizes differ (the template changed); the real check is reading the compiled `appservice` module's `appSettings` in both outputs and confirming every production setting name and value still appears with `demoMode` false. State what you compared.

UNVERIFIED: that production's deployed configuration is unchanged. Refuted if the operator's `az deployment group what-if -g fpaiobs-rg -f infra/main.bicep` shows any App Service setting or the SWA custom domain being added, changed or removed. The operator runs that what-if before the next `infra.yml` dispatch.

- [ ] **Step 7: Commit**

```bash
git add infra/main.bicep infra/modules/appservice.bicep infra/modules/swa.bicep
git commit -m "infra: parameterise swa origin, custom domain, ingest and demo mode"
```

---

### Task 6: Bootstrap script, demo workflows and docs

**Files:**
- Create: `infra/scripts/bootstrap-demo.ps1`, `.github/workflows/deploy-demo.yml`, `.github/workflows/demo-reset.yml`, `docs/demo.md`
- Modify: `README.md` (one link under the quick-start section)

**Interfaces:**
- Consumes: Bicep params from Task 5; the demo routes from Task 3; the banner variable from Task 4.
- Produces: GitHub environment `demo` with variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `DEMO_API_URL`, `DEMO_READONLY_KEY` and secrets `DEMO_ADMIN_KEY`, `DEMO_SWA_DEPLOYMENT_TOKEN`.

- [ ] **Step 1: Read the PowerShell traps note**

Open and read `~/.agents/notes/powershell-traps.md` before writing the script; apply anything relevant (native-exe exit codes, `pwsh -File` array arguments, quoting).

- [ ] **Step 2: Write `infra/scripts/bootstrap-demo.ps1`**

One re-runnable operator script. Requirements, in order, each followed by `if ($LASTEXITCODE -ne 0) { throw '...' }`:

1. Parameters: `-Location` (default `westeurope`), `-Prefix` (default `fpaiodemo`), `-Repo` (default `FixPortal/fixportal-observatory`). Refuse to run if `-Prefix` equals `fpaiobs`.
2. `az group create` for `$Prefix-rg`.
3. Generate three hex keys with `[Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))` (admin, read-only, IDE; 48 characters each) and a hex database password.
4. `az postgres flexible-server create` named `$Prefix-db`: Burstable `Standard_B1ms`, version 16, 32 GB, `--public-access None`; then `az postgres flexible-server db create` for `aiobservatory`.
5. `az staticwebapp create` named `$Prefix-swa`, Free SKU; read its deployment token and default hostname.
6. `az deployment group create -g $Prefix-rg -f infra/main.bicep` with parameters `prefix=$Prefix deployIngest=false demoMode=true aadTenantId='' aadClientId='' swaCustomDomain='' swaOrigin=https://<swa default hostname>`.
7. Give the signed-in user the `Key Vault Secrets Officer` role on `$Prefix-kv`, then set secrets `db-connection` (`Host=$Prefix-db.postgres.database.azure.com;Database=aiobservatory;Username=<admin>;Password=<generated>;Ssl Mode=Require`), `observatory-api-key`, `observatory-readonly-api-key`, `observatory-ide-api-key`.
8. `az webapp restart` for `$Prefix-api`.
9. Create the GitHub environment and set its variables and secrets with `gh`, piping secret values through stdin so they never appear in a command line or the transcript. Print the API URL and SWA hostname, and print nothing secret.

The script must not echo any key or password.

- [ ] **Step 3: Parse-check the script**

Run: `pwsh -NoProfile -Command "$null = [System.Management.Automation.Language.Parser]::ParseFile('infra/scripts/bootstrap-demo.ps1',[ref]$null,[ref]$e); if ($e) { $e; exit 1 }"`
Expected: no output, exit 0.

UNVERIFIED: that the script provisions a working stack end to end. It creates paid Azure resources and cannot be dry-run here. Refuted if the operator's first run fails, or the first deploy's `/api/aggregates` returns anything other than 200 for the demo read-only key.

- [ ] **Step 4: Write `.github/workflows/deploy-demo.yml`**

`workflow_dispatch` only, `environment: demo`, `if: github.ref == 'refs/heads/main'`, `permissions: contents: read` plus `id-token: write` on the Azure job. Two jobs, copying the structure of `deploy.yml` (pinned action SHAs as already used there, `persist-credentials: false`, `timeout-minutes: 20`):

- `deploy-api`: checkout, setup-dotnet, `dotnet publish src/AiObservatory.Api -c Release -o ./publish/api`, `azure/login` using the environment's OIDC variables, `azure/webapps-deploy` to `fpaiodemo-api`, then a bounded probe that expects exactly `401` from `${{ vars.DEMO_API_URL }}/api/aggregates` (same loop as `deploy.yml`'s "Verify the API is serving").
- `deploy-web`: checkout, setup-node, `npm ci` and `npm run build` in `src/AiObservatory.Web` with `VITE_API_BASE: ${{ vars.DEMO_API_URL }}`, `VITE_API_KEY: ${{ vars.DEMO_READONLY_KEY }}`, `VITE_DEMO_BANNER: 'true'` and no `VITE_AAD_*`, then `Azure/static-web-apps-deploy` (same pinned SHA as `deploy.yml`) with `secrets.DEMO_SWA_DEPLOYMENT_TOKEN`.

Do not reference `vars.*` or `secrets.*` of the `production` environment anywhere in this file.

- [ ] **Step 5: Write `.github/workflows/demo-reset.yml`**

```yaml
name: Demo reset

on:
  schedule:
    - cron: '17 3 * * *'
  workflow_dispatch:

concurrency:
  group: demo-reset
  cancel-in-progress: false

permissions:
  contents: read

jobs:
  reset:
    name: Reset demo data
    runs-on: ubuntu-latest
    timeout-minutes: 10
    environment: demo
    steps:
      - name: Reset the demo database
        env:
          DEMO_API_URL: ${{ vars.DEMO_API_URL }}
          DEMO_ADMIN_KEY: ${{ secrets.DEMO_ADMIN_KEY }}
        run: |
          code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 -X POST \
            -H "X-Observatory-Key: $DEMO_ADMIN_KEY" "$DEMO_API_URL/api/dev/reset-demo")
          echo "reset -> HTTP $code"
          [ "$code" = "200" ] || { echo "::error::Demo reset returned HTTP $code"; exit 1; }
```

The `environment: demo` restricts this to the demo environment's branch policy; scheduled runs use the default branch.

- [ ] **Step 6: Validate the workflows with the repo's own checks**

Run: `python -m pytest .github/scripts -q`, `python .github/scripts/assert_canonical_assets.py`, and `python .github/scripts/assert_workflow_hygiene.py` (read `ci.yml`'s "workflow-lint" job for the exact arguments the hygiene checker takes, and use those).
Expected: all pass. If the hygiene checker flags the new workflows, fix the workflows to the repo's rule; do not weaken the checker.

- [ ] **Step 7: Write `docs/demo.md` and link it**

Document: what the demo is and is not; the one-time setup order (run `bootstrap-demo.ps1`, add the DNS record, first `deploy-demo.yml` dispatch, then dispatch `demo-reset.yml`); how to rotate the read-only key (set the Key Vault secret, restart the API, update `DEMO_READONLY_KEY`, redeploy the web); the cost note (check SKUs against the Azure price calculator); that the demo has no ingest worker and no real credentials by design. Add one line to `README.md` under Quick start linking to `docs/demo.md`.

- [ ] **Step 8: Commit**

```bash
git add infra/scripts/bootstrap-demo.ps1 .github/workflows/deploy-demo.yml .github/workflows/demo-reset.yml docs/demo.md README.md
git commit -m "feat: demo bootstrap script, deploy and reset workflows, and docs"
```

---

### Task 7: Full local gate

- [ ] **Step 1:** Run the entire pre-push gate from Global Constraints, in order. Every step must pass; read the counts, not the word "passed" (the pre-change baseline in `docs/oss-qualification.md` is 1,402 .NET tests with one intentional skip and 367 frontend tests; expect the totals to rise by the tests this plan adds).
- [ ] **Step 2:** Run `pwsh -File .github/ai-smells/pr-detect.ps1 -RepoPath . -Base origin/main -Head HEAD -OutFile <scratch>.json` and fix every hit or record a one-line reason for it.
- [ ] **Step 3:** Push once and open the PR through the repo's finishing-a-development-branch flow (which writes the PR-gate sentinel). Do not push earlier.

## Self-review notes

- **Spec coverage:** infrastructure (Tasks 5, 6), API demo mode (Tasks 2, 3), reset (Task 1, 3), frontend (Task 4), CI/CD (Task 6), testing (each task), out-of-scope items untouched. Deviations are listed at the top.
- **Type consistency:** `DemoSeeder.{HasAnyDataAsync, ClearAsync, SeedAsync, ResetAsync}`, `DemoMode.{SettingName, IsEnabled, EnsureSafeDatabase}`, `DemoWriteBlockMiddleware`, `AiObservatoryApiFactory.DemoMode` and the `demoMode` local in `Program.cs` are used under those names in every task.
- **Known gap:** the frontend banner uses inline styles rather than the stylesheet's tokens; restyle it with the design tokens when the demo is first viewed.
