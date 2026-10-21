# AI Observatory Scaffold Normalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Normalize AI Observatory against the FixPortal .NET, test, frontend, EF Core, and CI scaffolds in one reviewable pull request.

**Architecture:** Preserve the existing application and project boundaries. Centralize dependency policy, reuse the shared architecture-rule package, repair the existing automation in place, and make coverage measure the complete frontend source tree.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core 10/Npgsql, xUnit v3, AwesomeAssertions, NSubstitute, React 19, TypeScript 6, Vite 8, Vitest 4, GitHub Actions, Stryker.NET.

## Global Constraints

- Preserve every existing project name and the current `src/` and `tests/` layout.
- Keep xUnit v3, NSubstitute, and AwesomeAssertions; do not add NUnit, Moq, or FluentAssertions.
- Do not retrofit XML comments across existing tests.
- Keep NodaTime domain types and the existing PostgreSQL EF Core mappings; create no migration.
- Preserve `.gitignore`, historical documents, deployment targets, React Doctor, coverage reporting, and CodeQL default setup.
- Do not perform a feature-first frontend migration, cognitive-complexity refactor, or Blacksmith runner migration.
- Keep mutation score informational with `thresholds.break: 0`, but allow configuration, restore, build, and runner failures to fail the workflow.
- Ship one ready pull request and request review with the exact comment `Gitar review`.

---

### Task 1: Normalize .NET dependencies and architecture rules

**Files:**
- Modify: `Directory.Build.props`
- Modify: `Directory.Packages.props`
- Modify: `AiObservatory.slnx`
- Modify: `src/AiObservatory.Api/AiObservatory.Api.csproj`
- Modify: `src/AiObservatory.Data/AiObservatory.Data.csproj`
- Modify: `src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`
- Modify: `tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj`
- Modify: `tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj`
- Modify: `tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj`
- Modify: `tests/AiObservatory.Api.Tests/ArchitectureTests.cs`
- Delete: `tests/AiObservatory.Api.Tests/ArchRules.cs`
- Format: `src/AiObservatory.Api/Program.cs`
- Format: `src/AiObservatory.Api/Services/Intelligence/IntelligenceWorkerService.cs`
- Format: `src/AiObservatory.Ingest/ProviderPollingWorkerService.cs`
- Format: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicIngestionService.cs`
- Format: `src/AiObservatory.Ingest/Services/Google/GoogleBillingClient.cs`
- Format: `src/AiObservatory.Ingest/Services/Google/GoogleIngestionService.cs`
- Format: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiIngestionService.cs`
- Format: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiUsageClient.cs`
- Format: `tests/AiObservatory.Api.Tests/AdminOnlyApiKeyEndpointFilterTests.cs`
- Format: `tests/AiObservatory.Api.Tests/ArchitectureTests.cs`
- Format: `tests/AiObservatory.Api.Tests/SubscriptionsEndpointsWafTests.cs`
- Format: `tests/AiObservatory.Data.Tests/Repositories/AdversarialReviewRepositoryTests.cs`
- Format: `tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs`
- Modify: `src/AiObservatory.Ingest/Services/Anthropic/AnthropicIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/Services/Google/GoogleIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/Services/OpenAi/OpenAiIngestionService.cs`
- Modify: `src/AiObservatory.Ingest/Services/GitHub/GitHubIngestionService.cs`

**Interfaces:**
- Consumes: existing central package management and `FixPortalArchRules` calls.
- Produces: a versionless private-assets `FixPortal.CodeStyle` reference for every project and the shared `FixPortal.CodeStyle.ArchRules.FixPortalArchRules` API for architecture tests.

- [ ] **Step 1: Move the code-style reference to the common build file**

Add to `Directory.Build.props`:

```xml
  <ItemGroup>
    <PackageReference Include="FixPortal.CodeStyle" PrivateAssets="all" />
  </ItemGroup>
```

In `Directory.Packages.props`, enable transitive pinning and replace the
`GlobalPackageReference` with central versions:

```xml
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="FixPortal.CodeStyle" Version="0.1.9" />
    <PackageVersion Include="FixPortal.CodeStyle.ArchRules" Version="0.1.9" />
    <PackageVersion Include="System.Security.Cryptography.Xml" Version="10.0.10" />
    <PackageVersion Include="Microsoft.OpenApi" Version="2.7.5" />
```

- [ ] **Step 2: Update NuGet versions and remove unused OpenAPI references**

Set these central versions:

```xml
<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
<PackageVersion Include="NSubstitute" Version="6.0.0" />
<PackageVersion Include="AwesomeAssertions" Version="9.5.0" />
<PackageVersion Include="NodaTime" Version="3.3.3" />
<PackageVersion Include="NodaTime.Testing" Version="3.3.3" />
<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.10" />
<PackageVersion Include="Microsoft.AspNetCore.TestHost" Version="10.0.10" />
<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.10" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.10" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime" Version="10.0.3" />
<PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.10" />
<PackageVersion Include="Microsoft.Identity.Web" Version="4.13.2" />
<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.10" />
<PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.10" />
<PackageVersion Include="Microsoft.Extensions.Options.DataAnnotations" Version="10.0.10" />
```

Keep `Microsoft.OpenApi` 2.7.5 as a central transitive security pin, but delete
its unused direct project references. Remove `VersionOverride` from the two
`Microsoft.EntityFrameworkCore.Relational` references so central package
management owns the version.

- [ ] **Step 3: Consume the shared architecture-rule package**

Add this package reference to `AiObservatory.Api.Tests.csproj`:

```xml
<PackageReference Include="FixPortal.CodeStyle.ArchRules" />
```

Add this import to `ArchitectureTests.cs`:

```csharp
using FixPortal.CodeStyle.ArchRules;
```

Delete `tests/AiObservatory.Api.Tests/ArchRules.cs`.

- [ ] **Step 4: Add solution configuration and solution items**

Add `Any CPU` configuration and a `/Solution Items/` folder to
`AiObservatory.slnx`. Include `.config/dotnet-tools.json`, `.editorconfig`, all
six existing workflow files, `.github/dependabot.yml`, `.gitignore`,
`Directory.Build.props`, `Directory.Packages.props`, `nuget.config`, `README.md`,
`scripts/summarize-stryker.ps1`, and `stryker-config.json`.

- [ ] **Step 5: Apply bounded formatter and low-risk analyzer fixes**

Run:

```powershell
dotnet format AiObservatory.slnx --no-restore
```

Retain only formatter/import changes identified by the baseline. Replace the
three `groups.Count()` calls with `groups.Count`. Change the rate-limit handler
to preserve its exception:

```csharp
catch (GitHubRateLimitExceededException ex)
{
    logger.LogWarning(ex, "GitHub: aborting remaining repos this poll cycle due to rate limit");
    return failedRepoCount;
}
```

Do not refactor S3776 findings or silence the deliberate local-development
connection/URI warnings.

- [ ] **Step 6: Verify the .NET normalization**

Run:

```powershell
dotnet restore AiObservatory.slnx
dotnet build AiObservatory.slnx --configuration Release --no-restore
dotnet format AiObservatory.slnx --verify-no-changes --no-restore
dotnet list AiObservatory.slnx package --vulnerable --include-transitive
dotnet list AiObservatory.slnx package --outdated
```

Expected: restore/build succeed; format reports no changes; no vulnerable or
outdated top-level packages remain. Existing advisory S3776/S1075/S2068/S1118
warnings may remain.

- [ ] **Step 7: Commit the .NET normalization**

```powershell
git add Directory.Build.props Directory.Packages.props AiObservatory.slnx src tests
git commit -m "chore: normalize observatory .NET scaffolding"
```

---

### Task 2: Normalize frontend dependencies and coverage

**Files:**
- Modify: `src/AiObservatory.Web/package.json`
- Modify: `src/AiObservatory.Web/package-lock.json`
- Modify: `src/AiObservatory.Web/vitest.config.ts`
- Modify: `src/AiObservatory.Web/src/architecture.spec.ts`
- Modify: `src/AiObservatory.Web/Dockerfile`
- Modify: `README.md`

**Interfaces:**
- Consumes: existing Vite, Vitest, ESLint, ArchUnitTS, and React application entry points.
- Produces: Node 24-compatible current dependencies and an all-source Vitest coverage denominator.

- [ ] **Step 1: Upgrade runtime dependencies**

Run in `src/AiObservatory.Web`:

```powershell
npm install @azure/msal-browser@5.17.1 @azure/msal-react@5.5.3 @fontsource/ibm-plex-mono@5.3.0 @fontsource/ibm-plex-sans@5.3.0 @tanstack/react-query@5.101.3 react@19.2.7 react-dom@19.2.7 react-markdown@10.1.0 recharts@3.10.0 rehype-sanitize@6.0.0
```

- [ ] **Step 2: Upgrade development dependencies**

Run in `src/AiObservatory.Web`:

```powershell
npm install --save-dev @eslint/js@10.0.1 @testing-library/jest-dom@7.0.0 @testing-library/react@16.3.2 @testing-library/user-event@14.6.1 @types/node@26.1.1 @types/react@19.2.17 @types/react-dom@19.2.3 @vitejs/plugin-react@6.0.3 @vitest/coverage-v8@4.1.10 eslint@10.7.0 eslint-plugin-react-hooks@7.1.1 eslint-plugin-react-refresh@0.5.3 eslint-plugin-sonarjs@4.2.0 globals@17.7.0 jsdom@29.1.1 react-doctor@0.8.3 rollup-plugin-visualizer@7.0.1 typescript@6.0.3 typescript-eslint@8.65.0 vite@8.1.5 vitest@4.1.10
```

Then pin ArchUnitTS exactly:

```powershell
npm install --save-dev --save-exact archunit@2.3.3
```

- [ ] **Step 3: Make coverage non-vacuous and stable**

Add to the Vitest coverage block:

```ts
include: ['src/**'],
```

Set thresholds to:

```ts
thresholds: {
  statements: 25,
  branches: 19,
  functions: 19,
  lines: 25,
},
```

Pass `15_000` as the timeout argument to the two ArchUnitTS `it` call sites so
coverage instrumentation cannot trip the default five-second timeout.

- [ ] **Step 4: Align Node documentation and container build**

Change the frontend Docker build stage to `node:24-alpine`. Change both README
prerequisite references from Node 22+ to Node 24+.

- [ ] **Step 5: Verify frontend behavior and dependency health**

Run in `src/AiObservatory.Web`:

```powershell
npm audit --audit-level=high
npm outdated
npm run lint
npm test
npm test -- --coverage
npm run build
```

Expected: audit has no high/critical advisory, outdated reports no direct drift,
lint/build pass, at least 123 tests pass, and all-source coverage clears the
25/19/19/25 floors.

- [ ] **Step 6: Commit frontend normalization**

```powershell
git add README.md src/AiObservatory.Web
git commit -m "chore(web): align frontend scaffold"
```

---

### Task 3: Repair and align CI automation

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/coverage.yml`
- Modify: `.github/workflows/deploy.yml`
- Modify: `.github/workflows/infra.yml`
- Modify: `.github/workflows/mutation.yml`
- Modify: `.github/workflows/react-doctor.yml`
- Modify: `.github/dependabot.yml`
- Modify: `stryker-config.json`

**Interfaces:**
- Consumes: the existing GitHub Actions deployment gates, PostgreSQL test contract, and Stryker configuration.
- Produces: explicitly named jobs, per-job actionlint, Node 24 builds, npm dependency updates, and a mutation workflow whose status reflects reality.

- [ ] **Step 1: Normalize CI job names and validation**

Set explicit names:

```yaml
backend: Backend (.NET)
build-web: Frontend (UI)
coverage: Coverage (.NET, advisory)
mutation: Mutation (Stryker.NET API pilot)
react-doctor: React Doctor
```

Immediately after checkout in every job, add:

```yaml
- name: Lint workflows (actionlint)
  uses: raven-actions/actionlint@3d39aea434753780c3b3d4a1a31c854b4dbf49d7 # v2
  with:
    shellcheck: true
```

Use Node 24 in both `ci.yml` and `deploy.yml`. Preserve all trusted-main deploy
conditions, third-party SHA pins, and `cancel-in-progress: false` settings.

- [ ] **Step 2: Repair mutation execution**

Set workflow permissions and token:

```yaml
permissions:
  contents: read
  packages: read

env:
  GITHUB_PACKAGES_TOKEN: ${{ secrets.GITHUB_TOKEN }}
  TEST_DB_CONNECTION: "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres"
```

Add the same PostgreSQL 16 service definition used by `ci.yml`, remove
job-level `continue-on-error`, give the job and Stryker step bounded timeouts,
and harden the summary step with `-ErrorAction SilentlyContinue` plus an explicit
report-file existence check.

In `stryker-config.json`, retain `AiObservatory.Api` as `project`, reduce
`test-projects` to:

```json
["tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj"]
```

- [ ] **Step 3: Add npm Dependabot coverage**

Add a weekly npm entry at `/src/AiObservatory.Web`, Monday 06:00 Europe/London,
with a ten-PR limit, `chore` commit prefix including scope, and a grouped
`npm-minor-and-patch` update policy. Add `include: scope` to GitHub Actions
commit messages and ensure the NuGet testing group includes
`Microsoft.NET.Test.Sdk`.

- [ ] **Step 4: Validate workflow syntax**

Run:

```powershell
actionlint
```

If the standalone executable is unavailable, validate by pushing the branch and
requiring the workflow's SHA-pinned actionlint steps to pass before merge.

- [ ] **Step 5: Commit CI normalization**

```powershell
git add .github stryker-config.json
git commit -m "ci: repair observatory quality workflows"
```

---

### Task 4: Run integrated verification and publish one ready PR

**Files:**
- Verify: all modified files
- Create: one GitHub pull request

**Interfaces:**
- Consumes: Tasks 1-3 and the repository's existing PostgreSQL test contract.
- Produces: a green, reviewable PR with real mutation execution evidence.

- [ ] **Step 1: Start an isolated PostgreSQL test service**

Use an explicit temporary container name and port, wait for `pg_isready`, and
set:

```powershell
$env:TEST_DB_CONNECTION = 'Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres'
```

If the local Docker daemon is unavailable, rely on the identical PR CI service
container and record the local environment limitation.

- [ ] **Step 2: Run complete local verification**

Run:

```powershell
dotnet restore AiObservatory.slnx
dotnet build AiObservatory.slnx --configuration Release --no-restore
dotnet test AiObservatory.slnx --configuration Release --no-build
dotnet format AiObservatory.slnx --verify-no-changes --no-restore
dotnet list AiObservatory.slnx package --vulnerable --include-transitive
git diff --check origin/main...HEAD
git status --short
```

Repeat frontend audit, lint, tests, all-source coverage, and build after the
final lock file is in place.

- [ ] **Step 3: Review the final diff for excluded churn**

Confirm there are no project renames, migrations, `.gitignore` changes,
historical-document rewrites, XML-comment retrofits, or broad complexity
refactors. Confirm `NUnit`, `Moq`, and `FluentAssertions` remain absent.

- [ ] **Step 4: Push and open one ready pull request**

Push `chore/standardize-scaffolding`, create a non-draft PR against `main`, and
describe dependency, architecture, coverage, CI, mutation, and verification
changes. Comment exactly:

```text
Gitar review
```

- [ ] **Step 5: Validate GitHub execution**

Wait for PR CI, coverage, React Doctor, CodeQL, and review automation. Manually
dispatch `mutation.yml` against `chore/standardize-scaffolding`; confirm restore,
PostgreSQL-backed tests, Stryker, summary, and artifacts execute rather than
being masked as green.

- [ ] **Step 6: Triage findings and hand off for merge approval**

Fix actionable findings on the same branch and dismiss non-actionable findings
with rationale. Re-run affected checks, report the final evidence and remaining
advisory warnings, and leave the ready PR for user approval. Merge later through
GitHub rebase-merge only after approval.
