# PostgreSQL-Free Tests and Complexity Cleanup Design

**Date:** 2026-07-21

## Goal

Make the ordinary unit-test lane runnable without PostgreSQL and leave the
solution build free of actionable code-quality warnings, without changing API,
ingestion, persistence, or authentication behaviour.

## Test classification

PostgreSQL-backed test classes receive the existing xUnit
`Category=Integration` trait. The three startup-guard tests that reach database
initialisation receive the trait individually so the remaining startup guards
stay in the unit lane. The README documents separate unit and integration
commands, and Stryker's existing `Category!=Integration` filter becomes
effective.

> **Correction, 2026-07-29.** The last clause was false when written and stayed
> false for eight days. Stryker was configured with `test-runner: mtp`, and
> `test-case-filter` is implemented only in Stryker's VSTest runner — the MTP
> runner ignores it silently, with no warning. The traits did make the
> `dotnet test` lanes work as described, but the mutation lane went on running
> every PostgreSQL-backed test against every mutant. It surfaced on 2026-07-27
> when the spend ledger's tests pushed the nightly run past its timeout.
>
> The fix was structural, not a filter: the database-backed tests moved into a
> separate `AiObservatory.Api.IntegrationTests` project, and Stryker now runs from
> the unit-only project's directory (`test-projects` does not restrict execution —
> only the working directory does). `test-runner` deliberately stays `mtp`;
> switching to `vstest` makes the filter work but produces invalid mutation
> results. See `docs/mutation-testing.md`.

## Complexity remediation

Endpoint route registration remains in the existing endpoint classes, but
complex inline handlers move to named private methods. Repeated request
validation is centralised only where it is genuinely duplicated. Transaction
boundaries, EF query shapes, response status codes, authentication decisions,
pagination limits, cancellation behaviour, and logging remain unchanged.

Natural helper boundaries will be extracted from activity, aggregates,
caveman, events, subscriptions, API-key filtering, budget alerts, and the
Anthropic and GitHub clients. `AdversarialReviewService.RecordRunAsync` and
`GitHubIngestionService.IngestSinceAsync` remain intact because their flat
guard/orchestration flows are clearer than an extraction made solely for the
metric; S3776 is suppressed only for those methods with a reason at the site.
The global S3776 threshold remains unchanged.

## Dependabot policy

The npm configuration ignores TypeScript major-version updates until TypeScript
7.1 restores the compiler API and `typescript-eslint` publishes a compatible
peer range, matching the established `fixportal-learning` policy. Dependabot
pull request 85 is closed because forcing TypeScript 7.0.2 would make ESLint
unusable.

## Verification

The branch must pass the PostgreSQL-free .NET test lane, integration-test
discovery, Release build with no warnings, both `dotnet format` verification
modes, frontend lint/tests/build, and Dependabot YAML inspection. All work ships
in the existing single ready pull request.
