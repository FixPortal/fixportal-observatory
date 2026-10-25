# Self-audit

Self-audit compares frozen Observatory figures with independently extracted local usage facts. It is disabled by default. An administrator must enable it, register the source machine and configure its local companion. The public demo is synthetic and read-only.

## Run an audit

1. Open **Self-audit** and choose a completed UTC date range, source families, their exact configured source IDs and the registered machine. The default is the previous seven completed UTC days; the maximum is 31 inclusive days.
2. Read **Readiness evidence**. Select a retained capture or request a fresh one. An offline machine may take up to 24 hours to answer. Recent machine contact does not prove that every native file is present.
3. Select **Start audit**. If you allow partial evidence, inspect any gap before choosing **Proceed with partial evidence**. Acknowledging partial evidence prevents an overall Matched verdict; merely allowing it in the scope does not.
4. Open the run in **Retained audits**. Read the overall verdict, each check's verdict, coverage and findings. A completed run only means processing ended.

## Interpret the verdict

| Verdict         | Meaning                                                                                                                                           |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Matched**     | All executed checks in the selected scope agree, with no incomplete or unsupported check. It does not prove complete history or invoice accuracy. |
| **Mismatched**  | At least one comparison disagrees. Inspect its finding and the stated source, date and pricing basis.                                             |
| **Incomplete**  | Required evidence is missing, stale, unverified or explicitly partial, or no check ran.                                                           |
| **Unsupported** | A check has no qualified extractor or rule. An unsupported requested check makes the overall run Incomplete.                                      |

Coverage counts are groups examined, not an accuracy percentage. **Event aggregation** can match while **Source accounting** is incomplete: stored records may add up even when native history is missing. Pricing needs frozen application and catalog evidence; displayed totals need a probe tied to the frozen snapshot. Keep billed spend, estimates and subscription notional separate.

## Reports, replay and retention

Viewers can read summaries, sanitized findings and reports. Administrators can request capture, replay retained inputs, verify displayed totals, export private bundles and manage evidence retention. Replay makes a linked evaluation of retained facts; it cannot restore missing source files. Displayed-total verification is a declared observation of the current frontend, not external attestation.

Evidence defaults to 90 days after capture sealing and summaries to 365 days after run completion. Save the private bundle, checksum and compatible verifier before evidence expires. A readable report alone cannot restore replay. Offline replay requires .NET 10; exit `3` can be a valid deterministic replay of an Incomplete result.

See the [full user guide](https://github.com/FixPortal/fixportal-observatory/blob/main/docs/self-audit-user-guide.md) for troubleshooting, the [operator guide](https://github.com/FixPortal/fixportal-observatory/blob/main/docs/self-audit.md) for installation and replay commands, and the [architecture guide](https://github.com/FixPortal/fixportal-observatory/blob/main/docs/self-audit-architecture.md) for evidence flow and trust boundaries.
