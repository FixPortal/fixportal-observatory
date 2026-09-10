# OSS qualification — 2026-09-10

Local verification of `reviewer-findings-batch26`, based on main `7ea496f`.
These results qualify the proposed changes, not an already-deployed release.
They do not replace the September adversarial review or close its unrelated items.

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

The full programme is not claimed complete while live-provider qualification remains
unassessed. CI execution, review, and merge of this branch are also still outstanding.
