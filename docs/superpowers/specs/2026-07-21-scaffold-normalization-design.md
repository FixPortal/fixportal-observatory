# AI Observatory Scaffold Normalization Design

**Date:** 2026-07-21

## Goal

Bring the existing .NET 10 and React application into line with the FixPortal
.NET, test, frontend, EF Core, and CI scaffolds without restructuring the
application or creating review-only churn.

## Scope

The normalization will:

- preserve every existing project name and the current `src/` and `tests/`
  layout;
- update direct NuGet and npm dependencies to current compatible releases,
  including Node 24 in local and CI build images;
- pin the vulnerable transitive `System.Security.Cryptography.Xml` and
  `Microsoft.OpenApi` dependencies to compatible fixed releases and refresh the
  npm lock file so the current `brace-expansion` advisory is removed;
- reference `FixPortal.CodeStyle` through `Directory.Build.props` with its
  version managed centrally;
- replace the repository's duplicate `FixPortalArchRules` implementation with
  the shared `FixPortal.CodeStyle.ArchRules` package;
- expose repository configuration and workflow files as solution items;
- reconcile CI job names, actionlint placement, Node versions, and Dependabot
  ecosystems with the scaffold;
- repair mutation testing so package restore is authorised, PostgreSQL-backed
  API tests can run, failures are not reported as successful, and the pilot
  uses only the test project relevant to the mutated API project;
- make frontend coverage include all source files, set honest thresholds from
  the measured all-source baseline, and give the architecture checks enough
  time under coverage instrumentation; and
- apply `dotnet format` only to the currently reported formatter/import drift.

## Deliberate exclusions

This pass will not rename projects, migrate the existing flat frontend to a
feature-first layout, retrofit XML comments, replace `.gitignore`, rewrite
historical documents, refactor advisory cognitive-complexity findings, change
application behavior, alter the EF Core model or migrations, or move jobs to
paid Blacksmith runners. Existing deployment, coverage, React Doctor, and
CodeQL functionality remains in place.

## Dependency and architecture approach

Central package management remains the source of every NuGet version. The
shared code-style package becomes a normal private-assets package reference in
`Directory.Build.props`; `Directory.Packages.props` holds its version alongside
the new architecture-rules package and the transitive security pin. Redundant
direct package references may be removed only where the source does not consume
the package API and restore/build proves the dependency remains available.

The existing architecture tests stay in `AiObservatory.Api.Tests`. They import
`FixPortalArchRules` from `FixPortal.CodeStyle.ArchRules`; the local copy is
deleted because it is byte-for-purpose duplication of the shared package.

## CI approach

The existing workflow split is retained. Every job checks out the repository
and runs the SHA-pinned actionlint action before its first build, test, or deploy
operation. Deploy workflows keep `cancel-in-progress: false`, trusted-main
gating, current third-party SHA pins, and their existing targets.

The mutation workflow receives `packages: read`, the repository token through
`GITHUB_PACKAGES_TOKEN`, and a PostgreSQL 16 service with the same test
connection used by ordinary CI. It mutates `AiObservatory.Api` and runs
`AiObservatory.Api.Tests`. `thresholds.break` remains zero, so mutation score is
informational, but workflow/tool failures are real failures rather than being
hidden by job-level `continue-on-error`.

Dependabot gains the missing npm ecosystem at
`/src/AiObservatory.Web`; existing NuGet and GitHub Actions coverage is retained.
CodeQL default setup is already active and requires no committed workflow.

## Frontend coverage approach

Vitest coverage will explicitly include `src/**`. The measured baseline with
that denominator is 25.65% statements, 19.73% branches, 19.70% functions, and
25.71% lines, so initial integer thresholds are 25, 19, 19, and 25 respectively.
TypeScript remains on 6.0.3 because the current `typescript-eslint` peer range is
`<6.1`; TypeScript 7 will wait for toolchain support rather than being forced.
This prevents untested files from disappearing from the denominator without
pretending the current suite has 70% whole-application coverage. Architecture
tests receive a targeted 15-second timeout because coverage instrumentation
pushed the initial ArchUnitTS graph build just beyond Vitest's five-second
default; ordinary tests keep the default timeout.

## Verification

The completed branch must pass:

- clean NuGet restore with no known vulnerable package report;
- Release solution build;
- all .NET tests against an ephemeral PostgreSQL 16 instance;
- EF migration/model validation through the existing database-backed tests;
- `dotnet format --verify-no-changes`;
- npm audit with no high/critical findings;
- frontend lint, 123-or-more tests, all-source coverage thresholds, and
  production build;
- local actionlint; and
- a manually dispatched mutation workflow on the feature branch that reaches
  and completes Stryker.

The work ships in one ready pull request. Automated findings and CI failures are
triaged on that branch, the PR receives the required `Gitar review` comment, and
the final merge uses GitHub rebase-merge.
