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
The process tests compare both independent replays directly with these committed
bytes. Other aggregate tests independently establish numerical expectations.

Run a built CLI with:

```powershell
dotnet src/AiObservatory.Audit.Cli/bin/Release/net10.0/AiObservatory.Audit.Cli.dll replay tests/fixtures/audit/golden-v1.zip --output <new-directory> --verifier-version 1.0.0
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

Optional frozen financial facts use the declared snapshot/financial-observations.ndjson entry;
its absence means legacy/unfrozen, while a present empty entry means an inspected empty list.
Source qualification and partial-capture acknowledgement remain optional digest-bound options.
