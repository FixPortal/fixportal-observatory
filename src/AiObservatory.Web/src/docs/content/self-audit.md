# Self-audit

Self-audit compares frozen Observatory totals with independently extracted usage facts. It is disabled by default. An administrator enables validated settings and registers a machine companion before requesting captures.

## Run and read an audit

Open **Audit**, choose dates and registered machine sources, then inspect readiness. The default range is the previous seven completed UTC days. Create a run and review each check's verdict, coverage and findings. Partial captures require explicit acknowledgement. Incomplete and unsupported checks remain visible even when other checks match.

Claude and Codex have independent extractors. The other six local sources and unqualified billing feeds are unsupported. Pricing needs frozen application/catalog provenance; presentation needs a probe tied to the snapshot. Agreement does not prove complete source history or invoice accuracy.

## Access and retention

Viewers can read summaries, sanitized findings and reports. Administrators control capture, cancellation, replay, detailed evidence, bundles, presentation probes, retention and deletion. A read-only key remains read-only with an Entra session. Demo access remains synthetic and read-only.

Evidence defaults to 90 days from sealing; summaries default to 365 days from completion. Changes affect future deadlines. Download a private replay bundle before evidence expires, and keep a private backup of that bundle and its compatible verifier. A readable report alone cannot restore replay.

Exports last at most two minutes, with two concurrent exports per instance. The default cohort limit is 512 MiB and combined temporary budget is 1 GiB. Retry interrupted exports while evidence remains available.

## Companion and offline verifier

The separate companion requires Node 24 and explicitly configured private source roots. Machine credentials are scoped, shown once, and can be rotated or revoked immediately. Preserve its identity directory privately for continuity.

The offline verifier requires .NET 10 and never downloads executables specified by a bundle. It writes canonical results and a SHA-256 digest. Exit 0 means matched, 2 mismatched, 3 incomplete and 4 invalid/incompatible/failed. Reevaluation with newer installed rules creates a new linked evaluation.

The [operator guide](https://github.com/FixPortal/fixportal-observatory/blob/main/docs/self-audit.md) has registration, rotation, retention, export and replay commands.
