# Claude Activity Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an admin-only "Activity" tab showing how much active time has been spent in Claude Code, broken down by day and by project.

**Architecture:** A new `ClaudeActivitySession` entity (one row per Claude Code session, upserted as the session grows) feeds two read endpoints — daily totals and per-project totals — gated by a new admin-only API key filter (stricter than the existing readonly-or-admin filter, since project/repo names are more revealing than aggregate spend). The frontend adds a 4th dashboard tab with a trend chart, a sortable project table, and a proportional treemap, both driven by the same per-project data and cross-linked by a shared "selected project" filter.

**Tech Stack:** ASP.NET Core minimal APIs, EF Core + Npgsql, NodaTime, React + TanStack Query, Recharts, Vitest.

**Out of scope for this plan:** the capture/backfill change in `observe-sweep.ps1` (private hook, lives outside this repo — see the spec's wire contract for what it must POST) and the GitHub-activity sub-project (separate spec, not started).

## Global Constraints

- Entities in `AiObservatory.Data.Entities` must be `sealed` (enforced by `ArchitectureTests.Model_types_must_be_sealed`).
- Async methods must end in `Async` (enforced by `ArchitectureTests`, via `FixPortalArchRules.AsyncMethodsMustEndInAsync`).
- Interfaces must start with `I` (only relevant if a new interface is introduced — this plan introduces none).
- New assertions in C# tests use AwesomeAssertions `.Should()`, never `Assert.*`.
- New frontend tests import `test`/`expect`/etc. explicitly from `vitest` (no globals).
- Frontend build gate before any push: `npx tsc -b --noEmit`, `npx eslint .`, `npx vitest run` (all from `src/AiObservatory.Web`).
- Backend build gate before any push: `dotnet build`, `dotnet test` across the touched projects.
- No new dependencies — every package used below (NodaTime, EF Core, Recharts, TanStack Query, AwesomeAssertions, NSubstitute, Vitest, RTL) is already referenced.

---

### Task 1: `ClaudeActivitySession` entity, DbContext registration, migration

**Files:**
- Create: `src/AiObservatory.Data/Entities/ClaudeActivitySession.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Create (generated): `src/AiObservatory.Data/Migrations/<timestamp>_AddClaudeActivitySession.cs` and `.Designer.cs`
- Modify (generated): `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: `AiObservatory.Data.Entities.ClaudeActivitySession` with properties `Id (Guid)`, `SessionId (string)`, `Project (string)`, `StartedAt (Instant)`, `LastSeenAt (Instant)`, `ActiveSeconds (long)`, `IngestedAt (Instant)`. `AiObservatoryDbContext.ClaudeActivitySessions` (`DbSet<ClaudeActivitySession>`). Both consumed by Task 3.

- [ ] **Step 1: Create the entity**

```csharp
// src/AiObservatory.Data/Entities/ClaudeActivitySession.cs
using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class ClaudeActivitySession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SessionId { get; init; } = "";
    public string Project { get; init; } = "";
    public Instant StartedAt { get; init; }
    public Instant LastSeenAt { get; init; }
    public long ActiveSeconds { get; init; }
    public Instant IngestedAt { get; init; }
}
```

- [ ] **Step 2: Register the DbSet and model configuration**

In `src/AiObservatory.Data/AiObservatoryDbContext.cs`, add the DbSet alongside the existing ones:

```csharp
    public DbSet<CavemanSession> CavemanSessions => Set<CavemanSession>();
    public DbSet<ClaudeActivitySession> ClaudeActivitySessions => Set<ClaudeActivitySession>();
```

Add a configuration block in `OnModelCreating`, after the `CavemanSession` block:

```csharp
        modelBuilder.Entity<ClaudeActivitySession>(b =>
        {
            b.Property(s => s.SessionId).HasMaxLength(200).IsRequired();
            b.Property(s => s.Project).HasMaxLength(200).IsRequired();
            b.HasIndex(s => s.SessionId).IsUnique();
            b.HasIndex(s => s.StartedAt);
            b.HasIndex(s => s.Project);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ClaudeActivitySession_ActiveSeconds_NonNegative", "\"ActiveSeconds\" >= 0");
            });
        });
```

- [ ] **Step 3: Build the Data project**

Run: `dotnet build src/AiObservatory.Data/AiObservatory.Data.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Generate the migration**

Run: `dotnet ef migrations add AddClaudeActivitySession --project src/AiObservatory.Data --startup-project src/AiObservatory.Api`
Expected: creates `Migrations/<timestamp>_AddClaudeActivitySession.cs` and `.Designer.cs`, and updates `AiObservatoryDbContextModelSnapshot.cs`. Open the generated `Up()` method and confirm it contains a `migrationBuilder.CreateTable(name: "ClaudeActivitySessions", ...)` with the unique index on `SessionId` and the check constraint — if EF named the check constraint differently than `CK_ClaudeActivitySession_ActiveSeconds_NonNegative`, that's fine, EF Core picks its own names for constraints unless told otherwise; just confirm the constraint clause itself reads `"ActiveSeconds" >= 0`.

- [ ] **Step 5: Confirm the architecture test still passes**

Run: `dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~ArchitectureTests"`
Expected: `Passed!` (the new entity is `sealed`, so `Model_types_must_be_sealed` stays green).

- [ ] **Step 6: Commit**

```bash
git add src/AiObservatory.Data/Entities/ClaudeActivitySession.cs src/AiObservatory.Data/AiObservatoryDbContext.cs src/AiObservatory.Data/Migrations/
git commit -m "feat: add ClaudeActivitySession entity and migration"
```

---

### Task 2: `AdminOnlyApiKeyEndpointFilter`

**Files:**
- Create: `src/AiObservatory.Api/AdminOnlyApiKeyEndpointFilter.cs`
- Test: `tests/AiObservatory.Api.Tests/AdminOnlyApiKeyEndpointFilterTests.cs`

**Interfaces:**
- Produces: `AiObservatory.Api.AdminOnlyApiKeyEndpointFilter` implementing `IEndpointFilter`, constructor `(IConfiguration config, IHostEnvironment env)`. Consumed by Task 3 via `.AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>()` on the two GET routes.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/AiObservatory.Api.Tests/AdminOnlyApiKeyEndpointFilterTests.cs
using System.Security.Claims;
using AiObservatory.Api;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace AiObservatory.Api.Tests;

public class AdminOnlyApiKeyEndpointFilterTests
{
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly IHostEnvironment _env = Substitute.For<IHostEnvironment>();
    private readonly AdminOnlyApiKeyEndpointFilter _sut;

    public AdminOnlyApiKeyEndpointFilterTests()
    {
        _env.EnvironmentName.Returns(Environments.Production);
        _sut = new AdminOnlyApiKeyEndpointFilter(_config, _env);
    }

    private static (EndpointFilterInvocationContext context, Func<bool> wasNextCalled) BuildContext(
        string method, string? key = null, bool authenticated = false)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticated)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "chris")], authenticationType: "Bearer"));
        }
        httpContext.Request.Method = method;
        if (key is not null)
        {
            httpContext.Request.Headers["X-Observatory-Key"] = key;
        }
        return (EndpointFilterInvocationContext.Create(httpContext), () => false);
    }

    [Fact]
    public async Task InvokeAsync_WhenAdminKeyConfigured_AllowsValidAdminKey()
    {
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var (context, _) = BuildContext("GET", "admin-key-12345");

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _) { nextCalled = true; return ValueTask.FromResult<object?>(Results.Ok()); }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Fact]
    public async Task InvokeAsync_WhenAdminKeyConfigured_RejectsReadonlyKey()
    {
        // The whole point of this filter: a valid readonly key must NOT pass here,
        // even though the readonly key is accepted by the shared ApiKeyEndpointFilter
        // on every other GET route.
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var (context, _) = BuildContext("GET", "readonly-key-12345");

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _) { nextCalled = true; return ValueTask.FromResult<object?>(Results.Ok()); }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task InvokeAsync_WhenNoKeyHeader_RejectsAnonymousRequest()
    {
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var (context, _) = BuildContext("GET");

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _) { nextCalled = true; return ValueTask.FromResult<object?>(Results.Ok()); }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().BeFalse();
        result.Should().BeOfType<UnauthorizedHttpResult>();
    }

    [Fact]
    public async Task InvokeAsync_WhenEntraAuthenticated_AllowsRegardlessOfKey()
    {
        _config["OBSERVATORY_API_KEY"].Returns("admin-key-12345");
        var (context, _) = BuildContext("GET", authenticated: true);

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _) { nextCalled = true; return ValueTask.FromResult<object?>(Results.Ok()); }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().BeTrue();
        result.Should().BeOfType<Ok>();
    }

    [Theory]
    [InlineData("Development", true, 200)]
    [InlineData("Production", false, 503)]
    public async Task InvokeAsync_WhenAdminKeyNotConfigured_FailsClosedOutsideDev(
        string environment, bool expectNext, int expectedStatus)
    {
        _env.EnvironmentName.Returns(environment);
        _config["OBSERVATORY_API_KEY"].Returns((string?)null);
        var (context, _) = BuildContext("GET");

        var nextCalled = false;
        ValueTask<object?> Next(EndpointFilterInvocationContext _) { nextCalled = true; return ValueTask.FromResult<object?>(Results.Ok()); }

        var result = await _sut.InvokeAsync(context, Next);

        nextCalled.Should().Be(expectNext);
        if (expectNext)
        {
            result.Should().BeOfType<Ok>();
        }
        else
        {
            var statusResult = result.Should().BeOfType<StatusCodeHttpResult>().Subject;
            statusResult.StatusCode.Should().Be(expectedStatus);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~AdminOnlyApiKeyEndpointFilterTests"`
Expected: build error — `AdminOnlyApiKeyEndpointFilter` does not exist yet.

- [ ] **Step 3: Implement the filter**

```csharp
// src/AiObservatory.Api/AdminOnlyApiKeyEndpointFilter.cs
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api;

// Stricter sibling of ApiKeyEndpointFilter for GET routes whose data is more
// sensitive than the rest of the read surface (project/repo names reveal what
// Chris is working on, unlike aggregate spend). The readonly viewer key is
// never accepted here — only an Entra-authenticated user or the admin key.
public class AdminOnlyApiKeyEndpointFilter(IConfiguration config, IHostEnvironment env) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            return await next(context);
        }

        var expectedAdmin = config["OBSERVATORY_API_KEY"];
        if (string.IsNullOrEmpty(expectedAdmin))
        {
            // Fail closed outside dev, mirroring ApiKeyEndpointFilter's behavior for
            // a missing key — a misconfigured deploy must not silently allow access.
            return env.IsDevelopment() ? await next(context) : Results.StatusCode(503);
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Observatory-Key", out var provided)
            || !FixedTimeEquals(provided.ToString(), expectedAdmin))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~AdminOnlyApiKeyEndpointFilterTests"`
Expected: `Passed!  - Failed: 0, Passed: 6`

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Api/AdminOnlyApiKeyEndpointFilter.cs tests/AiObservatory.Api.Tests/AdminOnlyApiKeyEndpointFilterTests.cs
git commit -m "feat: add admin-only API key filter for sensitive GET routes"
```

---

### Task 3: Activity endpoints (`POST /activity/sessions`, `GET /activity/daily`, `GET /activity/by-project`)

**Files:**
- Create: `src/AiObservatory.Api/Endpoints/ActivityEndpoints.cs`
- Modify: `src/AiObservatory.Api/Program.cs`
- Test: `tests/AiObservatory.Api.Tests/ActivityEndpointsTests.cs`

**Interfaces:**
- Consumes: `AiObservatory.Data.Entities.ClaudeActivitySession` (Task 1), `AiObservatory.Api.AdminOnlyApiKeyEndpointFilter` (Task 2).
- Produces (wire contract, consumed by Task 4's frontend client):
  - `POST /api/activity/sessions` body `{ sessions: [{ sessionId, project, startedAtUtc, lastSeenAtUtc, activeSeconds }] }` → `{ upserted: number }`.
  - `GET /api/activity/daily?from=&to=` → `[{ date: "yyyy-MM-dd", activeSeconds: number }]`.
  - `GET /api/activity/by-project?from=&to=` → `[{ project: string, sessionCount: number, activeSeconds: number, sharePercent: number }]`.
- Produces (for tests in this task): `ActivityEndpoints.ShouldReplaceExisting(ClaudeActivitySession existing, long newActiveSeconds, Instant newLastSeenAt) : bool`.

- [ ] **Step 1: Write the failing test for the upsert decision**

```csharp
// tests/AiObservatory.Api.Tests/ActivityEndpointsTests.cs
using AiObservatory.Api.Endpoints;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;
using Xunit;

namespace AiObservatory.Api.Tests;

public class ActivityEndpointsTests
{
    private static ClaudeActivitySession ExistingSession(long activeSeconds, Instant lastSeenAt) =>
        new()
        {
            SessionId = "s1",
            Project = "fixportal-ai-observatory",
            StartedAt = Instant.FromUtc(2026, 7, 1, 9, 0),
            LastSeenAt = lastSeenAt,
            ActiveSeconds = activeSeconds,
        };

    [Fact]
    public void ShouldReplaceExisting_WhenNewActiveSecondsGreater_ReturnsTrue()
    {
        var existing = ExistingSession(100, Instant.FromUtc(2026, 7, 1, 9, 5));
        var result = ActivityEndpoints.ShouldReplaceExisting(existing, 150, Instant.FromUtc(2026, 7, 1, 9, 5));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldReplaceExisting_WhenNewLastSeenAtLater_ReturnsTrue()
    {
        var existing = ExistingSession(100, Instant.FromUtc(2026, 7, 1, 9, 5));
        var result = ActivityEndpoints.ShouldReplaceExisting(existing, 100, Instant.FromUtc(2026, 7, 1, 9, 6));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldReplaceExisting_WhenBothOlderOrEqual_ReturnsFalse()
    {
        // Out-of-order delivery: a stale sweep result arrives after a newer one
        // already recorded more time. Must not regress the stored total.
        var existing = ExistingSession(150, Instant.FromUtc(2026, 7, 1, 9, 6));
        var result = ActivityEndpoints.ShouldReplaceExisting(existing, 100, Instant.FromUtc(2026, 7, 1, 9, 5));
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldReplaceExisting_WhenIdentical_ReturnsFalse()
    {
        var lastSeenAt = Instant.FromUtc(2026, 7, 1, 9, 5);
        var existing = ExistingSession(100, lastSeenAt);
        var result = ActivityEndpoints.ShouldReplaceExisting(existing, 100, lastSeenAt);
        result.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~ActivityEndpointsTests"`
Expected: build error — `AiObservatory.Api.Endpoints.ActivityEndpoints` does not exist yet.

- [ ] **Step 3: Implement the endpoints**

```csharp
// src/AiObservatory.Api/Endpoints/ActivityEndpoints.cs
using System.Globalization;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Api.Endpoints;

public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/activity/sessions", async (
            ActivitySessionsRequest req,
            AiObservatoryDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            if (req.Sessions is not { Count: > 0 })
            {
                return Results.Ok(new { Upserted = 0 });
            }

            if (req.Sessions.Count > 1000)
            {
                return Results.BadRequest("Cannot upsert more than 1000 sessions at once.");
            }

            foreach (var s in req.Sessions)
            {
                if (string.IsNullOrWhiteSpace(s.SessionId) || s.SessionId.Length > 200)
                {
                    return Results.BadRequest($"SessionId invalid: '{s.SessionId}'");
                }
                if (string.IsNullOrWhiteSpace(s.Project) || s.Project.Length > 200)
                {
                    return Results.BadRequest($"Project invalid: '{s.Project}'");
                }
                if (s.ActiveSeconds < 0)
                {
                    return Results.BadRequest("ActiveSeconds must be non-negative.");
                }
                if (s.LastSeenAtUtc < s.StartedAtUtc)
                {
                    return Results.BadRequest($"LastSeenAtUtc must not be before StartedAtUtc: '{s.SessionId}'");
                }
            }

            var now = clock.GetCurrentInstant();
            var sessionIds = req.Sessions.Select(s => s.SessionId).ToList();
            var existing = await db.ClaudeActivitySessions
                .Where(s => sessionIds.Contains(s.SessionId))
                .ToDictionaryAsync(s => s.SessionId, ct);

            var upserted = 0;
            foreach (var s in req.Sessions)
            {
                var startedAt = Instant.FromDateTimeOffset(s.StartedAtUtc);
                var lastSeenAt = Instant.FromDateTimeOffset(s.LastSeenAtUtc);
                if (startedAt > now + Duration.FromMinutes(5) || lastSeenAt > now + Duration.FromMinutes(5))
                {
                    return Results.BadRequest($"Timestamps must not be in the future: '{s.SessionId}'");
                }

                if (existing.TryGetValue(s.SessionId, out var current))
                {
                    if (!ShouldReplaceExisting(current, s.ActiveSeconds, lastSeenAt))
                    {
                        continue;
                    }

                    await db.ClaudeActivitySessions
                        .Where(x => x.SessionId == s.SessionId)
                        .ExecuteUpdateAsync(upd => upd
                            .SetProperty(p => p.ActiveSeconds, s.ActiveSeconds)
                            .SetProperty(p => p.LastSeenAt, lastSeenAt), ct);
                }
                else
                {
                    db.ClaudeActivitySessions.Add(new ClaudeActivitySession
                    {
                        SessionId = s.SessionId,
                        Project = s.Project,
                        StartedAt = startedAt,
                        LastSeenAt = lastSeenAt,
                        ActiveSeconds = s.ActiveSeconds,
                        IngestedAt = now,
                    });
                }
                upserted++;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { Upserted = upserted });
        });

        app.MapGet("/activity/daily", async (
            AiObservatoryDbContext db,
            IClock clock,
            string? from, string? to,
            CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }

            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var sessions = await db.ClaudeActivitySessions
                .AsNoTracking()
                .Where(s => s.StartedAt >= startInstant && s.StartedAt < endInstant)
                .Select(s => new { s.StartedAt, s.ActiveSeconds })
                .ToListAsync(ct);

            // In-memory grouping: LocalDatePattern.Iso.Format(Instant) isn't SQL-
            // translatable, and this is personal-scale data (one user's local
            // sessions), so a client-side GroupBy is fine — revisit with a SQL
            // GROUP BY if row count ever grows past a single user's history.
            var byDate = sessions
                .GroupBy(s => LocalDatePattern.Iso.Format(s.StartedAt.InUtc().Date))
                .Select(g => new DailyActivityResponse(g.Key, g.Sum(s => s.ActiveSeconds)))
                .OrderBy(d => d.Date)
                .ToList();

            return Results.Ok(byDate);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet("/activity/by-project", async (
            AiObservatoryDbContext db,
            IClock clock,
            string? from, string? to,
            CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }

            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var sessions = await db.ClaudeActivitySessions
                .AsNoTracking()
                .Where(s => s.StartedAt >= startInstant && s.StartedAt < endInstant)
                .Select(s => new { s.SessionId, s.Project, s.ActiveSeconds })
                .ToListAsync(ct);

            var totalSeconds = sessions.Sum(s => s.ActiveSeconds);

            var byProject = sessions
                .GroupBy(s => s.Project)
                .Select(g => new ProjectActivityResponse(
                    g.Key,
                    g.Select(s => s.SessionId).Distinct().Count(),
                    g.Sum(s => s.ActiveSeconds),
                    totalSeconds > 0 ? Math.Round(g.Sum(s => s.ActiveSeconds) * 100.0 / totalSeconds, 1) : 0))
                .OrderByDescending(p => p.ActiveSeconds)
                .ToList();

            return Results.Ok(byProject);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
    }

    public static bool ShouldReplaceExisting(ClaudeActivitySession existing, long newActiveSeconds, Instant newLastSeenAt) =>
        newActiveSeconds > existing.ActiveSeconds || newLastSeenAt > existing.LastSeenAt;

    private static bool TryParseDateRange(
        string? from, string? to, LocalDate today,
        out LocalDate start, out LocalDate end, out IResult? error)
    {
        error = null;
        start = today.PlusDays(-30);
        end = today;

        if (from is not null)
        {
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
            {
                error = Results.BadRequest("from must be yyyy-MM-dd");
                return false;
            }
            start = LocalDate.FromDateOnly(fromDate);
        }

        if (to is not null)
        {
            if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
            {
                error = Results.BadRequest("to must be yyyy-MM-dd");
                return false;
            }
            end = LocalDate.FromDateOnly(toDate);
        }

        return true;
    }
}

public sealed record ActivitySessionRequest(
    string SessionId,
    string Project,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    long ActiveSeconds
);

public sealed record ActivitySessionsRequest(List<ActivitySessionRequest> Sessions);

public sealed record DailyActivityResponse(string Date, long ActiveSeconds);

public sealed record ProjectActivityResponse(string Project, int SessionCount, long ActiveSeconds, double SharePercent);
```

- [ ] **Step 4: Wire the endpoints into the app**

In `src/AiObservatory.Api/Program.cs`, add the mapping call next to the others:

```csharp
api.MapEventsEndpoints();
api.MapCavemanEndpoints();
api.MapActivityEndpoints();
api.MapAdversarialReviewEndpoints();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~ActivityEndpointsTests"`
Expected: `Passed!  - Failed: 0, Passed: 4`

- [ ] **Step 6: Build the whole Api project**

Run: `dotnet build src/AiObservatory.Api/AiObservatory.Api.csproj`
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/AiObservatory.Api/Endpoints/ActivityEndpoints.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.Tests/ActivityEndpointsTests.cs
git commit -m "feat: add Claude activity ingestion and query endpoints"
```

---

### Task 4: Frontend API client types and React Query hooks

**Files:**
- Modify: `src/AiObservatory.Web/src/api/client.ts`
- Modify: `src/AiObservatory.Web/src/api/queries.ts`

**Interfaces:**
- Consumes: the wire contract from Task 3 (`GET /api/activity/daily`, `GET /api/activity/by-project`).
- Produces: `DailyActivity { date: string; activeSeconds: number }`, `ProjectActivity { project: string; sessionCount: number; activeSeconds: number; sharePercent: number }`, `getActivityDaily(from?, to?)`, `getActivityByProject(from?, to?)` from `client.ts`; `useActivityDaily(from?: Date, to?: Date): DailyActivity[]` and `useActivityByProject(from?: Date, to?: Date): ProjectActivity[]` from `queries.ts`. Consumed by Tasks 8, 9, 10, 11.

This task has no new branching logic — it's typed plumbing matching the existing untested `getAggregates`/`useAggregates` pattern exactly, so verification is the TypeScript compiler, not a new test.

- [ ] **Step 1: Add the types and fetch functions to `client.ts`**

In `src/AiObservatory.Web/src/api/client.ts`, add after the `getSubscriptions` export:

```ts
export interface DailyActivity {
  date: string
  activeSeconds: number
}

export interface ProjectActivity {
  project: string
  sessionCount: number
  activeSeconds: number
  sharePercent: number
}

export const getActivityDaily = (from?: string, to?: string) =>
  getJson<DailyActivity[]>('/activity/daily', { from, to })

export const getActivityByProject = (from?: string, to?: string) =>
  getJson<ProjectActivity[]>('/activity/by-project', { from, to })
```

- [ ] **Step 2: Add the hooks to `queries.ts`**

In `src/AiObservatory.Web/src/api/queries.ts`, update the import to pull in the new client functions and types:

```ts
import {
  getAggregates, getInsights, getSubscriptions,
  getAdversarialReviewRuns, getAdversarialReviewStats, getCavemanStats,
  getBudgetRules, getEmailStatus,
  getActivityDaily, getActivityByProject,
  type DailyAggregate, type Insight, type Subscription,
  type AdversarialReviewRun, type AdversarialReviewStats, type CavemanStats,
  type BudgetRule, type DailyActivity, type ProjectActivity,
} from './client'
```

Add the hooks after `useAggregates`:

```ts
export function useActivityDaily(from?: Date, to?: Date): DailyActivity[] {
  const hasRange = from != null && to != null
  const { data = [] } = useQuery({
    queryKey: hasRange ? ['activity-daily', localDate(from!), localDate(to!)] : ['activity-daily'],
    queryFn: hasRange
      ? () => getActivityDaily(localDate(from!), localDate(to!))
      : () => getActivityDaily(),
  })
  return data
}

export function useActivityByProject(from?: Date, to?: Date): ProjectActivity[] {
  const hasRange = from != null && to != null
  const { data = [] } = useQuery({
    queryKey: hasRange ? ['activity-by-project', localDate(from!), localDate(to!)] : ['activity-by-project'],
    queryFn: hasRange
      ? () => getActivityByProject(localDate(from!), localDate(to!))
      : () => getActivityByProject(),
  })
  return data
}
```

- [ ] **Step 3: Typecheck**

Run (from `src/AiObservatory.Web`): `npx tsc -b --noEmit`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add src/AiObservatory.Web/src/api/client.ts src/AiObservatory.Web/src/api/queries.ts
git commit -m "feat: add activity API client functions and query hooks"
```

---

### Task 5: `formatActiveTime` duration helper

**Files:**
- Create: `src/AiObservatory.Web/src/lib/duration.ts`
- Test: `src/AiObservatory.Web/src/lib/duration.test.ts`

**Interfaces:**
- Produces: `formatActiveTime(seconds: number): string`. Consumed by Tasks 8, 9, 10.

- [ ] **Step 1: Write the failing test**

```ts
// src/AiObservatory.Web/src/lib/duration.test.ts
import { test, expect } from 'vitest'
import { formatActiveTime } from './duration'

test('formats under an hour as minutes only', () => {
  expect(formatActiveTime(45 * 60)).toBe('45m')
})

test('formats over an hour as hours and minutes', () => {
  expect(formatActiveTime(6 * 3600 + 40 * 60)).toBe('6h 40m')
})

test('rounds to the nearest minute', () => {
  expect(formatActiveTime(89)).toBe('1m') // 89s rounds to 1m, not 0m
})

test('formats zero seconds as 0m', () => {
  expect(formatActiveTime(0)).toBe('0m')
})

test('formats an exact hour with no leftover minutes', () => {
  expect(formatActiveTime(3600)).toBe('1h 0m')
})
```

- [ ] **Step 2: Run test to verify it fails**

Run (from `src/AiObservatory.Web`): `npx vitest run src/lib/duration.test.ts`
Expected: FAIL — `Cannot find module './duration'`

- [ ] **Step 3: Implement the helper**

```ts
// src/AiObservatory.Web/src/lib/duration.ts
export function formatActiveTime(seconds: number): string {
  const totalMinutes = Math.round(seconds / 60)
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return hours === 0 ? `${minutes}m` : `${hours}h ${minutes}m`
}
```

- [ ] **Step 4: Run test to verify it passes**

Run (from `src/AiObservatory.Web`): `npx vitest run src/lib/duration.test.ts`
Expected: `5 passed`

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Web/src/lib/duration.ts src/AiObservatory.Web/src/lib/duration.test.ts
git commit -m "feat: add active-time duration formatting helper"
```

---

### Task 6: `projectBreakdownSort` pure logic

**Files:**
- Create: `src/AiObservatory.Web/src/components/projectBreakdownSort.ts`
- Test: `src/AiObservatory.Web/src/components/projectBreakdownSort.test.ts`

**Interfaces:**
- Consumes: `ProjectActivity` from `../api/client` (Task 4).
- Produces: `ProjectSortField = 'project' | 'sessions' | 'activeSeconds'`, `SortDirection = 'asc' | 'desc'`, `filterProjects(projects: ProjectActivity[], query: string): ProjectActivity[]`, `sortProjects(projects: ProjectActivity[], field: ProjectSortField, direction: SortDirection): ProjectActivity[]`. Consumed by Task 9.

- [ ] **Step 1: Write the failing tests**

```ts
// src/AiObservatory.Web/src/components/projectBreakdownSort.test.ts
import { test, expect } from 'vitest'
import { filterProjects, sortProjects } from './projectBreakdownSort'
import type { ProjectActivity } from '../api/client'

const projects: ProjectActivity[] = [
  { project: 'fixportal-ai-observatory', sessionCount: 14, activeSeconds: 24000, sharePercent: 52 },
  { project: 'fixportal-quickfixn', sessionCount: 7, activeSeconds: 11400, sharePercent: 25 },
  { project: 'Training', sessionCount: 2, activeSeconds: 2700, sharePercent: 7 },
]

test('filterProjects matches case-insensitively', () => {
  expect(filterProjects(projects, 'FIXPORTAL').map(p => p.project)).toEqual([
    'fixportal-ai-observatory', 'fixportal-quickfixn',
  ])
})

test('filterProjects with blank query returns all projects unchanged', () => {
  expect(filterProjects(projects, '  ')).toEqual(projects)
})

test('filterProjects with no matches returns empty array', () => {
  expect(filterProjects(projects, 'nope')).toEqual([])
})

test('sortProjects by project name ascending', () => {
  const sorted = sortProjects(projects, 'project', 'asc')
  expect(sorted.map(p => p.project)).toEqual(['fixportal-ai-observatory', 'fixportal-quickfixn', 'Training'])
})

test('sortProjects by activeSeconds descending', () => {
  const sorted = sortProjects(projects, 'activeSeconds', 'desc')
  expect(sorted.map(p => p.project)).toEqual(['fixportal-ai-observatory', 'fixportal-quickfixn', 'Training'])
})

test('sortProjects by sessions ascending', () => {
  const sorted = sortProjects(projects, 'sessions', 'asc')
  expect(sorted.map(p => p.project)).toEqual(['Training', 'fixportal-quickfixn', 'fixportal-ai-observatory'])
})

test('sortProjects does not mutate the input array', () => {
  const original = [...projects]
  sortProjects(projects, 'project', 'asc')
  expect(projects).toEqual(original)
})
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `src/AiObservatory.Web`): `npx vitest run src/components/projectBreakdownSort.test.ts`
Expected: FAIL — `Cannot find module './projectBreakdownSort'`

- [ ] **Step 3: Implement the helpers**

```ts
// src/AiObservatory.Web/src/components/projectBreakdownSort.ts
import type { ProjectActivity } from '../api/client'

export type ProjectSortField = 'project' | 'sessions' | 'activeSeconds'
export type SortDirection = 'asc' | 'desc'

export function filterProjects(projects: ProjectActivity[], query: string): ProjectActivity[] {
  const q = query.trim().toLowerCase()
  if (!q) return projects
  return projects.filter((p) => p.project.toLowerCase().includes(q))
}

export function sortProjects(
  projects: ProjectActivity[], field: ProjectSortField, direction: SortDirection,
): ProjectActivity[] {
  return projects.toSorted((a, b) => {
    let comparison: number
    if (field === 'project') comparison = a.project.localeCompare(b.project)
    else if (field === 'sessions') comparison = a.sessionCount - b.sessionCount
    else comparison = a.activeSeconds - b.activeSeconds
    return direction === 'asc' ? comparison : -comparison
  })
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run (from `src/AiObservatory.Web`): `npx vitest run src/components/projectBreakdownSort.test.ts`
Expected: `7 passed`

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Web/src/components/projectBreakdownSort.ts src/AiObservatory.Web/src/components/projectBreakdownSort.test.ts
git commit -m "feat: add project breakdown sort/filter helpers"
```

---

### Task 7: `buildTreemapBlocks` pure logic

**Files:**
- Create: `src/AiObservatory.Web/src/components/treemapBlocks.ts`
- Test: `src/AiObservatory.Web/src/components/treemapBlocks.test.ts`

**Interfaces:**
- Consumes: `ProjectActivity` from `../api/client` (Task 4).
- Produces: `TreemapBlock { project: string; activeSeconds: number; percent: number }`, `buildTreemapBlocks(projects: ProjectActivity[], maxBlocks?: number): TreemapBlock[]`. Consumed by Task 10.

- [ ] **Step 1: Write the failing tests**

```ts
// src/AiObservatory.Web/src/components/treemapBlocks.test.ts
import { test, expect } from 'vitest'
import { buildTreemapBlocks } from './treemapBlocks'
import type { ProjectActivity } from '../api/client'

const make = (project: string, activeSeconds: number): ProjectActivity =>
  ({ project, activeSeconds, sessionCount: 1, sharePercent: 0 })

test('returns empty array when there is no activity', () => {
  expect(buildTreemapBlocks([])).toEqual([])
})

test('returns all projects sorted descending when under the block cap', () => {
  const projects = [make('a', 100), make('b', 300), make('c', 200)]
  const blocks = buildTreemapBlocks(projects, 8)
  expect(blocks.map((b) => b.project)).toEqual(['b', 'c', 'a'])
})

test('computes percent of total active time', () => {
  const projects = [make('a', 25), make('b', 75)]
  const blocks = buildTreemapBlocks(projects, 8)
  expect(blocks.find((b) => b.project === 'a')!.percent).toBe(25)
  expect(blocks.find((b) => b.project === 'b')!.percent).toBe(75)
})

test('collapses projects beyond the cap into an Other bucket', () => {
  const projects = [make('a', 500), make('b', 400), make('c', 50), make('d', 30), make('e', 20)]
  const blocks = buildTreemapBlocks(projects, 2)
  expect(blocks.map((b) => b.project)).toEqual(['a', 'b', 'Other'])
  expect(blocks.find((b) => b.project === 'Other')!.activeSeconds).toBe(100) // 50+30+20
})

test('does not add an Other bucket when project count is within the cap', () => {
  const projects = [make('a', 100), make('b', 200)]
  const blocks = buildTreemapBlocks(projects, 8)
  expect(blocks.some((b) => b.project === 'Other')).toBe(false)
})
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `src/AiObservatory.Web`): `npx vitest run src/components/treemapBlocks.test.ts`
Expected: FAIL — `Cannot find module './treemapBlocks'`

- [ ] **Step 3: Implement the helper**

```ts
// src/AiObservatory.Web/src/components/treemapBlocks.ts
import type { ProjectActivity } from '../api/client'

export interface TreemapBlock {
  project: string
  activeSeconds: number
  percent: number
}

export function buildTreemapBlocks(projects: ProjectActivity[], maxBlocks = 8): TreemapBlock[] {
  const sorted = projects.toSorted((a, b) => b.activeSeconds - a.activeSeconds)
  const total = sorted.reduce((sum, p) => sum + p.activeSeconds, 0)
  if (total === 0) return []

  const top = sorted.slice(0, maxBlocks)
  const rest = sorted.slice(maxBlocks)

  const blocks: TreemapBlock[] = top.map((p) => ({
    project: p.project,
    activeSeconds: p.activeSeconds,
    percent: Math.round((p.activeSeconds / total) * 1000) / 10,
  }))

  if (rest.length > 0) {
    const restSeconds = rest.reduce((sum, p) => sum + p.activeSeconds, 0)
    blocks.push({
      project: 'Other',
      activeSeconds: restSeconds,
      percent: Math.round((restSeconds / total) * 1000) / 10,
    })
  }

  return blocks
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run (from `src/AiObservatory.Web`): `npx vitest run src/components/treemapBlocks.test.ts`
Expected: `5 passed`

- [ ] **Step 5: Commit**

```bash
git add src/AiObservatory.Web/src/components/treemapBlocks.ts src/AiObservatory.Web/src/components/treemapBlocks.test.ts
git commit -m "feat: add treemap block sizing helper"
```

---

### Task 8: `ActivityTrendChart` component

**Files:**
- Create: `src/AiObservatory.Web/src/components/ActivityTrendChart.tsx`

**Interfaces:**
- Consumes: `useActivityDaily` (Task 4), `formatActiveTime` (Task 5), `formatShortDate` from `../lib/format` (existing).
- Produces: `export default function ActivityTrendChart({ from, to }: { from?: Date; to?: Date })`. Consumed by Task 11.

No new logic beyond what Tasks 5–7 already test — this mirrors `SpendChart.tsx`'s structure with a single series, so it gets a typecheck/lint pass rather than a new unit test (same as `SpendChart`/`ProviderSplit`, neither of which has one).

- [ ] **Step 1: Implement the component**

```tsx
// src/AiObservatory.Web/src/components/ActivityTrendChart.tsx
import { lazy, Suspense, useMemo } from 'react'
import { useActivityDaily } from '../api/queries'
import { formatActiveTime } from '../lib/duration'
import { formatShortDate } from '../lib/format'

const TEXT_MUTED = 'var(--text-muted)'

const ChartInner = lazy(() =>
  import('recharts').then(({ BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer }) => ({
    default: function Inner({ byDate }: { byDate: { date: string; minutes: number }[] }) {
      return (
        <ResponsiveContainer width="100%" height={160}>
          <BarChart data={byDate}>
            <XAxis dataKey="date" tickFormatter={formatShortDate} tick={{ fontSize: 10, fill: TEXT_MUTED }} />
            <YAxis tick={{ fontSize: 10, fill: TEXT_MUTED }} tickFormatter={(v: number) => `${v}m`} />
            <Tooltip
              contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
              labelStyle={{ color: 'var(--text)' }}
              itemStyle={{ color: TEXT_MUTED }}
              labelFormatter={(label) => formatShortDate(String(label ?? ''))}
              formatter={(v: number) => formatActiveTime(v * 60)}
            />
            <Bar dataKey="minutes" fill="var(--brand)" />
          </BarChart>
        </ResponsiveContainer>
      )
    },
  }))
)

interface Props {
  from?: Date
  to?: Date
}

export default function ActivityTrendChart({ from, to }: Props) {
  const daily = useActivityDaily(from, to)

  const byDate = useMemo(
    () => daily
      .toSorted((a, b) => a.date.localeCompare(b.date))
      .map((d) => ({ date: d.date, minutes: Math.round(d.activeSeconds / 60) })),
    [daily],
  )

  if (byDate.length === 0) return <p className="panel-empty">No activity data for this period.</p>

  return (
    <Suspense fallback={<div style={{ height: 160 }} className="panel-empty">Loading chart...</div>}>
      <ChartInner byDate={byDate} />
    </Suspense>
  )
}
```

- [ ] **Step 2: Typecheck and lint**

Run (from `src/AiObservatory.Web`): `npx tsc -b --noEmit && npx eslint src/components/ActivityTrendChart.tsx`
Expected: no errors.

- [ ] **Step 3: Commit**

```bash
git add src/AiObservatory.Web/src/components/ActivityTrendChart.tsx
git commit -m "feat: add activity trend chart component"
```

---

### Task 9: `ProjectBreakdown` component (table + inline bar)

**Files:**
- Create: `src/AiObservatory.Web/src/components/ProjectBreakdown.tsx`
- Modify: `src/AiObservatory.Web/src/index.css`

**Interfaces:**
- Consumes: `ProjectActivity` from `../api/client` (Task 4), `filterProjects`/`sortProjects`/`ProjectSortField`/`SortDirection` from `./projectBreakdownSort` (Task 6), `formatActiveTime` (Task 5).
- Produces: `export default function ProjectBreakdown({ projects, selectedProject, onSelectProject }: { projects: ProjectActivity[]; selectedProject: string | null; onSelectProject: (project: string | null) => void })`. Consumed by Task 11.

- [ ] **Step 1: Implement the component**

```tsx
// src/AiObservatory.Web/src/components/ProjectBreakdown.tsx
import { useState, useMemo } from 'react'
import type { KeyboardEvent } from 'react'
import type { ProjectActivity } from '../api/client'
import { filterProjects, sortProjects } from './projectBreakdownSort'
import type { ProjectSortField, SortDirection } from './projectBreakdownSort'
import { formatActiveTime } from '../lib/duration'

interface SortableHeaderProps {
  field: ProjectSortField
  label: string
  sortField: ProjectSortField
  sortDirection: SortDirection
  onSort: (field: ProjectSortField) => void
}

const SortableHeader = ({ field, label, sortField, sortDirection, onSort }: SortableHeaderProps) => {
  const isActive = sortField === field
  const handleKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      onSort(field)
    }
  }
  let ariaSort: 'ascending' | 'descending' | 'none' = 'none'
  if (isActive) ariaSort = sortDirection === 'asc' ? 'ascending' : 'descending'
  let indicatorSymbol = '↕'
  if (isActive) indicatorSymbol = sortDirection === 'asc' ? '▲' : '▼'

  return (
    <th
      onClick={() => onSort(field)}
      onKeyDown={handleKeyDown}
      className="sortable-header"
      style={{ cursor: 'pointer' }}
      tabIndex={0}
      aria-sort={ariaSort}
    >
      <span className="sortable-header__content">
        {label}
        <span className={`sort-indicator ${isActive ? 'sort-indicator--active' : ''}`} aria-hidden="true">
          {indicatorSymbol}
        </span>
      </span>
    </th>
  )
}

interface Props {
  projects: ProjectActivity[]
  selectedProject: string | null
  onSelectProject: (project: string | null) => void
}

export default function ProjectBreakdown({ projects, selectedProject, onSelectProject }: Props) {
  const [searchQuery, setSearchQuery] = useState('')
  const [sortField, setSortField] = useState<ProjectSortField>('activeSeconds')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const visible = useMemo(() => {
    const base = selectedProject ? projects.filter((p) => p.project === selectedProject) : projects
    return sortProjects(filterProjects(base, searchQuery), sortField, sortDirection)
  }, [projects, selectedProject, searchQuery, sortField, sortDirection])

  const maxActiveSeconds = useMemo(
    () => projects.reduce((m, p) => (p.activeSeconds > m ? p.activeSeconds : m), 1),
    [projects],
  )

  if (projects.length === 0) return <p className="panel-empty">No activity data for this period.</p>

  const handleSort = (field: ProjectSortField) => {
    if (sortField === field) {
      setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortField(field)
      setSortDirection('desc')
    }
  }

  return (
    <>
      <div className="project-breakdown-controls">
        <input
          type="text"
          placeholder="Search projects..."
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          className="project-breakdown-search"
          aria-label="Search projects"
        />
        {selectedProject && (
          <button type="button" className="filter-chip" onClick={() => onSelectProject(null)}>
            Filtered: {selectedProject} ✕
          </button>
        )}
      </div>

      {visible.length === 0 ? (
        <p className="panel-empty">No matching projects found.</p>
      ) : (
        <table className="project-table">
          <thead>
            <tr>
              <SortableHeader field="project" label="Project" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="sessions" label="Sessions" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="activeSeconds" label="Active time" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <th>Share</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((p) => (
              <tr
                key={p.project}
                onClick={() => onSelectProject(p.project === selectedProject ? null : p.project)}
                style={{ cursor: 'pointer' }}
              >
                <td>{p.project}</td>
                <td>{p.sessionCount.toLocaleString()}</td>
                <td>{formatActiveTime(p.activeSeconds)}</td>
                <td>
                  <div className="project-table__share">
                    <div className="project-table__bar-track">
                      <div className="project-table__bar" style={{ width: `${(p.activeSeconds / maxActiveSeconds) * 100}%` }} />
                    </div>
                    <span>{p.sharePercent.toFixed(0)}%</span>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
```

- [ ] **Step 2: Add the CSS**

Append to `src/AiObservatory.Web/src/index.css`:

```css
/* ---- Activity tab: project breakdown ---- */
.project-breakdown-controls {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-3);
  margin-bottom: var(--space-4);
  align-items: center;
  justify-content: space-between;
}
.project-breakdown-search {
  appearance: none;
  font: inherit;
  font-size: 0.8rem;
  flex: 1 1 200px;
  padding: var(--space-2) var(--space-3);
  background: var(--app-bg);
  border: 1px solid var(--border);
  border-radius: var(--r-control);
  color: var(--text);
  transition: border-color var(--transition-fast);
}
.project-breakdown-search:focus {
  outline: 2px solid var(--brand);
  outline-offset: 1px;
}
.project-table { width: 100%; border-collapse: collapse; font-size: 0.8rem; }
.project-table th {
  text-align: left;
  font-weight: 500;
  font-size: 0.65rem;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--text-muted);
  padding: var(--space-1) var(--space-2);
}
.project-table td {
  padding: var(--space-2);
  border-top: 1px solid var(--border);
  color: var(--text);
  font-family: var(--font-mono);
}
.project-table tbody tr { transition: background-color var(--transition-fast); }
.project-table tbody tr:hover { background-color: var(--app-bg); }
.project-table__share { display: flex; align-items: center; gap: var(--space-2); }
.project-table__bar-track {
  flex: 1;
  height: 8px;
  border-radius: 4px;
  background: var(--border);
  overflow: hidden;
}
.project-table__bar { height: 100%; border-radius: 4px; background: var(--brand); }
```

- [ ] **Step 3: Typecheck and lint**

Run (from `src/AiObservatory.Web`): `npx tsc -b --noEmit && npx eslint src/components/ProjectBreakdown.tsx`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add src/AiObservatory.Web/src/components/ProjectBreakdown.tsx src/AiObservatory.Web/src/index.css
git commit -m "feat: add project breakdown table component"
```

---

### Task 10: `ProjectTreemap` component (click-to-filter)

**Files:**
- Create: `src/AiObservatory.Web/src/components/ProjectTreemap.tsx`
- Test: `src/AiObservatory.Web/src/components/ProjectTreemap.test.tsx`
- Modify: `src/AiObservatory.Web/src/index.css`

**Interfaces:**
- Consumes: `ProjectActivity` from `../api/client` (Task 4), `buildTreemapBlocks` from `./treemapBlocks` (Task 7), `formatActiveTime` (Task 5).
- Produces: `export default function ProjectTreemap({ projects, selectedProject, onSelectProject }: { projects: ProjectActivity[]; selectedProject: string | null; onSelectProject: (project: string | null) => void })`. Consumed by Task 11.

This component has real interactive behavior (click selects/deselects a project, the "Other" bucket is non-interactive) — unlike Tasks 8–9 it gets an RTL test, mirroring `CollapsiblePanel.test.tsx`.

- [ ] **Step 1: Write the failing test**

```tsx
// src/AiObservatory.Web/src/components/ProjectTreemap.test.tsx
import { test, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import ProjectTreemap from './ProjectTreemap'
import type { ProjectActivity } from '../api/client'

const projects: ProjectActivity[] = [
  { project: 'fixportal-ai-observatory', sessionCount: 14, activeSeconds: 24000, sharePercent: 67 },
  { project: 'Training', sessionCount: 2, activeSeconds: 2700, sharePercent: 8 },
]

test('renders a block per project', () => {
  render(<ProjectTreemap projects={projects} selectedProject={null} onSelectProject={vi.fn()} />)
  expect(screen.getByText('fixportal-ai-observatory')).toBeInTheDocument()
  expect(screen.getByText('Training')).toBeInTheDocument()
})

test('clicking a block selects that project', () => {
  const onSelectProject = vi.fn()
  render(<ProjectTreemap projects={projects} selectedProject={null} onSelectProject={onSelectProject} />)
  fireEvent.click(screen.getByText('Training'))
  expect(onSelectProject).toHaveBeenCalledWith('Training')
})

test('clicking the already-selected block deselects it', () => {
  const onSelectProject = vi.fn()
  render(<ProjectTreemap projects={projects} selectedProject="Training" onSelectProject={onSelectProject} />)
  fireEvent.click(screen.getByText('Training'))
  expect(onSelectProject).toHaveBeenCalledWith(null)
})

test('shows empty state when there is no activity', () => {
  render(<ProjectTreemap projects={[]} selectedProject={null} onSelectProject={vi.fn()} />)
  expect(screen.getByText('No activity data for this period.')).toBeInTheDocument()
})
```

- [ ] **Step 2: Run test to verify it fails**

Run (from `src/AiObservatory.Web`): `npx vitest run src/components/ProjectTreemap.test.tsx`
Expected: FAIL — `Cannot find module './ProjectTreemap'`

- [ ] **Step 3: Implement the component**

```tsx
// src/AiObservatory.Web/src/components/ProjectTreemap.tsx
import { useMemo } from 'react'
import type { ProjectActivity } from '../api/client'
import { buildTreemapBlocks } from './treemapBlocks'
import { formatActiveTime } from '../lib/duration'

// Fixed palette cycled by index — projects are arbitrary strings, unlike
// providerColor's known provider set, so there's no semantic color to key off.
const PALETTE = ['var(--brand)', '#5a9c7c', '#3f6f8f', '#7a5fa0', '#b8895a', '#4f8a8b', '#9c5a7c', '#6b8e4e']

interface Props {
  projects: ProjectActivity[]
  selectedProject: string | null
  onSelectProject: (project: string | null) => void
}

export default function ProjectTreemap({ projects, selectedProject, onSelectProject }: Props) {
  const blocks = useMemo(() => buildTreemapBlocks(projects), [projects])

  if (blocks.length === 0) return <p className="panel-empty">No activity data for this period.</p>

  return (
    <div className="activity-treemap">
      {blocks.map((b, i) => (
        <button
          key={b.project}
          type="button"
          className={`activity-treemap__block${b.project === selectedProject ? ' activity-treemap__block--selected' : ''}`}
          style={{ flexGrow: b.activeSeconds, background: PALETTE[i % PALETTE.length] }}
          // The "Other" bucket aggregates everything past the top N — there's no
          // single project to filter the table to, so it's not interactive.
          disabled={b.project === 'Other'}
          onClick={() => onSelectProject(b.project === selectedProject ? null : b.project)}
          title={`${b.project} — ${formatActiveTime(b.activeSeconds)} (${b.percent}%)`}
        >
          <span className="activity-treemap__label">{b.project}</span>
          <span className="activity-treemap__value">{formatActiveTime(b.activeSeconds)}</span>
        </button>
      ))}
    </div>
  )
}
```

- [ ] **Step 4: Add the CSS**

Append to `src/AiObservatory.Web/src/index.css`:

```css
/* ---- Activity tab: treemap ---- */
.activity-treemap {
  display: flex;
  gap: 2px;
  height: 160px;
  border-radius: var(--r-chip);
  overflow: hidden;
}
.activity-treemap__block {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--space-1);
  min-width: 48px;
  border: none;
  color: var(--text-on-brand);
  cursor: pointer;
  text-align: center;
  padding: var(--space-2);
  transition: filter var(--transition-fast);
}
.activity-treemap__block:hover:not(:disabled) { filter: brightness(1.1); }
.activity-treemap__block:disabled { cursor: default; }
.activity-treemap__block--selected { outline: 2px solid var(--text); outline-offset: -2px; }
.activity-treemap__label {
  font-size: 0.7rem;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 100%;
}
.activity-treemap__value { font-size: 0.65rem; opacity: 0.85; }
```

- [ ] **Step 5: Run test to verify it passes**

Run (from `src/AiObservatory.Web`): `npx vitest run src/components/ProjectTreemap.test.tsx`
Expected: `4 passed`

- [ ] **Step 6: Commit**

```bash
git add src/AiObservatory.Web/src/components/ProjectTreemap.tsx src/AiObservatory.Web/src/components/ProjectTreemap.test.tsx src/AiObservatory.Web/src/index.css
git commit -m "feat: add project activity treemap component"
```

---

### Task 11: `ActivityPage` assembly

**Files:**
- Create: `src/AiObservatory.Web/src/pages/ActivityPage.tsx`

**Interfaces:**
- Consumes: `ActivityTrendChart` (Task 8), `ProjectBreakdown` (Task 9), `ProjectTreemap` (Task 10), `useActivityByProject`/`localDate` (Task 4 / existing), `useDateRange` (existing `lib/dateRange`), `DateRangePicker` (existing).
- Produces: `export default function ActivityPage()`. Consumed by Task 12.

- [ ] **Step 1: Implement the page**

```tsx
// src/AiObservatory.Web/src/pages/ActivityPage.tsx
import { useState, lazy, Suspense } from 'react'
import DateRangePicker from '../components/DateRangePicker'
import ProjectBreakdown from '../components/ProjectBreakdown'
import { useDateRange } from '../lib/dateRange'
import { useActivityByProject, localDate } from '../api/queries'

const ActivityTrendChart = lazy(() => import('../components/ActivityTrendChart'))
const ProjectTreemap = lazy(() => import('../components/ProjectTreemap'))

export default function ActivityPage() {
  const { from, to, preset, setPreset, setCustom } = useDateRange()
  const byProject = useActivityByProject(from, to)
  const [selectedProject, setSelectedProject] = useState<string | null>(null)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`

  return (
    // Reuses the Reporting page's layout class — it's generic (flex column +
    // spacing), not Reporting-specific, so there's nothing to extract.
    <div className="reporting-page">
      <div className="reporting-range-bar">
        <DateRangePicker from={from} to={to} preset={preset} onPreset={setPreset} onCustom={setCustom} />
        <span className="reporting-range-label">{rangeLabel}</span>
      </div>
      <div className="panel">
        <div className="panel-title">Active time — {rangeLabel}</div>
        <Suspense fallback={<div className="chart-skeleton" />}>
          <ActivityTrendChart from={from} to={to} />
        </Suspense>
      </div>
      <div className="main-grid">
        <div className="panel">
          <div className="panel-title">Time by project</div>
          <ProjectBreakdown projects={byProject} selectedProject={selectedProject} onSelectProject={setSelectedProject} />
        </div>
        <div className="panel">
          <div className="panel-title">Time by project — treemap</div>
          <Suspense fallback={<div className="chart-skeleton" />}>
            <ProjectTreemap projects={byProject} selectedProject={selectedProject} onSelectProject={setSelectedProject} />
          </Suspense>
        </div>
      </div>
    </div>
  )
}
```

- [ ] **Step 2: Typecheck and lint**

Run (from `src/AiObservatory.Web`): `npx tsc -b --noEmit && npx eslint src/pages/ActivityPage.tsx`
Expected: no errors.

- [ ] **Step 3: Commit**

```bash
git add src/AiObservatory.Web/src/pages/ActivityPage.tsx
git commit -m "feat: assemble activity dashboard page"
```

---

### Task 12: Wire the Activity tab into `Dashboard.tsx`, admin-gated

**Files:**
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx`

**Interfaces:**
- Consumes: `ActivityPage` (Task 11), `urlApiKey` from `../auth/msal` (existing).

- [ ] **Step 1: Add the tab type, import, and gating import**

In `src/AiObservatory.Web/src/pages/Dashboard.tsx`, update the type and imports:

```tsx
import ReportingPage from './ReportingPage'
import ActivityPage from './ActivityPage'
import { urlApiKey } from '../auth/msal'
```

```tsx
type DashboardTab = 'overview' | 'adversarial-review' | 'reporting' | 'activity'
```

- [ ] **Step 2: Add the tab button, gated on `!urlApiKey`**

After the "Reporting" tab button:

```tsx
        <button
          type="button"
          className={`page-nav__tab${tab === 'reporting' ? ' page-nav__tab--active' : ''}`}
          onClick={() => setTab('reporting')}
        >
          Reporting
        </button>
        {!urlApiKey && (
          <button
            type="button"
            className={`page-nav__tab${tab === 'activity' ? ' page-nav__tab--active' : ''}`}
            onClick={() => setTab('activity')}
          >
            Activity
          </button>
        )}
```

- [ ] **Step 3: Add the tab content**

After `{tab === 'reporting' && <ReportingPage />}`:

```tsx
        {tab === 'reporting' && <ReportingPage />}
        {tab === 'activity' && <ActivityPage />}
```

- [ ] **Step 4: Typecheck, lint, and run the full frontend test suite**

Run (from `src/AiObservatory.Web`):
```bash
npx tsc -b --noEmit
npx eslint .
npx vitest run
```
Expected: no type errors, no lint errors, all tests pass (including the new ones from Tasks 5–7 and 10).

- [ ] **Step 5: Manual check**

Run the dev server (`npm run dev` from `src/AiObservatory.Web`), open the app **without** a `?key=...` query param, and confirm:
- A 4th "Activity" tab appears next to Reporting.
- Clicking it shows the trend chart (or its empty state) and the two-panel project breakdown.
- Appending `?key=anything` to the URL hides the Activity tab entirely.

(There will be no real data yet — the out-of-repo capture/backfill change hasn't run — so the empty states are the expected result; this checks wiring and gating, not real numbers.)

- [ ] **Step 6: Commit**

```bash
git add src/AiObservatory.Web/src/pages/Dashboard.tsx
git commit -m "feat: add admin-only Activity tab to dashboard nav"
```

---

## Self-Review

**Spec coverage:**
- Entity, capture/upsert contract, backfill wire contract → Task 1 (entity), Task 3 (POST upsert semantics matching the spec's "replace if greater" rule via `ShouldReplaceExisting`).
- API shape (`POST /activity/sessions`, `GET /activity/daily`, `GET /activity/by-project`) → Task 3.
- Admin-only GET gating → Task 2 (filter) + Task 3 (applied to both GETs) + Task 12 (frontend tab gating).
- Frontend tab placement, trend chart, table+bar+treemap, click-to-filter → Tasks 8–12.
- No second daily-rollup table → Task 3's GETs query `ClaudeActivitySession` directly with in-memory grouping (documented inline).
- Out-of-repo capture/backfill → explicitly called out as out of scope in the plan header; not a task here.
- GitHub activity → explicitly out of scope; separate spec.

**Placeholder scan:** no TBD/TODO; every step has complete, runnable code.

**Type consistency:** `ProjectActivity { project, sessionCount, activeSeconds, sharePercent }` is used identically in Task 4 (client.ts), Task 6 (sort helper), Task 7 (treemap helper), Task 9 (table), and Task 10 (treemap) — checked field names and types match across all five. `DailyActivityResponse`/`ProjectActivityResponse` (C#, PascalCase, Task 3) serialize via the default minimal-API JSON options to camelCase on the wire (`date`/`activeSeconds`, `project`/`sessionCount`/`activeSeconds`/`sharePercent`), matching the TypeScript `DailyActivity`/`ProjectActivity` interfaces in Task 4 exactly. `ActivityEndpoints.ShouldReplaceExisting` signature is identical between its Task 3 implementation and Task 3's own tests (no cross-task drift since both are in the same task). `onSelectProject` callback signature `(project: string | null) => void` is identical across Task 9, Task 10, and Task 11's wiring.

---

Plan complete and saved to `docs/superpowers/plans/2026-07-01-claude-activity-dashboard.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
