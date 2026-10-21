# PostgreSQL-Free Tests and Complexity Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make PostgreSQL-backed tests explicitly selectable, clear every actionable build warning, and prevent unsupported TypeScript 7 Dependabot PRs.

**Architecture:** Preserve every public route and service contract. Move complex inline endpoint lambdas into named handlers, extract only cohesive validation/conversion helpers, and suppress S3776 at the two deliberately linear methods where extraction would reduce clarity.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core/Npgsql, NodaTime, xUnit v3, React/TypeScript, Dependabot.

## Global Constraints

- Preserve API results, EF Core query shapes, transactions, concurrency handling, cancellation, and logging.
- Do not add packages, projects, interfaces, migrations, or speculative abstractions.
- Keep PostgreSQL-backed tests out of the ordinary unit lane with `Category=Integration`.
- Keep the global S3776 threshold at 15.
- Ship all changes in the existing single ready pull request.

---

### Task 1: Preserve a PostgreSQL-free unit lane

**Files:**
- Modify: `tests/AiObservatory.Api.Tests/*.cs`
- Modify: `tests/AiObservatory.Data.Tests/Repositories/*.cs`
- Modify: `README.md`

**Interfaces:**
- Produces: `Category=Integration` discovery for every PostgreSQL-backed test.

- [ ] **Step 1: Verify the original failing discovery check**

```powershell
dotnet test AiObservatory.slnx --list-tests --filter "Category=Integration" --no-restore
```

Expected before the classification edits: no matching tests.

- [ ] **Step 2: Add the minimum traits and documentation**

Add `[Trait("Category", "Integration")]` to database-backed classes, except
the mixed `StartupGuardsTests`, where only database-reaching methods receive it.
Document the separate `Category!=Integration` and `Category=Integration` commands.

- [ ] **Step 3: Verify both lanes**

```powershell
dotnet test AiObservatory.slnx --filter "Category!=Integration" --no-restore
```

Expected: 167 passing unit tests without PostgreSQL.

```powershell
dotnet test AiObservatory.slnx --list-tests --filter "Category=Integration" --no-build --no-restore
```

Expected: 58 discovered integration tests.

### Task 2: Extract endpoint handlers without changing behaviour

**Files:**
- Modify: `src/AiObservatory.Api/Endpoints/ActivityEndpoints.cs`
- Modify: `src/AiObservatory.Api/Endpoints/AggregatesEndpoints.cs`
- Modify: `src/AiObservatory.Api/Endpoints/CavemanEndpoints.cs`
- Modify: `src/AiObservatory.Api/Endpoints/EventsEndpoints.cs`
- Modify: `src/AiObservatory.Api/Endpoints/SubscriptionsEndpoints.cs`
- Test: existing corresponding `*WafTests.cs` and `ActivityEndpointDataTests.cs`

**Interfaces:**
- Consumes: the existing request records, `AiObservatoryDbContext`, repositories, and `IClock`.
- Produces: the same route templates and HTTP results through named private handlers.

- [ ] **Step 1: Record the analyzer failure**

```powershell
dotnet build AiObservatory.slnx --configuration Release --no-incremental --no-restore
```

Expected: S3776 on all five endpoint registration methods.

- [ ] **Step 2: Implement the minimum extraction**

Replace complex route lambdas with named method groups such as
`UpsertSessionsAsync`, `GetAggregatesAsync`, `RecordEventAsync`, and
`CreateSubscriptionAsync`. Extract `ValidateSessions` and
`ValidateSubscription` only where their logic is cohesive or duplicated.

- [ ] **Step 3: Run the endpoint safety net**

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "Category!=Integration"
```

Expected: all non-database API tests pass.

### Task 3: Simplify service and client control flow

**Files:**
- Modify: `src/AiObservatory.Api/ApiKeyEndpointFilter.cs`
- Modify: `src/AiObservatory.Api/Services/BudgetAlertService.cs`
- Modify: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicUsageClient.cs`
- Modify: `src/AiObservatory.Ingest/Services/GitHub/GitHubActivityClient.cs`
- Modify: `src/AiObservatory.Api/Services/AdversarialReviewService.cs`
- Modify: `src/AiObservatory.Ingest/Services/GitHub/GitHubIngestionService.cs`
- Test: existing matching service/client tests.

**Interfaces:**
- Produces: unchanged authentication decisions, alert retry behaviour, pagination, pricing, and ingestion orchestration.

- [ ] **Step 1: Extract natural decisions and transformations**

Split read/admin API-key decisions; split budget-window and per-rule processing;
split Anthropic bucket conversion; and split GitHub pull-request conversion.

- [ ] **Step 2: Suppress only the metric false positives**

Wrap `RecordRunAsync` and `IngestSinceAsync` individually with documented
`#pragma warning disable S3776` / `restore` pairs. Do not alter the global rule.

- [ ] **Step 3: Run focused tests**

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "Category!=Integration"
```

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj
```

Expected: all tests pass.

### Task 4: Prevent unsupported TypeScript major updates

**Files:**
- Modify: `.github/dependabot.yml`

**Interfaces:**
- Produces: npm major updates for `typescript` are ignored; all other npm updates remain enabled.

- [ ] **Step 1: Add the established ignore entry**

```yaml
    ignore:
      - dependency-name: typescript
        update-types: ["version-update:semver-major"]
```

Include the established unblock condition from `fixportal-learning`: remove it
when TypeScript 7.1 restores the compiler API and `typescript-eslint` supports it.

- [ ] **Step 2: Close the incompatible Dependabot PR**

```powershell
gh pr close 85 --comment "Closing because TypeScript 7.0 removes the compiler API required by typescript-eslint. Dependabot major updates are ignored until the lint toolchain supports TypeScript 7."
```

Expected: PR #85 is closed.

### Task 5: Verify and publish

**Files:** all changed files.

- [ ] **Step 1: Verify .NET formatting, tests, and warnings**

```powershell
dotnet format whitespace AiObservatory.slnx --verify-no-changes --no-restore
```

```powershell
dotnet format style AiObservatory.slnx --verify-no-changes --no-restore
```

```powershell
dotnet test AiObservatory.slnx --configuration Release --filter "Category!=Integration"
```

```powershell
dotnet build AiObservatory.slnx --configuration Release --no-incremental --no-restore
```

Expected: exit zero, all unit tests pass, and zero warnings.

- [ ] **Step 2: Verify the frontend**

```powershell
npm --prefix src/AiObservatory.Web run lint
```

```powershell
npm --prefix src/AiObservatory.Web test -- --run
```

```powershell
npm --prefix src/AiObservatory.Web run build
```

Expected: all commands pass without changing the lock file.

- [ ] **Step 3: Commit and publish one ready PR**

Stage only the scoped files, push `test/classify-postgres-integration`, open a
ready PR, and add the exact comment `Gitar review`.
