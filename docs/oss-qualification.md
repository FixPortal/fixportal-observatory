# OSS qualification — 2026-09-10

Original local verification of `reviewer-findings-batch26`, based on main `7ea496f`.
That pass merged in [PR #237](https://github.com/FixPortal/fixportal-observatory/pull/237)
as `deabf21`; its [post-merge CI and deployment](https://github.com/FixPortal/fixportal-observatory/actions/runs/34493628784)
passed, including API and ingest health checks. The table preserves its local baseline;
the follow-up and post-merge evidence below are recorded separately.
They do not replace the September adversarial review or close its unrelated items.

## Current assessment

The evidence in this record supports Observatory as a credible OSS candidate for its
documented, tested scope: credential-free onboarding, enforced checks, regression
tests, and a successfully deployed remediation pass. This is a scoped assessment,
not a claim that every provider integration or the whole assurance programme is
complete. Live Google billing qualification remains **Not assessed** (M24).
The missing Gitar verdicts remain a historical review-coverage gap; the maintainer
has explicitly deferred further investigation for now, not supplied those verdicts.

No additional application change is justified solely by these two evidence gaps.
The earlier deployment-overlap and mutation-attribution limitations below still apply.

## Original local baseline

| Area | Evidence and disposition |
| --- | --- |
| Credential-free onboarding | `node scripts/compose-smoke.mjs`: built the Compose stack, served the app and JavaScript, proxied the authenticated API, found 70 synthetic aggregates, observed seed exit 0 and healthy ingest; removed its own containers and volume. Weekly/manual `quick-start.yml` repeats this extended check, outside the PR gate. This is an HTTP smoke, not a browser interaction test. |
| Frontend coverage enforcement | CI now runs `npm test -- --coverage`. Local run: 367 tests; statements 71.44%, branches 62.75%, functions 63.91%, lines 73.82%, all above the unchanged thresholds. Four workers prevent initialization oversubscription on high-core hosts. ESLint, TypeScript, and Vite build also passed. |
| Backend | Release restore/build: zero warnings/errors. `dotnet test --solution AiObservatory.slnx --configuration Release --report-xunit-trx --results-directory ./TestResults --timeout 5m`: 1,402 passed, one intentional performance-test skip, zero failures; database tests used disposable Testcontainers. |
| Repository contracts | CI's explicit four-file `node --test` command: 75 passed, including isolated public NuGet restore. Run this separately from .NET tests on Windows because the restore contract rebuilds their assemblies. |
| Policy-checker tests | `python -m pytest .github/scripts -q`: 38 passed; now included in workflow-lint. Removed obsolete private-helper tests, retained behavioural cases, repaired bracket-access handling and multiline `continue-on-error` fail-open behaviour. The same repair is prepared in the canonical scaffold-ci asset with regression checks. |
| Deployment serialization | Caller job acquires `deploy-production` for CI-triggered deployment; direct dispatch acquires the same group at workflow scope. The called workflow uses a distinct run-specific group to avoid re-entering the caller's lock. Actionlint and four deploy contract tests passed. No production deployment was triggered to test overlap. |
| Mutation configuration | Controlled comparison found the same 143 compile errors and 455 valid mutants with warnings-as-errors on/off. Compiler enforcement stays enabled. See [counts and limitations](mutation-testing.md#warnings-as-errors-qualification-2026-09-10). |
| Read-only authorization regression | Added one four-case theory for POST/PUT/PATCH/DELETE with a valid read-only key. Forcing the filter down its GET authorization path made all four cases fail; restoring the unchanged production logic passed. Isolated serial Stryker changed from 35 killed/one survived to 36 killed/zero survived, with one uncovered mutant unchanged. Broader kill-attribution repeatability remains unverified. |
| Google billing export | **Not assessed** against a real BigQuery export. No project/export-table pair was configured in the inspected deployment. M24 remains open; see [qualification requirements](provider-setup.md). |

GitHub documents [caller-job concurrency and reusable-workflow context](https://docs.github.com/en/actions/reference/workflows-and-actions/reusing-workflow-configurations).
The deployment check above establishes the configuration contract; actual overlapping
manual/called runs remain **UNVERIFIED:** concurrent production mutation despite the
shared lock would refute the serialization claim. Do not create production deployments
solely to manufacture that evidence.

## Follow-up — reviewer-findings-batch27

The mutation attribution investigation reproduced 15 outcome changes between identical
four-worker runs. Two one-worker runs matched every one of the 1,024 scoped records;
the configuration now retains one worker. Strengthened billing aggregation assertions
catch incorrect gross, discount, and retained raw net totals through independently
injected faults. The final pair, including all three assertions, again matched all
1,024 records (191 killed, 42 survived, 41.98%; still an informational score).
See [repeatability evidence and limits](mutation-testing.md#repeatability-qualification-2026-09-10-follow-up).

Local follow-up validation: Release build with zero warnings/errors; CSharpier passed;
the full .NET suite passed 1,402 tests with one intentional skip; frontend lint,
build, and all 367 tests with unchanged coverage thresholds passed; the 75 Node
contracts and 38 Python policy tests passed. After the final raw-net assertion,
all 437 API unit tests passed again. Independent local code review found the evidence
misidentification described above; the correction and test were re-reviewed with no
remaining findings. This local review does not substitute for the required PR reviewers.

The follow-up was folded into [PR #238](https://github.com/FixPortal/fixportal-observatory/pull/238),
preserving its canonical hygiene-checker sync. Its nine failing tests were reproduced,
then repaired to pass fixture action directories and create actual Dockerfiles.
Registry-lookalike and filename-case cases extend the Python suite to 40 passing tests.
The combined branch also passed the full .NET, frontend, and Node suites above.

## Post-merge evidence and remaining qualifications — 2026-09-10

PR #238 merged at `2026-09-10T17:14:15Z` as
`fd01963b35d3c7e8022d8e130ec7f7a54656dfa4`.
Its [post-merge CI and deployment](https://github.com/FixPortal/fixportal-observatory/actions/runs/34506964252)
completed successfully, including frontend, backend, Node contracts, workflow lint,
gate coverage, CI Gate, web/API/ingest deployment, and the ingest health check.
This was rechecked with `gh run view 34506964252 --repo FixPortal/fixportal-observatory`;
all ten jobs reported `success`. The follow-up's merge and deployment are therefore
no longer outstanding. This documentation update does not rerun those application tests.

### M24 — live Google billing export

Live Google billing validation is still **Not assessed**: the deployment recheck
has no `GOOGLE_CLOUD_PROJECT_ID` / `GOOGLE_BILLING_EXPORT_TABLE` pair. M24 needs the real
export table and an existing access method, not more synthetic fixtures. The maintainer
confirmed that no suitable export is available; provisioning Google Cloud infrastructure
solely to manufacture audit evidence is not part of this pass. The optional integration
remains unconfigured in the inspected deployment. This does not establish that the
integration works against a real export, or make M24 resolved or inapplicable.

Resume qualification when a suitable export and access are available: inspect partition
metadata, dry-run both production queries and record bytes processed, then compare recent,
boundary-day, and older corrected usage with source totals as described in
[provider setup](provider-setup.md). Missing pruning or mismatched/omitted billing groups
would refute qualification. The full programme is not claimed complete while this
remains unassessed.

### Gitar — investigation deferred by maintainer

CodeRabbit approved #237. On #238 it identified the hygiene-checker fixture mismatch;
that finding was fixed and its thread resolved, but its review was subsequently dismissed.
Neither fact supplies the missing Gitar review.

After the maintainer refreshed the renamed repository in Gitar, manual requests on
[#237](https://github.com/FixPortal/fixportal-observatory/pull/237#issuecomment-5624789207)
and [#238](https://github.com/FixPortal/fixportal-observatory/pull/238#issuecomment-5624789190)
were acknowledged. Gitar checks `103030938958` and `103030942703` completed with
`success` at `20:11:34Z` and `20:11:50Z`, respectively, but their title, summary, and text
were empty; the PR comments supplied acknowledgments and an automatic-processing pause
notice, not a review verdict. No Gitar review appeared in either PR's reviews API.
This records the inspected state, not a proven cause of the missing results.

The maintainer requested that Gitar investigation be set aside for now. No support
request or further review retry is part of this update. The review-coverage gap is
retained explicitly: deferral is not approval, a passing check is not a verdict, and
this decision does not change the repository's committed review policy for future PRs.

## v1.0.1 release snapshot — 2026-09-10

The fresh-history release incorporates main `7f29b79` (the latest canonical gate-checker
sync), this qualification record, and the release documentation. Before creating the
new root commit, the combined snapshot passed these local checks:

- `dotnet csharpier check .`: 281 files checked.
- Release restore/build: zero warnings and errors. The full `dotnet test` command above
  with `--no-build` passed 1,402 tests, with one intentional performance-test skip.
- Frontend `npm ci`, `npm run lint`, `npm test -- --coverage`, and `npm run build`:
  367 tests passed; coverage matched the original baseline and all thresholds passed.
- The explicit four-file Node contract command: 75 passed.
- `python -m pytest .github/scripts -q`: 40 passed; both gate coverage and workflow
  hygiene checks passed. Hygiene retained informational workflow-permission notices.
- `node scripts/compose-smoke.mjs`: frontend, JavaScript, authenticated API, 70 synthetic
  aggregates, and ingest health passed; its disposable stack and volume were removed.

This release verification does not close M24, supply missing Gitar verdicts, or extend
the mutation and deployment-overlap evidence beyond the qualifications above.
