# GitHub Activity Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Track PR, commit, and CI/Actions activity across Chris's fix-portal repos and surface it as a new "GitHub" dashboard tab.

**Architecture:** A 4th provider client in `AiObservatory.Ingest` (`GitHubActivityClient`), polled by the existing `ProviderPollingWorkerService`, calls GitHub's REST API directly (no out-of-repo hook, unlike Claude Activity) and upserts per-entity rows via a new `IGitHubActivityRepository`. Three new read-only GETs in `AiObservatory.Api` expose repo-level rollups. A 5th frontend tab renders a PR table plus a commit/CI two-panel row.

**Tech Stack:** .NET 10 minimal APIs, EF Core + Npgsql (NodaTime), xUnit v3 + AwesomeAssertions + NSubstitute, React + TypeScript + Vite + TanStack Query + Vitest.

## Global Constraints

- Repo scope: `IngestOptions.GitHubRepoAllowlist` (`string[]` of `owner/repo`) — same fix-portal/chris-fixportal repos as Claude Activity, but configured in-repo (no out-of-repo hook holds this filter).
- Auth: a single fine-grained GitHub PAT read from config (reuses the existing `GITHUB_TOKEN` key already used for Copilot metrics; this PAT now additionally needs `contents:read`, `pull-requests:read`, `actions:read`).
- Initial backfill: 30 days per repo (once, detected by "no rows yet for this repo"); ongoing poll reuses `IngestOptions.LookbackDays` (3).
- All three new GET endpoints are gated by `AdminOnlyApiKeyEndpointFilter` (repo names/PR titles are as revealing as project names).
- Commits and CI runs are exposed only as repo-level rollups from the API — no per-commit/per-run GET.
- Rate limiting: check `X-RateLimit-Remaining` after each GitHub API call; abort the rest of the poll cycle (not the whole worker) if it drops below 50, log a warning, resume next cycle.
- A single repo's access failure (403/404) is logged and skipped; it must not fail the rest of the poll cycle.
- New test work uses AwesomeAssertions (`.Should()`), never `Assert.*`; NSubstitute for mocks; xUnit v3 (matches this repo's existing test projects).
- NodaTime `Instant` for all new entity timestamps; inject `IClock`, never `SystemClock.Instance`/`DateTime.UtcNow` inside application code (only at DI registration, matching the existing `Program.cs` pattern).

---

### Task 1: `GitHub*` entities + DbContext registration

**Files:**
- Create: `src/AiObservatory.Data/Entities/GitHubPullRequest.cs`
- Create: `src/AiObservatory.Data/Entities/GitHubCommit.cs`
- Create: `src/AiObservatory.Data/Entities/GitHubWorkflowRun.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`

**Interfaces:**
- Produces: `GitHubPullRequest`, `GitHubCommit`, `GitHubWorkflowRun` entity classes; `AiObservatoryDbContext.GitHubPullRequests`, `.GitHubCommits`, `.GitHubWorkflowRuns` `DbSet<T>` properties — every later task reads/writes through these.

- [ ] **Step 1: Create the three entity classes**

`src/AiObservatory.Data/Entities/GitHubPullRequest.cs`:
```csharp
using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class GitHubPullRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Repo { get; init; } = "";
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string State { get; init; } = ""; // open / merged / closed
    public Instant CreatedAt { get; init; }
    public Instant? MergedAt { get; init; }
    public Instant? ClosedAt { get; init; }
    public Instant? FirstReviewAt { get; init; }
    public int ReviewCount { get; init; }
    public Instant IngestedAt { get; init; }
}
```

`src/AiObservatory.Data/Entities/GitHubCommit.cs`:
```csharp
using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class GitHubCommit
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Repo { get; init; } = "";
    public string Sha { get; init; } = "";
    public string Author { get; init; } = "";
    public Instant CommittedAt { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public Instant IngestedAt { get; init; }
}
```

`src/AiObservatory.Data/Entities/GitHubWorkflowRun.cs`:
```csharp
using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class GitHubWorkflowRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Repo { get; init; } = "";
    public long RunId { get; init; }
    public string WorkflowName { get; init; } = "";
    public string Status { get; init; } = ""; // success / failure / cancelled / in_progress / queued
    public Instant CreatedAt { get; init; }
    public Instant IngestedAt { get; init; }
}
```

- [ ] **Step 2: Register DbSets and Fluent config in `AiObservatoryDbContext`**

In `src/AiObservatory.Data/AiObservatoryDbContext.cs`, add three properties alongside the existing `DbSet<ClaudeActivitySession>` line:

```csharp
public DbSet<GitHubPullRequest> GitHubPullRequests => Set<GitHubPullRequest>();
public DbSet<GitHubCommit> GitHubCommits => Set<GitHubCommit>();
public DbSet<GitHubWorkflowRun> GitHubWorkflowRuns => Set<GitHubWorkflowRun>();
```

Add to `OnModelCreating`, after the `ClaudeActivitySession` block:

```csharp
modelBuilder.Entity<GitHubPullRequest>(b =>
{
    b.Property(p => p.Repo).HasMaxLength(200).IsRequired();
    b.Property(p => p.Title).HasMaxLength(500).IsRequired();
    b.Property(p => p.Author).HasMaxLength(200).IsRequired();
    b.Property(p => p.State).HasMaxLength(20).IsRequired();
    b.HasIndex(p => new { p.Repo, p.Number }).IsUnique();
    b.HasIndex(p => p.CreatedAt);
    b.ToTable(t => t.HasCheckConstraint("CK_GitHubPullRequest_ReviewCount_NonNegative", "\"ReviewCount\" >= 0"));
});

modelBuilder.Entity<GitHubCommit>(b =>
{
    b.Property(c => c.Repo).HasMaxLength(200).IsRequired();
    b.Property(c => c.Sha).HasMaxLength(40).IsRequired();
    b.Property(c => c.Author).HasMaxLength(200).IsRequired();
    b.HasIndex(c => new { c.Repo, c.Sha }).IsUnique();
    b.HasIndex(c => c.CommittedAt);
    b.ToTable(t =>
    {
        t.HasCheckConstraint("CK_GitHubCommit_Additions_NonNegative", "\"Additions\" >= 0");
        t.HasCheckConstraint("CK_GitHubCommit_Deletions_NonNegative", "\"Deletions\" >= 0");
    });
});

modelBuilder.Entity<GitHubWorkflowRun>(b =>
{
    b.Property(r => r.Repo).HasMaxLength(200).IsRequired();
    b.Property(r => r.WorkflowName).HasMaxLength(200).IsRequired();
    b.Property(r => r.Status).HasMaxLength(20).IsRequired();
    b.HasIndex(r => new { r.Repo, r.RunId }).IsUnique();
    b.HasIndex(r => r.CreatedAt);
});
```

- [ ] **Step 3: Build to confirm compilation**

Run: `dotnet build src/AiObservatory.Data/AiObservatory.Data.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```
git add src/AiObservatory.Data/Entities/GitHubPullRequest.cs src/AiObservatory.Data/Entities/GitHubCommit.cs src/AiObservatory.Data/Entities/GitHubWorkflowRun.cs src/AiObservatory.Data/AiObservatoryDbContext.cs
git commit -m "feat(data): add GitHub PR/commit/workflow-run entities"
```

---

### Task 2: EF migration for the three new tables

**Files:**
- Create: `src/AiObservatory.Data/Migrations/<timestamp>_AddGitHubActivity.cs` (and `.Designer.cs`)
- Modify: `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs` (auto-generated)

**Interfaces:**
- Consumes: the entities and `OnModelCreating` config from Task 1.
- Produces: the `GitHubPullRequests`, `GitHubCommits`, `GitHubWorkflowRuns` tables that Task 4's repository writes to and Task 13's endpoints read from.

- [ ] **Step 1: Generate the migration**

Run: `dotnet ef migrations add AddGitHubActivity --project src/AiObservatory.Data --startup-project src/AiObservatory.Api`
Expected: `Done. To undo this action, use 'ef migrations remove'` and three new files under `src/AiObservatory.Data/Migrations/`.

- [ ] **Step 2: Read the generated `Up()`/`Down()` and confirm it matches Task 1's Fluent config**

Open the new `<timestamp>_AddGitHubActivity.cs` and confirm: three `CreateTable` calls (`GitHubPullRequests`, `GitHubCommits`, `GitHubWorkflowRuns`), three unique `CreateIndex` calls on `(Repo, Number)` / `(Repo, Sha)` / `(Repo, RunId)`, and the three `CheckConstraint` calls from Step 2 of Task 1. If anything is missing, re-run Task 1 Step 2 and regenerate — do not hand-edit the migration.

- [ ] **Step 3: Apply the migration against a local Postgres to verify it runs cleanly**

Requires a local Postgres reachable at `127.0.0.1:5432` (per this repo's existing test convention — `docker-compose.yml` in the repo root brings one up if not already running).

Run: `dotnet ef database update --project src/AiObservatory.Data --startup-project src/AiObservatory.Api --connection "Host=127.0.0.1;Database=aiobs_dev;Username=postgres;Password=postgres"`
Expected: `Done.` with no errors.

- [ ] **Step 4: Commit**

```
git add src/AiObservatory.Data/Migrations/
git commit -m "feat(data): migration for GitHub activity tables"
```

---

### Task 3: `IGitHubActivityRepository` + records + implementation

**Files:**
- Create: `src/AiObservatory.Data/Repositories/IGitHubActivityRepository.cs`
- Create: `src/AiObservatory.Data/Repositories/GitHubActivityRepository.cs`
- Modify: `src/AiObservatory.Data/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `GitHubPullRequest`/`GitHubCommit`/`GitHubWorkflowRun` entities (Task 1), `AiObservatoryDbContext` DbSets (Task 1).
- Produces: `IGitHubActivityRepository` with methods
  `Task UpsertPullRequestAsync(GitHubPullRequestRecord record, Instant ingestedAt, CancellationToken ct = default)`,
  `Task UpsertCommitAsync(GitHubCommitRecord record, Instant ingestedAt, CancellationToken ct = default)`,
  `Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord record, Instant ingestedAt, CancellationToken ct = default)`,
  `Task<bool> HasAnyDataForRepoAsync(string repo, CancellationToken ct = default)` —
  Task 6 (`GitHubIngestionService`) is the consumer of all four.
  Also produces the record types `GitHubPullRequestRecord(string Repo, int Number, string Title, string Author, string State, Instant CreatedAt, Instant? MergedAt, Instant? ClosedAt, Instant? FirstReviewAt, int ReviewCount)`,
  `GitHubCommitRecord(string Repo, string Sha, string Author, Instant CommittedAt, int Additions, int Deletions)`,
  `GitHubWorkflowRunRecord(string Repo, long RunId, string WorkflowName, string Status, Instant CreatedAt)` —
  these live in the Data project (not Ingest) specifically so `AiObservatory.Ingest`'s `GitHubActivityClient` (Task 4, which references `AiObservatory.Data`) can produce them without a circular project reference.

- [ ] **Step 1: Write the interface and records**

`src/AiObservatory.Data/Repositories/IGitHubActivityRepository.cs`:
```csharp
using NodaTime;

namespace AiObservatory.Data.Repositories;

public record GitHubPullRequestRecord(
    string Repo, int Number, string Title, string Author, string State,
    Instant CreatedAt, Instant? MergedAt, Instant? ClosedAt, Instant? FirstReviewAt, int ReviewCount);

public record GitHubCommitRecord(
    string Repo, string Sha, string Author, Instant CommittedAt, int Additions, int Deletions);

public record GitHubWorkflowRunRecord(
    string Repo, long RunId, string WorkflowName, string Status, Instant CreatedAt);

public interface IGitHubActivityRepository
{
    Task UpsertPullRequestAsync(GitHubPullRequestRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertCommitAsync(GitHubCommitRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task<bool> HasAnyDataForRepoAsync(string repo, CancellationToken ct = default);
}
```

- [ ] **Step 2: Implement the repository with raw SQL upserts**

`src/AiObservatory.Data/Repositories/GitHubActivityRepository.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Repositories;

public class GitHubActivityRepository(AiObservatoryDbContext ctx) : IGitHubActivityRepository
{
    public Task UpsertPullRequestAsync(GitHubPullRequestRecord r, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubPullRequests"
                ("Id", "Repo", "Number", "Title", "Author", "State", "CreatedAt", "MergedAt", "ClosedAt", "FirstReviewAt", "ReviewCount", "IngestedAt")
            VALUES
                (gen_random_uuid(), {r.Repo}, {r.Number}, {r.Title}, {r.Author}, {r.State}, {r.CreatedAt}, {r.MergedAt}, {r.ClosedAt}, {r.FirstReviewAt}, {r.ReviewCount}, {ingestedAt})
            ON CONFLICT ("Repo", "Number") DO UPDATE SET
                "Title" = EXCLUDED."Title",
                "State" = EXCLUDED."State",
                "MergedAt" = EXCLUDED."MergedAt",
                "ClosedAt" = EXCLUDED."ClosedAt",
                "FirstReviewAt" = EXCLUDED."FirstReviewAt",
                "ReviewCount" = EXCLUDED."ReviewCount"
            """, ct);

    public Task UpsertCommitAsync(GitHubCommitRecord r, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubCommits"
                ("Id", "Repo", "Sha", "Author", "CommittedAt", "Additions", "Deletions", "IngestedAt")
            VALUES
                (gen_random_uuid(), {r.Repo}, {r.Sha}, {r.Author}, {r.CommittedAt}, {r.Additions}, {r.Deletions}, {ingestedAt})
            ON CONFLICT ("Repo", "Sha") DO NOTHING
            """, ct);

    public Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord r, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubWorkflowRuns"
                ("Id", "Repo", "RunId", "WorkflowName", "Status", "CreatedAt", "IngestedAt")
            VALUES
                (gen_random_uuid(), {r.Repo}, {r.RunId}, {r.WorkflowName}, {r.Status}, {r.CreatedAt}, {ingestedAt})
            ON CONFLICT ("Repo", "RunId") DO UPDATE SET
                "Status" = EXCLUDED."Status"
            """, ct);

    public async Task<bool> HasAnyDataForRepoAsync(string repo, CancellationToken ct = default) =>
        await ctx.GitHubPullRequests.AsNoTracking().AnyAsync(p => p.Repo == repo, ct)
        || await ctx.GitHubCommits.AsNoTracking().AnyAsync(c => c.Repo == repo, ct)
        || await ctx.GitHubWorkflowRuns.AsNoTracking().AnyAsync(r => r.Repo == repo, ct);
}
```

A commit is immutable once authored, so `UpsertCommitAsync` is `DO NOTHING` (no fields to refresh). A PR's `State`/`MergedAt`/`ClosedAt`/`ReviewCount` change across polls, so it's `DO UPDATE`. A workflow run's `Status` changes as it completes, so it's `DO UPDATE` on that one column.

- [ ] **Step 3: Register the repository in `ServiceCollectionExtensions`**

In `src/AiObservatory.Data/ServiceCollectionExtensions.cs`, add alongside the existing two `AddScoped` lines:
```csharp
services.AddScoped<IGitHubActivityRepository, GitHubActivityRepository>();
```

- [ ] **Step 4: Build to confirm compilation**

Run: `dotnet build src/AiObservatory.Data/AiObservatory.Data.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```
git add src/AiObservatory.Data/Repositories/IGitHubActivityRepository.cs src/AiObservatory.Data/Repositories/GitHubActivityRepository.cs src/AiObservatory.Data/ServiceCollectionExtensions.cs
git commit -m "feat(data): add GitHubActivityRepository with upsert-on-conflict writes"
```

---

### Task 4: Repository integration tests (real Postgres)

**Files:**
- Create: `tests/AiObservatory.Data.Tests/Repositories/GitHubActivityRepositoryTests.cs`

**Interfaces:**
- Consumes: `IGitHubActivityRepository`, `GitHubActivityRepository`, and the three record types from Task 3.

- [ ] **Step 1: Write the failing tests**

`tests/AiObservatory.Data.Tests/Repositories/GitHubActivityRepositoryTests.cs`:
```csharp
using AiObservatory.Data;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Xunit;

namespace AiObservatory.Data.Tests.Repositories;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
// Own database (aiobs_test_github), same isolation rationale as AdversarialReviewRepositoryTests.
public class GitHubActivityRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IGitHubActivityRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn) { Database = "aiobs_test_github" }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new GitHubActivityRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
        {
            await _ctx.Database.EnsureDeletedAsync();
        }
        await _ctx.DisposeAsync();
    }

    private static GitHubPullRequestRecord Pr(string state = "open", int reviewCount = 0, Instant? firstReviewAt = null) =>
        new("fix-portal/example", 42, "Add feature", "chris", state,
            Instant.FromUtc(2026, 7, 1, 9, 0), null, null, firstReviewAt, reviewCount);

    [Fact]
    public async Task UpsertPullRequestAsync_WhenNew_Inserts()
    {
        await _repo.UpsertPullRequestAsync(Pr(), Instant.FromUtc(2026, 7, 1, 10, 0), TestContext.Current.CancellationToken);

        var stored = await _ctx.GitHubPullRequests.SingleAsync(TestContext.Current.CancellationToken);
        stored.State.Should().Be("open");
        stored.ReviewCount.Should().Be(0);
    }

    [Fact]
    public async Task UpsertPullRequestAsync_WhenRepolledWithNewState_UpdatesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertPullRequestAsync(Pr(), Instant.FromUtc(2026, 7, 1, 10, 0), ct);

        await _repo.UpsertPullRequestAsync(
            Pr(state: "merged", reviewCount: 2, firstReviewAt: Instant.FromUtc(2026, 7, 1, 11, 0)),
            Instant.FromUtc(2026, 7, 2, 10, 0), ct);

        var stored = await _ctx.GitHubPullRequests.SingleAsync(ct);
        stored.State.Should().Be("merged");
        stored.ReviewCount.Should().Be(2);
        stored.FirstReviewAt.Should().Be(Instant.FromUtc(2026, 7, 1, 11, 0));
        // IngestedAt is set only on first insert — a repoll must not disturb it.
        stored.IngestedAt.Should().Be(Instant.FromUtc(2026, 7, 1, 10, 0));
    }

    [Fact]
    public async Task UpsertCommitAsync_WhenRepolled_IsNoOpNotDuplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = new GitHubCommitRecord("fix-portal/example", "abc123", "chris", Instant.FromUtc(2026, 7, 1, 9, 0), 10, 2);

        await _repo.UpsertCommitAsync(commit, Instant.FromUtc(2026, 7, 1, 10, 0), ct);
        await _repo.UpsertCommitAsync(commit, Instant.FromUtc(2026, 7, 2, 10, 0), ct);

        var count = await _ctx.GitHubCommits.CountAsync(ct);
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpsertWorkflowRunAsync_WhenStatusChanges_UpdatesStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = new GitHubWorkflowRunRecord("fix-portal/example", 999, "ci.yml", "in_progress", Instant.FromUtc(2026, 7, 1, 9, 0));

        await _repo.UpsertWorkflowRunAsync(run, Instant.FromUtc(2026, 7, 1, 9, 0), ct);
        await _repo.UpsertWorkflowRunAsync(run with { Status = "success" }, Instant.FromUtc(2026, 7, 1, 9, 5), ct);

        var stored = await _ctx.GitHubWorkflowRuns.SingleAsync(ct);
        stored.Status.Should().Be("success");
    }

    [Fact]
    public async Task HasAnyDataForRepoAsync_WhenNoRowsForRepo_ReturnsFalse()
    {
        var result = await _repo.HasAnyDataForRepoAsync("fix-portal/never-seen", TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAnyDataForRepoAsync_WhenARowExists_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertCommitAsync(
            new GitHubCommitRecord("fix-portal/example", "abc123", "chris", Instant.FromUtc(2026, 7, 1, 9, 0), 1, 0),
            Instant.FromUtc(2026, 7, 1, 9, 0), ct);

        var result = await _repo.HasAnyDataForRepoAsync("fix-portal/example", ct);
        result.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail (or are skipped without Postgres)**

Run: `dotnet test tests/AiObservatory.Data.Tests --filter "FullyQualifiedName~GitHubActivityRepositoryTests"`
Expected (with local Postgres running per `docker-compose.yml`): FAIL — `GitHubActivityRepository`/`GitHubPullRequestRecord` etc. do not exist yet is not applicable (they exist from Task 3); expect instead a clean compile and then real assertion failures if the SQL is wrong, or all passing if Tasks 1-3 are correct. If no local Postgres is reachable, this suite fails with a connection error — expected per this repo's known local-environment constraint.

- [ ] **Step 3: Fix any failing assertions against the Task 3 implementation, then re-run**

Run: `dotnet test tests/AiObservatory.Data.Tests --filter "FullyQualifiedName~GitHubActivityRepositoryTests"`
Expected: PASS, 6/6.

- [ ] **Step 4: Commit**

```
git add tests/AiObservatory.Data.Tests/Repositories/GitHubActivityRepositoryTests.cs
git commit -m "test(data): cover GitHubActivityRepository upsert semantics"
```

---

### Task 5: `IngestOptions.GitHubRepoAllowlist`

**Files:**
- Modify: `src/AiObservatory.Ingest/IngestOptions.cs`

**Interfaces:**
- Produces: `IngestOptions.GitHubRepoAllowlist` (`string[]`, default `[]`) — consumed by Task 8 (`GitHubIngestionService`) and Task 9 (`Program.cs` registration gate).

- [ ] **Step 1: Add the property**

In `src/AiObservatory.Ingest/IngestOptions.cs`, add after `LookbackDays`:
```csharp
    // owner/repo pairs to poll for PR/commit/CI activity. Empty disables the
    // GitHub Activity client entirely (see Program.cs) — there is no out-of-repo
    // hook holding this filter, unlike Claude Activity's project allowlist.
    public string[] GitHubRepoAllowlist { get; set; } = [];
```

- [ ] **Step 2: Build to confirm compilation**

Run: `dotnet build src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```
git add src/AiObservatory.Ingest/IngestOptions.cs
git commit -m "feat(ingest): add GitHubRepoAllowlist option"
```

---

### Task 6: `GitHubActivityClient` — pull requests + reviews

**Files:**
- Create: `src/AiObservatory.Ingest/Services/GitHub/IGitHubActivityClient.cs`
- Create: `src/AiObservatory.Ingest/Services/GitHub/GitHubActivityClient.cs`
- Create: `src/AiObservatory.Ingest/Services/GitHub/GitHubRateLimitExceededException.cs`
- Test: `tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs` (PR portion only — commits/runs added in Tasks 7-8)

**Interfaces:**
- Consumes: `GitHubPullRequestRecord` from `AiObservatory.Data.Repositories` (Task 3).
- Produces: `IGitHubActivityClient.GetPullRequestsAsync(string repo, LocalDate since, CancellationToken ct = default) : Task<IReadOnlyList<GitHubPullRequestRecord>>` — consumed by Task 8 (`GitHubIngestionService`). Also produces `GitHubRateLimitExceededException`, thrown by every fetch method in this client (Tasks 6-8) and caught by Task 8.

- [ ] **Step 1: Write the failing test for pagination + rate-limit behavior**

`tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using AiObservatory.Ingest.Services.GitHub;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public class GitHubActivityClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(string json, int rateLimitRemaining = 4999)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-RateLimit-Remaining", rateLimitRemaining.ToString());
        return response;
    }

    private static GitHubActivityClient CreateSut(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") }, NullLogger<GitHubActivityClient>.Instance);

    [Fact]
    public async Task GetPullRequestsAsync_ParsesFieldsAndFetchesReviewCount()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse("""[{"submitted_at":"2026-07-01T12:00:00Z"},{"submitted_at":"2026-07-01T14:00:00Z"}]""");
            }
            return JsonResponse("""
                [{"number":42,"title":"Add feature","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]
                """);
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        var pr = result.Single();
        pr.Number.Should().Be(42);
        pr.Author.Should().Be("chris");
        pr.State.Should().Be("open");
        pr.ReviewCount.Should().Be(2);
        pr.FirstReviewAt.Should().Be(Instant.FromUtc(2026, 7, 1, 12, 0));
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenNoReviews_ReviewCountZeroAndFirstReviewAtNull()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews")) return JsonResponse("[]");
            return JsonResponse("""
                [{"number":1,"title":"WIP","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]
                """);
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        result.Single().ReviewCount.Should().Be(0);
        result.Single().FirstReviewAt.Should().BeNull();
    }

    [Fact]
    public async Task GetPullRequestsAsync_PaginatesUntilShortPage()
    {
        var callCount = 0;
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews")) return JsonResponse("[]");
            callCount++;
            // Page 1: a full 100-row page (forces a second page request); page 2: short page, stop.
            if (req.RequestUri!.ToString().Contains("page=2"))
            {
                return JsonResponse("""[{"number":2,"title":"b","user":{"login":"chris"},"state":"open","created_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]""");
            }
            var page = string.Join(",", Enumerable.Range(1, 100).Select(i =>
                $$"""{"number":{{i}},"title":"t","user":{"login":"chris"},"state":"open","created_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}"""));
            return JsonResponse($"[{page}]");
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        result.Should().HaveCount(101); // 100 from page 1 + 1 from page 2
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenRateLimitNearZero_ThrowsRateLimitException()
    {
        var handler = new StubHandler(_ => JsonResponse("[]", rateLimitRemaining: 10));
        var sut = CreateSut(handler);

        var act = () => sut.GetPullRequestsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GitHubRateLimitExceededException>();
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenRepoForbidden_ThrowsHttpRequestException()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var sut = CreateSut(handler);

        var act = () => sut.GetPullRequestsAsync("fix-portal/private-no-access", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AiObservatory.Ingest.Tests --filter "FullyQualifiedName~GitHubActivityClientTests"`
Expected: FAIL — `GitHubActivityClient` does not exist.

- [ ] **Step 3: Write the exception type and client**

`src/AiObservatory.Ingest/Services/GitHub/GitHubRateLimitExceededException.cs`:
```csharp
namespace AiObservatory.Ingest.Services.GitHub;

// Thrown when X-RateLimit-Remaining drops below the safety threshold mid-poll.
// The ingestion service (GitHubIngestionService) catches this to abort the rest
// of THIS poll cycle's repos without failing the whole worker — see Task 8.
public class GitHubRateLimitExceededException(int remaining) : Exception(
    $"GitHub API rate limit nearly exhausted ({remaining} requests remaining); aborting remaining repos this cycle.");
```

`src/AiObservatory.Ingest/Services/GitHub/IGitHubActivityClient.cs`:
```csharp
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Ingest.Services.GitHub;

public interface IGitHubActivityClient
{
    Task<IReadOnlyList<GitHubPullRequestRecord>> GetPullRequestsAsync(string repo, LocalDate since, CancellationToken ct = default);
    Task<IReadOnlyList<GitHubCommitRecord>> GetCommitsAsync(string repo, LocalDate since, CancellationToken ct = default);
    Task<IReadOnlyList<GitHubWorkflowRunRecord>> GetWorkflowRunsAsync(string repo, LocalDate since, CancellationToken ct = default);
}
```

`src/AiObservatory.Ingest/Services/GitHub/GitHubActivityClient.cs` (PR method only — `GetCommitsAsync`/`GetWorkflowRunsAsync` are stubbed to throw `NotImplementedException` here and filled in by Tasks 7-8):
```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.GitHub;

// Calls the GitHub REST API directly (no out-of-repo hook, unlike Claude Activity).
// Requires a PAT with contents:read, pull-requests:read, actions:read.
public class GitHubActivityClient(HttpClient http, ILogger<GitHubActivityClient> logger) : IGitHubActivityClient
{
    // Stop calling GitHub once headroom drops this low — leaves margin for other
    // callers (e.g. the Copilot client sharing the same token) within the hour window.
    private const int RateLimitFloor = 50;
    private const int PerPage = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<IReadOnlyList<GitHubPullRequestRecord>> GetPullRequestsAsync(string repo, LocalDate since, CancellationToken ct = default)
    {
        var results = new List<GitHubPullRequestRecord>();
        var page = 1;
        while (true)
        {
            // Default sort for this endpoint is created/desc (newest first) — once a page
            // yields a PR older than `since`, every PR on every later page is older too, so
            // the outer loop can stop instead of paging through a repo's entire PR history
            // on every 3-day poll.
            var response = await http.GetAsync($"/repos/{repo}/pulls?state=all&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var prs = await response.Content.ReadFromJsonAsync<List<PullRequestDto>>(JsonOptions, ct) ?? [];

            var reachedOlderThanSince = false;
            foreach (var pr in prs)
            {
                var createdAt = InstantPattern.ExtendedIso.Parse(pr.CreatedAt).Value;
                if (createdAt.InUtc().Date < since)
                {
                    reachedOlderThanSince = true;
                    break;
                }

                var (reviewCount, firstReviewAt) = await GetReviewSummaryAsync(repo, pr.Number, ct);
                results.Add(new GitHubPullRequestRecord(
                    repo, pr.Number, pr.Title, pr.User.Login, pr.State,
                    createdAt,
                    pr.MergedAt is null ? null : InstantPattern.ExtendedIso.Parse(pr.MergedAt).Value,
                    pr.ClosedAt is null ? null : InstantPattern.ExtendedIso.Parse(pr.ClosedAt).Value,
                    firstReviewAt, reviewCount));
            }

            if (reachedOlderThanSince || prs.Count < PerPage) break;
            page++;
        }
        return results;
    }

    private async Task<(int ReviewCount, Instant? FirstReviewAt)> GetReviewSummaryAsync(string repo, int number, CancellationToken ct)
    {
        var response = await http.GetAsync($"/repos/{repo}/pulls/{number}/reviews", ct);
        CheckRateLimit(response);
        response.EnsureSuccessStatusCode();
        var reviews = await response.Content.ReadFromJsonAsync<List<ReviewDto>>(JsonOptions, ct) ?? [];
        if (reviews.Count == 0) return (0, null);

        var first = reviews
            .Select(r => InstantPattern.ExtendedIso.Parse(r.SubmittedAt).Value)
            .Min();
        return (reviews.Count, first);
    }

    private void CheckRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && int.TryParse(values.FirstOrDefault(), out var remaining)
            && remaining < RateLimitFloor)
        {
            logger.LogWarning("GitHub API rate limit at {Remaining}; aborting remaining repos this poll cycle", remaining);
            throw new GitHubRateLimitExceededException(remaining);
        }
    }

    public Task<IReadOnlyList<GitHubCommitRecord>> GetCommitsAsync(string repo, LocalDate since, CancellationToken ct = default) =>
        throw new NotImplementedException("Added in Task 7");

    public Task<IReadOnlyList<GitHubWorkflowRunRecord>> GetWorkflowRunsAsync(string repo, LocalDate since, CancellationToken ct = default) =>
        throw new NotImplementedException("Added in Task 8");

    private sealed record PullRequestDto(
        int Number, string Title, PullRequestUserDto User, string State,
        string CreatedAt, string? MergedAt, string? ClosedAt);
    private sealed record PullRequestUserDto(string Login);
    private sealed record ReviewDto(string SubmittedAt);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/AiObservatory.Ingest.Tests --filter "FullyQualifiedName~GitHubActivityClientTests"`
Expected: PASS, 5/5.

- [ ] **Step 5: Commit**

```
git add src/AiObservatory.Ingest/Services/GitHub/ tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs
git commit -m "feat(ingest): GitHubActivityClient PR fetch with pagination + rate-limit guard"
```

---

### Task 7: `GitHubActivityClient` — commits with churn stats

**Files:**
- Modify: `src/AiObservatory.Ingest/Services/GitHub/GitHubActivityClient.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs`

**Interfaces:**
- Produces: `GetCommitsAsync` fully implemented (was a stub from Task 6).

- [ ] **Step 1: Write the failing tests**

Append to `GitHubActivityClientTests.cs`:
```csharp
    [Fact]
    public async Task GetCommitsAsync_FetchesShaAuthorDateAndChurnStats()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/commits/abc123"))
            {
                return JsonResponse("""{"sha":"abc123","stats":{"additions":10,"deletions":2}}""");
            }
            return JsonResponse("""
                [{"sha":"abc123","commit":{"author":{"name":"chris","date":"2026-07-01T09:00:00Z"}}}]
                """);
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCommitsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        var commit = result.Single();
        commit.Sha.Should().Be("abc123");
        commit.Author.Should().Be("chris");
        commit.Additions.Should().Be(10);
        commit.Deletions.Should().Be(2);
    }

    [Fact]
    public async Task GetCommitsAsync_PassesSinceAsQueryParam()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/commits/")) return JsonResponse("""{"sha":"x","stats":{"additions":0,"deletions":0}}""");
            return JsonResponse("[]");
        });
        var sut = CreateSut(handler);

        await sut.GetCommitsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        handler.RequestedUrls.Should().Contain(u => u.Contains("since=2026-07-01"));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AiObservatory.Ingest.Tests --filter "FullyQualifiedName~GitHubActivityClientTests"`
Expected: FAIL — `GetCommitsAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement `GetCommitsAsync`**

Replace the `GetCommitsAsync` stub in `GitHubActivityClient.cs`:
```csharp
    public async Task<IReadOnlyList<GitHubCommitRecord>> GetCommitsAsync(string repo, LocalDate since, CancellationToken ct = default)
    {
        var sinceStr = LocalDatePattern.Iso.Format(since);
        var results = new List<GitHubCommitRecord>();
        var page = 1;
        while (true)
        {
            var response = await http.GetAsync($"/repos/{repo}/commits?since={sinceStr}&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var commits = await response.Content.ReadFromJsonAsync<List<CommitListDto>>(JsonOptions, ct) ?? [];

            foreach (var c in commits)
            {
                // Per-commit call needed for churn stats — the list endpoint omits them.
                // Personal-scale repo volume keeps this within the rate-limit budget.
                var detailResponse = await http.GetAsync($"/repos/{repo}/commits/{c.Sha}", ct);
                CheckRateLimit(detailResponse);
                detailResponse.EnsureSuccessStatusCode();
                var detail = await detailResponse.Content.ReadFromJsonAsync<CommitDetailDto>(JsonOptions, ct)
                    ?? new CommitDetailDto(c.Sha, new CommitStatsDto(0, 0));

                results.Add(new GitHubCommitRecord(
                    repo, c.Sha, c.Commit.Author.Name,
                    InstantPattern.ExtendedIso.Parse(c.Commit.Author.Date).Value,
                    detail.Stats.Additions, detail.Stats.Deletions));
            }

            if (commits.Count < PerPage) break;
            page++;
        }
        return results;
    }

    private sealed record CommitListDto(string Sha, CommitInnerDto Commit);
    private sealed record CommitInnerDto(CommitAuthorDto Author);
    private sealed record CommitAuthorDto(string Name, string Date);
    private sealed record CommitDetailDto(string Sha, CommitStatsDto Stats);
    private sealed record CommitStatsDto(int Additions, int Deletions);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AiObservatory.Ingest.Tests --filter "FullyQualifiedName~GitHubActivityClientTests"`
Expected: PASS, 7/7.

- [ ] **Step 5: Commit**

```
git add src/AiObservatory.Ingest/Services/GitHub/GitHubActivityClient.cs tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs
git commit -m "feat(ingest): GitHubActivityClient commit churn fetch"
```

---

### Task 8: `GitHubActivityClient` — workflow runs

**Files:**
- Modify: `src/AiObservatory.Ingest/Services/GitHub/GitHubActivityClient.cs`
- Modify: `tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs`

**Interfaces:**
- Produces: `GetWorkflowRunsAsync` fully implemented (was a stub from Task 6).

- [ ] **Step 1: Write the failing tests**

Append to `GitHubActivityClientTests.cs`:
```csharp
    [Fact]
    public async Task GetWorkflowRunsAsync_UsesConclusionWhenCompleted()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            {"workflow_runs":[{"id":123,"name":"CI","status":"completed","conclusion":"success","created_at":"2026-07-01T09:00:00Z"}]}
            """));
        var sut = CreateSut(handler);

        var result = await sut.GetWorkflowRunsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        var run = result.Single();
        run.RunId.Should().Be(123);
        run.WorkflowName.Should().Be("CI");
        run.Status.Should().Be("success");
    }

    [Fact]
    public async Task GetWorkflowRunsAsync_UsesStatusWhenNotYetCompleted()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            {"workflow_runs":[{"id":124,"name":"CI","status":"in_progress","conclusion":null,"created_at":"2026-07-01T09:00:00Z"}]}
            """));
        var sut = CreateSut(handler);

        var result = await sut.GetWorkflowRunsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        result.Single().Status.Should().Be("in_progress");
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AiObservatory.Ingest.Tests --filter "FullyQualifiedName~GitHubActivityClientTests"`
Expected: FAIL — `GetWorkflowRunsAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement `GetWorkflowRunsAsync`**

Replace the `GetWorkflowRunsAsync` stub in `GitHubActivityClient.cs`:
```csharp
    public async Task<IReadOnlyList<GitHubWorkflowRunRecord>> GetWorkflowRunsAsync(string repo, LocalDate since, CancellationToken ct = default)
    {
        var sinceStr = LocalDatePattern.Iso.Format(since);
        var results = new List<GitHubWorkflowRunRecord>();
        var page = 1;
        while (true)
        {
            var response = await http.GetAsync($"/repos/{repo}/actions/runs?created=%3E%3D{sinceStr}&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<WorkflowRunsResponseDto>(JsonOptions, ct)
                ?? new WorkflowRunsResponseDto([]);

            foreach (var run in body.WorkflowRuns)
            {
                results.Add(new GitHubWorkflowRunRecord(
                    repo, run.Id, run.Name, run.Conclusion ?? run.Status,
                    InstantPattern.ExtendedIso.Parse(run.CreatedAt).Value));
            }

            if (body.WorkflowRuns.Count < PerPage) break;
            page++;
        }
        return results;
    }

    private sealed record WorkflowRunsResponseDto(List<WorkflowRunDto> WorkflowRuns);
    private sealed record WorkflowRunDto(long Id, string Name, string Status, string? Conclusion, string CreatedAt);
```

Note: `%3E%3D` is the URL-encoded `>=`, required by the GitHub Search-qualifier-style `created` filter on the workflow runs endpoint.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AiObservatory.Ingest.Tests --filter "FullyQualifiedName~GitHubActivityClientTests"`
Expected: PASS, 9/9.

- [ ] **Step 5: Commit**

```
git add src/AiObservatory.Ingest/Services/GitHub/GitHubActivityClient.cs tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs
git commit -m "feat(ingest): GitHubActivityClient workflow run fetch"
```

---

### Task 9: `GitHubIngestionService` — orchestration, backfill, per-repo skip

**Files:**
- Create: `src/AiObservatory.Ingest/Services/GitHub/GitHubIngestionService.cs`
- Test: `tests/AiObservatory.Ingest.Tests/Services/GitHubIngestionServiceTests.cs`

**Interfaces:**
- Consumes: `IGitHubActivityClient` (Tasks 6-8), `IGitHubActivityRepository` (Task 3), `IngestOptions.GitHubRepoAllowlist` (Task 5), `IClock`.
- Produces: `GitHubIngestionService.IngestSinceAsync(LocalDate date, CancellationToken ct = default) : Task` — consumed by Task 10 (`ProviderPollingWorkerService` wiring). Unlike the other providers' `IngestAsync(LocalDate date)` (called once per date in the lookback window), this is called **once per poll cycle** with the earliest lookback date — GitHub's API takes a `since` range, not a single day, so re-querying the same range once per date would triple the API calls for no benefit.

- [ ] **Step 1: Write the failing tests**

`tests/AiObservatory.Ingest.Tests/Services/GitHubIngestionServiceTests.cs`:
```csharp
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public class GitHubIngestionServiceTests
{
    private static IOptions<IngestOptions> Options(params string[] repos) =>
        Microsoft.Extensions.Options.Options.Create(new IngestOptions { GitHubRepoAllowlist = repos });

    [Fact]
    public async Task IngestSinceAsync_WhenRepoHasNoPriorData_UsesThirtyDayBackfillWindow()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(false);
        client.GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance);

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestSinceAsync(pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate.PlusDays(-30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_WhenRepoAlreadyHasData_UsesGivenDateNotBackfill()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(true);
        client.GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance);

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestSinceAsync(pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_PersistsEveryFetchedRecordViaRepository()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var pr = new GitHubPullRequestRecord("fix-portal/example", 1, "t", "chris", "open", Instant.FromUtc(2026, 7, 1, 9, 0), null, null, null, 0);
        client.GetPullRequestsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([pr]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance);
        await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await repo.Received(1).UpsertPullRequestAsync(pr, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_WhenOneRepoThrows403_SkipsItAndContinuesWithNextRepo()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        client.GetPullRequestsAsync("fix-portal/broken", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new HttpRequestException("403")));
        client.GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/broken", "fix-portal/ok"), NullLogger<GitHubIngestionService>.Instance);

        await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_WhenRateLimitExceeded_AbortsRemainingReposWithoutThrowing()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        client.GetPullRequestsAsync("fix-portal/first", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new GitHubRateLimitExceededException(10)));
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/first", "fix-portal/second"), NullLogger<GitHubIngestionService>.Instance);

        await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await client.DidNotReceive().GetPullRequestsAsync("fix-portal/second", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AiObservatory.Ingest.Tests --filter "FullyQualifiedName~GitHubIngestionServiceTests"`
Expected: FAIL — `GitHubIngestionService` does not exist.

- [ ] **Step 3: Implement `GitHubIngestionService`**

`src/AiObservatory.Ingest/Services/GitHub/GitHubIngestionService.cs`:
```csharp
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest.Services.GitHub;

// Called once per poll cycle (not once per lookback date, unlike the other
// providers) — GitHub's API takes a since-date range, so re-querying the same
// range per date would triple API calls for no benefit. See IGitHubActivityClient.
public class GitHubIngestionService(
    IGitHubActivityClient client,
    IGitHubActivityRepository repository,
    IOptions<IngestOptions> options,
    ILogger<GitHubIngestionService> logger)
{
    private const int BackfillDays = 30;

    public async Task IngestSinceAsync(LocalDate date, CancellationToken ct = default)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        foreach (var repo in options.Value.GitHubRepoAllowlist)
        {
            try
            {
                var hasData = await repository.HasAnyDataForRepoAsync(repo, ct);
                var since = hasData ? date : date.PlusDays(-BackfillDays);

                var prs = await client.GetPullRequestsAsync(repo, since, ct);
                foreach (var pr in prs) await repository.UpsertPullRequestAsync(pr, now, ct);

                var commits = await client.GetCommitsAsync(repo, since, ct);
                foreach (var c in commits) await repository.UpsertCommitAsync(c, now, ct);

                var runs = await client.GetWorkflowRunsAsync(repo, since, ct);
                foreach (var r in runs) await repository.UpsertWorkflowRunAsync(r, now, ct);

                logger.LogInformation(
                    "GitHub: ingested {PrCount} PRs, {CommitCount} commits, {RunCount} workflow runs for {Repo}",
                    prs.Count, commits.Count, runs.Count, repo);
            }
            catch (GitHubRateLimitExceededException)
            {
                logger.LogWarning("GitHub: aborting remaining repos this poll cycle due to rate limit");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GitHub: failed to ingest {Repo}; skipping for this cycle", repo);
            }
        }
    }
}
```

Note: `SystemClock.Instance` is used directly here rather than an injected `IClock` because this service has no other time-dependent logic to make testable (only stamps `IngestedAt`) — consistent with `CopilotIngestionService`, which takes `IClock` for the same reason but this service's tests don't assert on `IngestedAt`'s exact value, only that upsert is called. If a future test needs to assert the timestamp, inject `IClock` then.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AiObservatory.Ingest.Tests --filter "FullyQualifiedName~GitHubIngestionServiceTests"`
Expected: PASS, 5/5.

- [ ] **Step 5: Commit**

```
git add src/AiObservatory.Ingest/Services/GitHub/GitHubIngestionService.cs tests/AiObservatory.Ingest.Tests/Services/GitHubIngestionServiceTests.cs
git commit -m "feat(ingest): GitHubIngestionService with backfill detection and per-repo error isolation"
```

---

### Task 10: Wire GitHub into `ProviderPollingWorkerService` and `Program.cs`

**Files:**
- Modify: `src/AiObservatory.Ingest/ProviderPollingWorkerService.cs`
- Modify: `src/AiObservatory.Ingest/Program.cs`

**Interfaces:**
- Consumes: `GitHubIngestionService.IngestSinceAsync` (Task 9), `IngestOptions.GitHubRepoAllowlist` (Task 5), `IGitHubActivityClient`/`GitHubActivityClient` (Tasks 6-8).

- [ ] **Step 1: Add a GitHub-specific branch to `RunPollAsync`**

In `src/AiObservatory.Ingest/ProviderPollingWorkerService.cs`, modify `RunPollAsync` to add a GitHub call using `dates[0]` (the earliest date in the lookback window) instead of the generic per-date `TryIngestAsync`:

```csharp
    private async Task RunPollAsync(IReadOnlyList<LocalDate> dates, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        await TryIngestAsync<AnthropicIngestionService>(sp, "Anthropic",
            (s, d) => s.IngestAsync(d, ct), dates);
        await TryIngestAsync<CopilotIngestionService>(sp, "Copilot",
            (s, d) => s.IngestAsync(d, ct), dates);
        await TryIngestAsync<GoogleIngestionService>(sp, "Google",
            (s, d) => s.IngestAsync(d, ct), dates);
        await TryIngestAsync<OpenAiIngestionService>(sp, "OpenAI",
            (s, d) => s.IngestAsync(d, ct), dates);
        // GitHub takes a since-date RANGE per call (unlike the other providers'
        // single-day calls), so it's invoked once per cycle with the earliest
        // lookback date, not once per date in `dates`.
        await TryIngestAsync<GitHubIngestionService>(sp, "GitHub",
            (s, _) => s.IngestSinceAsync(dates[0], ct), [dates[0]]);
    }
```

Add the using at the top of the file:
```csharp
using AiObservatory.Ingest.Services.GitHub;
```

- [ ] **Step 2: Register the client and ingestion service in `Program.cs`**

In `src/AiObservatory.Ingest/Program.cs`, add after the OpenAI block (before `services.AddHostedService<ProviderPollingWorkerService>();`):

```csharp
        // GitHub Activity — enabled when GITHUB_TOKEN is set AND at least one repo is
        // allowlisted. Reuses the same GITHUB_TOKEN as Copilot metrics; this PAT now also
        // needs contents:read, pull-requests:read, actions:read (in addition to
        // manage_billing:copilot if Copilot metrics are also enabled).
        var githubRepoAllowlist = cfg.GetSection($"{IngestOptions.SectionName}:GitHubRepoAllowlist").Get<string[]>() ?? [];
        if (IsConfigured(githubToken) && githubRepoAllowlist.Length > 0)
        {
            services.AddHttpClient<IGitHubActivityClient, GitHubActivityClient>(c =>
            {
                c.BaseAddress = new Uri("https://api.github.com");
                c.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
                c.DefaultRequestHeaders.Add("User-Agent", "fpaiobs-ingest");
                c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            });
            services.AddScoped<GitHubIngestionService>();
        }
```

Add the using at the top:
```csharp
using AiObservatory.Ingest.Services.GitHub;
```

- [ ] **Step 3: Build to confirm compilation**

Run: `dotnet build src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```
git add src/AiObservatory.Ingest/ProviderPollingWorkerService.cs src/AiObservatory.Ingest/Program.cs
git commit -m "feat(ingest): wire GitHub Activity into the polling worker"
```

---

### Task 11: `GitHubActivityEndpoints` — three GETs

**Files:**
- Create: `src/AiObservatory.Api/Endpoints/GitHubActivityEndpoints.cs`
- Test: `tests/AiObservatory.Api.Tests/GitHubActivityEndpointsTests.cs`
- Modify: `src/AiObservatory.Api/Program.cs`

**Interfaces:**
- Consumes: `AiObservatoryDbContext.GitHubPullRequests`/`.GitHubCommits`/`.GitHubWorkflowRuns` (Task 1), `ActivityEndpoints.TryParseDateRange` (existing, reused — DRY, no duplicate date-range parser), `AdminOnlyApiKeyEndpointFilter` (existing).
- Produces: `GitHubActivityEndpoints.MapGitHubActivityEndpoints(this IEndpointRouteBuilder app)`, `GitHubActivityEndpoints.ComputeTurnaroundHours(Instant createdAt, Instant? firstReviewAt) : double?` (pure, unit-tested), the response records `GitHubPrResponse`, `GitHubCommitSummaryResponse`, `GitHubCiResponse`.

- [ ] **Step 1: Write the failing unit tests for the pure turnaround calc**

`tests/AiObservatory.Api.Tests/GitHubActivityEndpointsTests.cs`:
```csharp
using AiObservatory.Api.Endpoints;
using AwesomeAssertions;
using NodaTime;
using Xunit;

namespace AiObservatory.Api.Tests;

public class GitHubActivityEndpointsTests
{
    [Fact]
    public void ComputeTurnaroundHours_WhenFirstReviewAtIsNull_ReturnsNull()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(Instant.FromUtc(2026, 7, 1, 9, 0), null);
        result.Should().BeNull();
    }

    [Fact]
    public void ComputeTurnaroundHours_WhenReviewedThreeHoursLater_ReturnsThree()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(
            Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        result.Should().Be(3.0);
    }

    [Fact]
    public void ComputeTurnaroundHours_RoundsToOneDecimalPlace()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(
            Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 9, 40));
        result.Should().Be(0.7); // 40 minutes = 0.666...h, rounds to 0.7
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AiObservatory.Api.Tests --filter "FullyQualifiedName~GitHubActivityEndpointsTests"`
Expected: FAIL — `GitHubActivityEndpoints` does not exist.

- [ ] **Step 3: Implement the endpoints file**

`src/AiObservatory.Api/Endpoints/GitHubActivityEndpoints.cs`:
```csharp
using AiObservatory.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class GitHubActivityEndpoints
{
    public static void MapGitHubActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/github/prs", async (
            AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }
            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var prs = await db.GitHubPullRequests
                .AsNoTracking()
                .Where(p => p.CreatedAt >= startInstant && p.CreatedAt < endInstant)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(ct);

            var response = prs.Select(p => new GitHubPrResponse(
                p.Repo, p.Number, p.Title, p.Author, p.State,
                p.CreatedAt.ToString(), p.MergedAt?.ToString(),
                p.ReviewCount, ComputeTurnaroundHours(p.CreatedAt, p.FirstReviewAt)));

            return Results.Ok(response);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet("/github/commits/summary", async (
            AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }
            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var commits = await db.GitHubCommits
                .AsNoTracking()
                .Where(c => c.CommittedAt >= startInstant && c.CommittedAt < endInstant)
                .Select(c => new { c.Repo, c.Additions, c.Deletions })
                .ToListAsync(ct);

            var byRepo = commits
                .GroupBy(c => c.Repo)
                .Select(g => new GitHubCommitSummaryResponse(g.Key, g.Count(), g.Sum(c => c.Additions), g.Sum(c => c.Deletions)))
                .OrderByDescending(r => r.CommitCount)
                .ToList();

            return Results.Ok(byRepo);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet("/github/ci", async (
            AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }
            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var runs = await db.GitHubWorkflowRuns
                .AsNoTracking()
                .Where(r => r.CreatedAt >= startInstant && r.CreatedAt < endInstant)
                .Select(r => new { r.Repo, r.WorkflowName, r.Status })
                .ToListAsync(ct);

            var byRepoWorkflow = runs
                .GroupBy(r => (r.Repo, r.WorkflowName))
                .Select(g =>
                {
                    var total = g.Count();
                    var failed = g.Count(r => r.Status == "failure");
                    return new GitHubCiResponse(
                        g.Key.Repo, g.Key.WorkflowName, total, failed,
                        total > 0 ? Math.Round((total - failed) * 100.0 / total, 1) : 0);
                })
                .OrderByDescending(r => r.TotalRuns)
                .ToList();

            return Results.Ok(byRepoWorkflow);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
    }

    public static double? ComputeTurnaroundHours(Instant createdAt, Instant? firstReviewAt)
    {
        if (firstReviewAt is not { } reviewedAt) return null;
        return Math.Round((reviewedAt - createdAt).TotalHours, 1);
    }
}

public sealed record GitHubPrResponse(
    string Repo, int Number, string Title, string Author, string State,
    string CreatedAt, string? MergedAt, int ReviewCount, double? TurnaroundHours);

public sealed record GitHubCommitSummaryResponse(string Repo, int CommitCount, int Additions, int Deletions);

public sealed record GitHubCiResponse(string Repo, string WorkflowName, int TotalRuns, int FailedRuns, double SuccessRate);
```

- [ ] **Step 4: Wire into `Program.cs`**

In `src/AiObservatory.Api/Program.cs`, add after `api.MapActivityEndpoints();`:
```csharp
api.MapGitHubActivityEndpoints();
```

- [ ] **Step 5: Run to verify pass, then build**

Run: `dotnet test tests/AiObservatory.Api.Tests --filter "FullyQualifiedName~GitHubActivityEndpointsTests"`
Expected: PASS, 3/3.

Run: `dotnet build src/AiObservatory.Api/AiObservatory.Api.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```
git add src/AiObservatory.Api/Endpoints/GitHubActivityEndpoints.cs src/AiObservatory.Api/Program.cs tests/AiObservatory.Api.Tests/GitHubActivityEndpointsTests.cs
git commit -m "feat(api): GitHub PR/commit-summary/CI endpoints"
```

---

### Task 12: Frontend `client.ts` types + fetchers

**Files:**
- Modify: `src/AiObservatory.Web/src/api/client.ts`

**Interfaces:**
- Produces: `GitHubPr`, `GitHubCommitSummary`, `GitHubCiSummary` TypeScript interfaces and `getGitHubPrs`, `getGitHubCommitSummary`, `getGitHubCi` fetchers — consumed by Task 13 (`queries.ts`).

- [ ] **Step 1: Add interfaces and fetchers**

In `src/AiObservatory.Web/src/api/client.ts`, add after the `ProjectActivity`/`getActivityByProject` block:
```typescript
export interface GitHubPr {
  repo: string
  number: number
  title: string
  author: string
  state: 'open' | 'merged' | 'closed'
  createdAt: string
  mergedAt: string | null
  reviewCount: number
  turnaroundHours: number | null
}

export interface GitHubCommitSummary {
  repo: string
  commitCount: number
  additions: number
  deletions: number
}

export interface GitHubCiSummary {
  repo: string
  workflowName: string
  totalRuns: number
  failedRuns: number
  successRate: number
}

export const getGitHubPrs = (from?: string, to?: string) =>
  getJson<GitHubPr[]>('/github/prs', { from, to })

export const getGitHubCommitSummary = (from?: string, to?: string) =>
  getJson<GitHubCommitSummary[]>('/github/commits/summary', { from, to })

export const getGitHubCi = (from?: string, to?: string) =>
  getJson<GitHubCiSummary[]>('/github/ci', { from, to })
```

- [ ] **Step 2: Typecheck**

Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web`)
Expected: no errors.

- [ ] **Step 3: Commit**

```
git add src/AiObservatory.Web/src/api/client.ts
git commit -m "feat(web): GitHub activity API client types and fetchers"
```

---

### Task 13: Frontend `queries.ts` hooks

**Files:**
- Modify: `src/AiObservatory.Web/src/api/queries.ts`

**Interfaces:**
- Consumes: `getGitHubPrs`, `getGitHubCommitSummary`, `getGitHubCi`, `GitHubPr`, `GitHubCommitSummary`, `GitHubCiSummary` (Task 12).
- Produces: `useGitHubPrs(from?, to?)`, `useGitHubCommitSummary(from?, to?)`, `useGitHubCi(from?, to?)` — each returning `{ data, isError, isLoading }` — consumed by Task 16 (`GitHubPage.tsx`).

- [ ] **Step 1: Add the hooks**

In `src/AiObservatory.Web/src/api/queries.ts`, update the import block to add:
```typescript
  getGitHubPrs, getGitHubCommitSummary, getGitHubCi,
  type GitHubPr, type GitHubCommitSummary, type GitHubCiSummary,
```

Then add after `useActivityByProject`:
```typescript
export function useGitHubPrs(from?: Date, to?: Date): { prs: GitHubPr[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-prs', localDate(from!), localDate(to!)] : ['github-prs'],
    queryFn: hasRange ? () => getGitHubPrs(localDate(from!), localDate(to!)) : () => getGitHubPrs(),
  })
  return { prs: data, isError, isLoading: isPending }
}

export function useGitHubCommitSummary(from?: Date, to?: Date): { summary: GitHubCommitSummary[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-commits-summary', localDate(from!), localDate(to!)] : ['github-commits-summary'],
    queryFn: hasRange ? () => getGitHubCommitSummary(localDate(from!), localDate(to!)) : () => getGitHubCommitSummary(),
  })
  return { summary: data, isError, isLoading: isPending }
}

export function useGitHubCi(from?: Date, to?: Date): { ci: GitHubCiSummary[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-ci', localDate(from!), localDate(to!)] : ['github-ci'],
    queryFn: hasRange ? () => getGitHubCi(localDate(from!), localDate(to!)) : () => getGitHubCi(),
  })
  return { ci: data, isError, isLoading: isPending }
}
```

- [ ] **Step 2: Typecheck**

Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web`)
Expected: no errors.

- [ ] **Step 3: Commit**

```
git add src/AiObservatory.Web/src/api/queries.ts
git commit -m "feat(web): GitHub activity query hooks"
```

---

### Task 14: `githubSort.ts` — sort/filter helpers + tests

**Files:**
- Create: `src/AiObservatory.Web/src/components/githubSort.ts`
- Test: `src/AiObservatory.Web/src/components/githubSort.test.ts`

**Interfaces:**
- Consumes: `GitHubPr`, `GitHubCommitSummary`, `GitHubCiSummary` (Task 12).
- Produces: `filterPrs`, `sortPrs`, `PrSortField` type; `sortCommitSummaries`, `CommitSortField` type; `sortCiSummaries`, `CiSortField` type — consumed by Task 16 (`GitHubPage.tsx` and its sub-tables).

- [ ] **Step 1: Write the failing tests**

`src/AiObservatory.Web/src/components/githubSort.test.ts`:
```typescript
import { describe, expect, it } from 'vitest'
import { filterPrs, sortPrs, sortCommitSummaries, sortCiSummaries } from './githubSort'
import type { GitHubPr, GitHubCommitSummary, GitHubCiSummary } from '../api/client'

const pr = (overrides: Partial<GitHubPr>): GitHubPr => ({
  repo: 'fix-portal/example', number: 1, title: 'A PR', author: 'chris', state: 'open',
  createdAt: '2026-07-01T09:00:00Z', mergedAt: null, reviewCount: 0, turnaroundHours: null,
  ...overrides,
})

describe('filterPrs', () => {
  it('matches on title case-insensitively', () => {
    const result = filterPrs([pr({ title: 'Add Feature' }), pr({ title: 'Fix bug' })], 'feature')
    expect(result).toHaveLength(1)
    expect(result[0].title).toBe('Add Feature')
  })

  it('returns everything when query is blank', () => {
    const items = [pr({}), pr({ number: 2 })]
    expect(filterPrs(items, '  ')).toHaveLength(2)
  })
})

describe('sortPrs', () => {
  it('sorts by createdAt descending by default direction', () => {
    const older = pr({ number: 1, createdAt: '2026-07-01T09:00:00Z' })
    const newer = pr({ number: 2, createdAt: '2026-07-02T09:00:00Z' })
    const result = sortPrs([older, newer], 'createdAt', 'desc')
    expect(result[0].number).toBe(2)
  })

  it('sorts by reviewCount ascending', () => {
    const a = pr({ number: 1, reviewCount: 3 })
    const b = pr({ number: 2, reviewCount: 1 })
    const result = sortPrs([a, b], 'reviewCount', 'asc')
    expect(result[0].number).toBe(2)
  })
})

describe('sortCommitSummaries', () => {
  it('sorts by commitCount descending', () => {
    const a: GitHubCommitSummary = { repo: 'a', commitCount: 2, additions: 0, deletions: 0 }
    const b: GitHubCommitSummary = { repo: 'b', commitCount: 5, additions: 0, deletions: 0 }
    const result = sortCommitSummaries([a, b], 'commitCount', 'desc')
    expect(result[0].repo).toBe('b')
  })
})

describe('sortCiSummaries', () => {
  it('sorts by successRate ascending, surfacing the worst repo first', () => {
    const a: GitHubCiSummary = { repo: 'a', workflowName: 'ci', totalRuns: 10, failedRuns: 1, successRate: 90 }
    const b: GitHubCiSummary = { repo: 'b', workflowName: 'ci', totalRuns: 10, failedRuns: 5, successRate: 50 }
    const result = sortCiSummaries([a, b], 'successRate', 'asc')
    expect(result[0].repo).toBe('b')
  })
})
```

- [ ] **Step 2: Run to verify failure**

Run: `npx vitest run src/components/githubSort.test.ts` (from `src/AiObservatory.Web`)
Expected: FAIL — `./githubSort` does not exist.

- [ ] **Step 3: Implement `githubSort.ts`**

`src/AiObservatory.Web/src/components/githubSort.ts`:
```typescript
import type { GitHubPr, GitHubCommitSummary, GitHubCiSummary } from '../api/client'

export type SortDirection = 'asc' | 'desc'

export type PrSortField = 'repo' | 'createdAt' | 'reviewCount' | 'turnaroundHours'

export function filterPrs(prs: GitHubPr[], query: string): GitHubPr[] {
  const q = query.trim().toLowerCase()
  if (!q) return prs
  return prs.filter((p) => p.title.toLowerCase().includes(q) || p.repo.toLowerCase().includes(q))
}

export function sortPrs(prs: GitHubPr[], field: PrSortField, direction: SortDirection): GitHubPr[] {
  return prs.toSorted((a, b) => {
    let comparison: number
    if (field === 'repo') comparison = a.repo.localeCompare(b.repo)
    else if (field === 'createdAt') comparison = a.createdAt.localeCompare(b.createdAt)
    else if (field === 'reviewCount') comparison = a.reviewCount - b.reviewCount
    else comparison = (a.turnaroundHours ?? -1) - (b.turnaroundHours ?? -1)
    return direction === 'asc' ? comparison : -comparison
  })
}

export type CommitSortField = 'repo' | 'commitCount'

export function sortCommitSummaries(
  summaries: GitHubCommitSummary[], field: CommitSortField, direction: SortDirection,
): GitHubCommitSummary[] {
  return summaries.toSorted((a, b) => {
    const comparison = field === 'repo' ? a.repo.localeCompare(b.repo) : a.commitCount - b.commitCount
    return direction === 'asc' ? comparison : -comparison
  })
}

export type CiSortField = 'repo' | 'totalRuns' | 'successRate'

export function sortCiSummaries(
  summaries: GitHubCiSummary[], field: CiSortField, direction: SortDirection,
): GitHubCiSummary[] {
  return summaries.toSorted((a, b) => {
    let comparison: number
    if (field === 'repo') comparison = a.repo.localeCompare(b.repo)
    else if (field === 'totalRuns') comparison = a.totalRuns - b.totalRuns
    else comparison = a.successRate - b.successRate
    return direction === 'asc' ? comparison : -comparison
  })
}
```

- [ ] **Step 4: Run to verify pass**

Run: `npx vitest run src/components/githubSort.test.ts` (from `src/AiObservatory.Web`)
Expected: PASS, 6/6.

- [ ] **Step 5: Commit**

```
git add src/AiObservatory.Web/src/components/githubSort.ts src/AiObservatory.Web/src/components/githubSort.test.ts
git commit -m "feat(web): GitHub PR/commit/CI sort and filter helpers"
```

---

### Task 15: `GitHubPage.tsx` and its three tables

**Files:**
- Create: `src/AiObservatory.Web/src/pages/GitHubPage.tsx`
- Create: `src/AiObservatory.Web/src/components/GitHubPrTable.tsx`
- Create: `src/AiObservatory.Web/src/components/GitHubCommitTable.tsx`
- Create: `src/AiObservatory.Web/src/components/GitHubCiTable.tsx`

**Interfaces:**
- Consumes: `useGitHubPrs`, `useGitHubCommitSummary`, `useGitHubCi` (Task 13); `filterPrs`, `sortPrs`, `sortCommitSummaries`, `sortCiSummaries` (Task 14); `useDateRange` (existing, same as `ActivityPage.tsx`); `DateRangePicker` (existing); `SearchIcon` (existing).
- Produces: `GitHubPage` default export — consumed by Task 16 (`Dashboard.tsx`).

- [ ] **Step 1: Write `GitHubPrTable.tsx`**

`src/AiObservatory.Web/src/components/GitHubPrTable.tsx`:
```tsx
import { useState, useMemo } from 'react'
import type { GitHubPr } from '../api/client'
import { filterPrs, sortPrs } from './githubSort'
import type { PrSortField, SortDirection } from './githubSort'
import SearchIcon from '../design/SearchIcon'

interface SortableHeaderProps {
  field: PrSortField
  label: string
  sortField: PrSortField
  sortDirection: SortDirection
  onSort: (field: PrSortField) => void
}

const SortableHeader = ({ field, label, sortField, sortDirection, onSort }: SortableHeaderProps) => {
  const isActive = sortField === field
  let ariaSort: 'ascending' | 'descending' | 'none' = 'none'
  if (isActive) ariaSort = sortDirection === 'asc' ? 'ascending' : 'descending'
  let indicatorSymbol = '↕'
  if (isActive) indicatorSymbol = sortDirection === 'asc' ? '▲' : '▼'

  return (
    <th className="sortable-header" aria-sort={ariaSort}>
      <button type="button" className="sortable-header__content" onClick={() => onSort(field)}>
        {label}
        <span className={`sort-indicator ${isActive ? 'sort-indicator--active' : ''}`} aria-hidden="true">
          {indicatorSymbol}
        </span>
      </button>
    </th>
  )
}

export default function GitHubPrTable({ prs }: { prs: GitHubPr[] }) {
  const [searchQuery, setSearchQuery] = useState('')
  const [sortField, setSortField] = useState<PrSortField>('createdAt')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const visible = useMemo(
    () => sortPrs(filterPrs(prs, searchQuery), sortField, sortDirection),
    [prs, searchQuery, sortField, sortDirection],
  )

  if (prs.length === 0) return <p className="panel-empty">No PR activity for this period.</p>

  const handleSort = (field: PrSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('desc') }
  }

  return (
    <>
      <div className="breakdown-controls">
        <div className="breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search PRs..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="breakdown-search"
            aria-label="Search pull requests"
          />
        </div>
      </div>
      {visible.length === 0 ? (
        <p className="panel-empty">No matching PRs found.</p>
      ) : (
        <table className="model-table">
          <thead>
            <tr>
              <SortableHeader field="repo" label="Repo" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <th>Title</th>
              <th>Author</th>
              <th>State</th>
              <SortableHeader field="createdAt" label="Created" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="reviewCount" label="Reviews" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="turnaroundHours" label="Turnaround" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            </tr>
          </thead>
          <tbody>
            {visible.map((p) => (
              <tr key={`${p.repo}#${p.number}`}>
                <td>{p.repo}</td>
                <td>#{p.number} {p.title}</td>
                <td>{p.author}</td>
                <td>{p.state}</td>
                <td>{new Date(p.createdAt).toLocaleDateString()}</td>
                <td>{p.reviewCount}</td>
                <td>{p.turnaroundHours == null ? '—' : `${p.turnaroundHours}h`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
```

- [ ] **Step 2: Write `GitHubCommitTable.tsx`**

`src/AiObservatory.Web/src/components/GitHubCommitTable.tsx`:
```tsx
import { useState, useMemo } from 'react'
import type { GitHubCommitSummary } from '../api/client'
import { sortCommitSummaries } from './githubSort'
import type { CommitSortField, SortDirection } from './githubSort'

export default function GitHubCommitTable({ summary }: { summary: GitHubCommitSummary[] }) {
  const [sortField, setSortField] = useState<CommitSortField>('commitCount')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const visible = useMemo(
    () => sortCommitSummaries(summary, sortField, sortDirection),
    [summary, sortField, sortDirection],
  )

  if (summary.length === 0) return <p className="panel-empty">No commit activity for this period.</p>

  const handleSort = (field: CommitSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('desc') }
  }

  return (
    <table className="project-table">
      <thead>
        <tr>
          <th>
            <button type="button" onClick={() => handleSort('repo')}>Repo</button>
          </th>
          <th>
            <button type="button" onClick={() => handleSort('commitCount')}>Commits</button>
          </th>
          <th>Churn</th>
        </tr>
      </thead>
      <tbody>
        {visible.map((s) => (
          <tr key={s.repo}>
            <td>{s.repo}</td>
            <td>{s.commitCount}</td>
            <td>+{s.additions} / -{s.deletions}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
```

- [ ] **Step 3: Write `GitHubCiTable.tsx`**

`src/AiObservatory.Web/src/components/GitHubCiTable.tsx`:
```tsx
import { useState, useMemo } from 'react'
import type { GitHubCiSummary } from '../api/client'
import { sortCiSummaries } from './githubSort'
import type { CiSortField, SortDirection } from './githubSort'

const SUCCESS_RATE_WARN_THRESHOLD = 80

export default function GitHubCiTable({ ci }: { ci: GitHubCiSummary[] }) {
  const [sortField, setSortField] = useState<CiSortField>('successRate')
  const [sortDirection, setSortDirection] = useState<SortDirection>('asc')

  const visible = useMemo(
    () => sortCiSummaries(ci, sortField, sortDirection),
    [ci, sortField, sortDirection],
  )

  if (ci.length === 0) return <p className="panel-empty">No CI activity for this period.</p>

  const handleSort = (field: CiSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('asc') }
  }

  return (
    <table className="project-table">
      <thead>
        <tr>
          <th><button type="button" onClick={() => handleSort('repo')}>Repo</button></th>
          <th>Workflow</th>
          <th><button type="button" onClick={() => handleSort('totalRuns')}>Runs</button></th>
          <th>Failed</th>
          <th><button type="button" onClick={() => handleSort('successRate')}>Success rate</button></th>
        </tr>
      </thead>
      <tbody>
        {visible.map((c) => (
          <tr key={`${c.repo}:${c.workflowName}`}>
            <td>{c.repo}</td>
            <td>{c.workflowName}</td>
            <td>{c.totalRuns}</td>
            <td>{c.failedRuns}</td>
            <td style={{ color: c.successRate < SUCCESS_RATE_WARN_THRESHOLD ? 'var(--danger, #d33)' : undefined }}>
              {c.successRate.toFixed(0)}%
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
```

- [ ] **Step 4: Write `GitHubPage.tsx`**

`src/AiObservatory.Web/src/pages/GitHubPage.tsx`:
```tsx
import GitHubPrTable from '../components/GitHubPrTable'
import GitHubCommitTable from '../components/GitHubCommitTable'
import GitHubCiTable from '../components/GitHubCiTable'
import DateRangePicker from '../components/DateRangePicker'
import { useDateRange } from '../lib/dateRange'
import { useGitHubPrs, useGitHubCommitSummary, useGitHubCi, localDate } from '../api/queries'

export default function GitHubPage() {
  const { from, to, preset, setPreset, setCustom } = useDateRange()
  const { prs, isError: prsError } = useGitHubPrs(from, to)
  const { summary, isError: summaryError } = useGitHubCommitSummary(from, to)
  const { ci, isError: ciError } = useGitHubCi(from, to)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`
  const isError = prsError || summaryError || ciError

  return (
    <div className="reporting-page">
      <div className="reporting-range-bar">
        <DateRangePicker from={from} to={to} preset={preset} onPreset={setPreset} onCustom={setCustom} />
        <span className="reporting-range-label">{rangeLabel}</span>
      </div>
      {isError && (
        <div className="error-banner" role="alert">
          Couldn’t load GitHub activity data. It may be unavailable or you may not be authorised — try refreshing.
        </div>
      )}
      <div className="panel">
        <div className="panel-title">Pull requests — {rangeLabel}</div>
        <GitHubPrTable prs={prs} />
      </div>
      <div className="main-grid">
        <div className="panel">
          <div className="panel-title">Commits by repo</div>
          <GitHubCommitTable summary={summary} />
        </div>
        <div className="panel">
          <div className="panel-title">CI health</div>
          <GitHubCiTable ci={ci} />
        </div>
      </div>
    </div>
  )
}
```

- [ ] **Step 5: Typecheck**

Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web`)
Expected: no errors.

- [ ] **Step 6: Commit**

```
git add src/AiObservatory.Web/src/pages/GitHubPage.tsx src/AiObservatory.Web/src/components/GitHubPrTable.tsx src/AiObservatory.Web/src/components/GitHubCommitTable.tsx src/AiObservatory.Web/src/components/GitHubCiTable.tsx
git commit -m "feat(web): GitHubPage with PR, commit, and CI tables"
```

---

### Task 16: Wire the "GitHub" tab into `Dashboard.tsx`

**Files:**
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx`

**Interfaces:**
- Consumes: `GitHubPage` (Task 15).

- [ ] **Step 1: Add the tab**

In `src/AiObservatory.Web/src/pages/Dashboard.tsx`:

1. Add the import: `import GitHubPage from './GitHubPage'`
2. Widen the tab union: `type DashboardTab = 'overview' | 'adversarial-review' | 'reporting' | 'activity' | 'github'`
3. Add to `TABS`: `{ id: 'github', label: 'GitHub', readonlyHidden: true },`
4. Add the render branch after `{tab === 'activity' && <ActivityPage />}`:
   ```tsx
   {tab === 'github' && <GitHubPage />}
   ```

- [ ] **Step 2: Typecheck and lint**

Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web`)
Expected: no errors.

Run: `npx eslint .` (from `src/AiObservatory.Web`)
Expected: no errors (warnings acceptable per this repo's CI gate).

- [ ] **Step 3: Commit**

```
git add src/AiObservatory.Web/src/pages/Dashboard.tsx
git commit -m "feat(web): add GitHub tab to dashboard nav"
```

---

### Task 17: Frontend component tests

**Files:**
- Create: `src/AiObservatory.Web/src/components/GitHubPrTable.test.tsx`
- Create: `src/AiObservatory.Web/src/components/GitHubCiTable.test.tsx`

**Interfaces:**
- Consumes: `GitHubPrTable` (Task 15), `GitHubCiTable` (Task 15).

- [ ] **Step 1: Write the failing tests**

`src/AiObservatory.Web/src/components/GitHubPrTable.test.tsx`:
```tsx
import { describe, expect, it } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import GitHubPrTable from './GitHubPrTable'
import type { GitHubPr } from '../api/client'

const prs: GitHubPr[] = [
  { repo: 'fix-portal/a', number: 1, title: 'Add feature', author: 'chris', state: 'open', createdAt: '2026-07-01T09:00:00Z', mergedAt: null, reviewCount: 2, turnaroundHours: 3.5 },
  { repo: 'fix-portal/b', number: 2, title: 'Fix bug', author: 'chris', state: 'merged', createdAt: '2026-07-02T09:00:00Z', mergedAt: '2026-07-02T12:00:00Z', reviewCount: 0, turnaroundHours: null },
]

describe('GitHubPrTable', () => {
  it('renders every PR row', () => {
    render(<GitHubPrTable prs={prs} />)
    expect(screen.getByText(/Add feature/)).toBeInTheDocument()
    expect(screen.getByText(/Fix bug/)).toBeInTheDocument()
  })

  it('filters by search query', () => {
    render(<GitHubPrTable prs={prs} />)
    fireEvent.change(screen.getByLabelText('Search pull requests'), { target: { value: 'bug' } })
    expect(screen.queryByText(/Add feature/)).not.toBeInTheDocument()
    expect(screen.getByText(/Fix bug/)).toBeInTheDocument()
  })

  it('shows an empty state when there is no activity', () => {
    render(<GitHubPrTable prs={[]} />)
    expect(screen.getByText('No PR activity for this period.')).toBeInTheDocument()
  })

  it('renders em dash for a PR with no turnaround yet', () => {
    render(<GitHubPrTable prs={prs} />)
    expect(screen.getByText('—')).toBeInTheDocument()
  })
})
```

`src/AiObservatory.Web/src/components/GitHubCiTable.test.tsx`:
```tsx
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import GitHubCiTable from './GitHubCiTable'
import type { GitHubCiSummary } from '../api/client'

const ci: GitHubCiSummary[] = [
  { repo: 'fix-portal/a', workflowName: 'ci.yml', totalRuns: 10, failedRuns: 3, successRate: 70 },
  { repo: 'fix-portal/b', workflowName: 'ci.yml', totalRuns: 10, failedRuns: 0, successRate: 100 },
]

describe('GitHubCiTable', () => {
  it('sorts by success rate ascending by default, worst repo first', () => {
    render(<GitHubCiTable ci={ci} />)
    const rows = screen.getAllByRole('row').slice(1) // skip header row
    expect(rows[0]).toHaveTextContent('fix-portal/a')
  })

  it('shows an empty state when there is no CI activity', () => {
    render(<GitHubCiTable ci={[]} />)
    expect(screen.getByText('No CI activity for this period.')).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: Run to verify they pass**

(These are written against the already-implemented Task 15 components, so this is a verification run, not a red-then-green cycle.)

Run: `npx vitest run src/components/GitHubPrTable.test.tsx src/components/GitHubCiTable.test.tsx` (from `src/AiObservatory.Web`)
Expected: PASS, 6/6.

- [ ] **Step 3: Full frontend check suite**

Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web`)
Expected: no errors.

Run: `npx eslint .` (from `src/AiObservatory.Web`)
Expected: no errors.

Run: `npx vitest run` (from `src/AiObservatory.Web`)
Expected: full suite PASS.

- [ ] **Step 4: Commit**

```
git add src/AiObservatory.Web/src/components/GitHubPrTable.test.tsx src/AiObservatory.Web/src/components/GitHubCiTable.test.tsx
git commit -m "test(web): cover GitHubPrTable and GitHubCiTable"
```

---

### Task 18: Full backend check + docs config note

**Files:**
- Modify: `docker-compose.yml` (only if the seed/demo service needs the new `GITHUB_TOKEN`/allowlist env vars documented — check first)

**Interfaces:**
- None new — this task is verification-only plus an optional docs touch-up.

- [ ] **Step 1: Full backend test run**

Run: `dotnet test` (from repo root)
Expected: all test projects PASS (note: `AiObservatory.Data.Tests` requires local Postgres on `127.0.0.1:5432` per this repo's known constraint — skip/expect-fail if unavailable, do not treat as a regression).

- [ ] **Step 2: Full backend build**

Run: `dotnet build` (from repo root)
Expected: Build succeeded, 0 errors, 0 new warnings.

- [ ] **Step 3: Check `docker-compose.yml` for whether it documents env vars for the other providers (e.g. `GITHUB_TOKEN`, `COPILOT_ORG`)**

Read `docker-compose.yml`. If it lists example env vars for the Ingest service (commented out or with placeholder values) for the existing providers, add equivalent commented-out lines for `GITHUB_TOKEN` (if not already present for Copilot) and `Ingest__GitHubRepoAllowlist__0=fix-portal/example-repo` so a self-hoster discovers the new option the same way they'd discover the others. If the file doesn't document per-provider env vars at all, skip this step — don't introduce a new documentation convention unprompted.

- [ ] **Step 4: Commit (only if Step 3 made a change)**

```
git add docker-compose.yml
git commit -m "docs: document GitHubRepoAllowlist env var in docker-compose"
```

---

## Self-Review Notes

- **Spec coverage:** every spec section has a task — capture/client (Tasks 6-9), data model (Tasks 1-2), repository (Task 3-4), API (Task 11), frontend (Tasks 12-17), testing (interleaved throughout), error handling / rate limiting (Task 6, Task 9), backfill (Task 9).
- **Placeholder scan:** no TBD/TODO markers; every code step has complete, runnable code.
- **Type consistency:** `GitHubPullRequestRecord`/`GitHubCommitRecord`/`GitHubWorkflowRunRecord` (Task 3) are used identically by the client (Tasks 6-8), the ingestion service (Task 9), and the repository tests (Task 4) — same field names and order throughout. `IGitHubActivityClient`'s three method signatures (Task 6) match what `GitHubIngestionService` calls (Task 9). Frontend `GitHubPr`/`GitHubCommitSummary`/`GitHubCiSummary` (Task 12) field names match the API response records (Task 11) exactly (camelCase via the API's existing `JsonNamingPolicy.CamelCase` enum/property convention already configured in `Program.cs`).
