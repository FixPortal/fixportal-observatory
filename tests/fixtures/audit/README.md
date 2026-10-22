# Synthetic portable audit v1

`golden-v1.zip` contains only invented identifiers and values. No installation capture,
conversation, credential, environment variable or filesystem path is included.

The retained capture contains one Claude message with input counter 9007199254740993,
zero other counters, standard pricing dimensions and explicit closure. Its original
canonical NDJSON bytes, manifest count and SHA-256 are retained in the archive.
The frozen event and aggregate both report the same exact input counter, zero output,
zero cost/savings and one request on 2026-10-20. Source/daily checks are explicitly
excluded for this aggregate replay fixture. Aggregate numbers match; aggregate
thought-token and machine attribution exclusions remain visible. Presentation has
no captured probe and is incomplete; pricing arithmetic is incomplete because the retained application/catalog publication evidence is absent.
The overall verdict is **Incomplete**, never a claim of complete source assurance.

`golden-v1.result.json` is the canonical result of the installed independent verifier
1.0.0, rule version 1, schema 1. Its SHA-256 is
`5a194bb9f85af8731a076e0fbea4baad6a57e0f8a32c5c746f3cea90d9a78f5b`.
These v1 bytes remain fixed. New process tests require explicit reevaluation with
the installed verifier and retain this recorded result; they do not relabel it as
a new original replay. Other aggregate tests independently establish numerical expectations.

To reproduce the original result, build the public v1 Audit/CLI source from commit
`37340cfcdab22a857282648c130289e666d4af46` in a separate checkout. Its Audit/CLI sources
are identical to the preserved compatibility runner's sources. This reproduces source
semantics, not the exact original binary or a hermetic .NET runtime:

```powershell
git worktree add --detach <new-v1-checkout> 37340cfcdab22a857282648c130289e666d4af46
dotnet build <new-v1-checkout>/src/AiObservatory.Audit.Cli/AiObservatory.Audit.Cli.csproj --configuration Release
dotnet <new-v1-checkout>/src/AiObservatory.Audit.Cli/bin/Release/net10.0/AiObservatory.Audit.Cli.dll replay <current-checkout>/tests/fixtures/audit/golden-v1.zip --output <new-directory> --verifier-version 1.0.0
```

This returns exit 3 and writes `result.json`, `result.sha256`, and `report.txt`.
Exit 0 means matched, 2 mismatched, 3 incomplete, and 4 invalid, incompatible or failed.
The executable uses only its installed library. It never fetches packages, downloads
another verifier, executes archive content, reads credentials or opens a database.
`--reevaluate` explicitly evaluates retained facts with installed rules and preserves
`recorded-result.json` plus `evaluation.json`; it does not claim identical replay.

Bundle v1 declares and hashes every entry except `manifest.json`, whose content hash
is SHA-256 over the canonical path-sorted entry list (keys `length`, `path`, `sha256`).
Fixed entries are `options.json`, `result.json`, `snapshot/{events,aggregates,spend,source-statuses,fx}.ndjson`
and `pricing/catalogs.json`. Capture paths use lowercase canonical GUIDs and ordered
six-digit chunk indices: `captures/<guid>/manifest.json` and
`captures/<guid>/chunks/000000.ndjson`. Source chunk bytes must already be canonical
NDJSON with one LF after each record; they are preserved rather than regenerated.

The hard expansion limit is 512 MiB per bundle, each capture chunk is at most 8 MiB,
each capture has at most one million records, and the archive has at most 10,000 entries.
Manifest/options and each capture manifest are limited to 1 MiB; each NDJSON line is
limited to 1 MiB. Central-directory metadata is limited to 8 MiB before materializing
ZIP entries. Compressed input is bounded to 528 MiB including metadata; compressed
entries may expand at most 200 times their compressed size. ZIP64 and multidisk
archives are unsupported because these sizes/counts do not require them. Callers
may tighten the public limits, and cannot lift the protocol maxima. Writer output
uses stored ZIP entries, fixed timestamps, and ordinal paths for reproducible bytes.

Task 10 explicitly advanced this synthetic pre-release fixture under verifier 1.0.0.
The input digest remains 0048b16609f4b1075bf5fd40bac774df99457485e81eddea260dfe40bcb99827.
The former result digest was 9ba1eba53ee58dfe669aa6e2d4c1ca96a6bc909b4275c68a98258965026452b8;
pricing is now implemented with honest missing-provenance coverage. No published or private recorded result was rewritten.
Future published semantic changes require a verifier version change.

## Prospective synthetic v2

`golden-v2.zip` and `golden-v2.result.json` are additional invented evidence, with
Claude source version 2, extractor 1.1.0, verifier 1.1.0/audit rule 2, and wire schema 1.
Two closed messages have identical other dimensions: 70,000 uncached input + 30,000
cache-read input + 0 creation (100,000, lower; output 20) and 70,000 uncached input +
30,000 cache-read input + 1 creation (100,001, upper; output 37).
Source/daily checks match both independent bands.
No pricing or complete installation assurance is asserted by this scoped Matched result.
The existing `BundleTests.VersionTwo_BundleIsDeterministicAndContainsBothIndependentBands`
constructs the same facts and compares the exact archive and result bytes.

| Artifact | SHA-256 |
| --- | --- |
| golden-v1.zip | adbf69fa7d1b54d9752ab6fd15cb7b7d62f8ac552603ef35e6cb04dbd841f014 |
| golden-v1.result.json | 5a194bb9f85af8731a076e0fbea4baad6a57e0f8a32c5c746f3cea90d9a78f5b |
| golden-v2.zip | 2eb839718c9ad11798d587cd0185c99549d2c5cc790b3f4c3cb8b3733997f4f4 |
| golden-v2.result.json | 5469f42f0643b857f3427dec79ddcb762abe54793404cfa4423bf94046db9677 |

The current CLI replays v2 by default; `node scripts/audit-replay-smoke.mjs` checks
two deterministic runs, old-identity rejection and linked reevaluation without
building, restoring, downloading or choosing a runner from bundle content.

Optional frozen financial facts use the declared snapshot/financial-observations.ndjson entry;
its absence means legacy/unfrozen, while a present empty entry means an inspected empty list.
Source qualification and partial-capture acknowledgement remain optional digest-bound options.
