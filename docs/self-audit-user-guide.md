# Self-audit user guide

Self-audit checks whether selected Observatory figures agree with retained, independently extracted local usage evidence. It gives each comparison a verdict, coverage counts and findings. An audit is a comparison of the evidence it could inspect: **Matched does not certify missing history, source completeness or a provider invoice**.

For machine installation and private settings, see the [operator guide](self-audit.md). For components and trust boundaries, see the [architecture guide](self-audit-architecture.md).

## Before you start

- An administrator must enable self-audit and register the machine that holds the source files. The local companion must be configured for the exact Claude or Codex source IDs and be able to reach the Observatory. Self-audit is disabled by default; if the tab is absent or reports that it is disabled, contact the administrator.
- Use a signed-in account with administrator access to create runs, request captures, replay, verify displayed totals or export bundles. A viewer can inspect retained summaries, sanitized findings and readable reports. The public demo is synthetic and read-only.
- Know the **configured source ID** for each source on the machine. The extractor family (`claude-local` or `codex-local`) identifies the file format; a source ID such as `codex-local@workstation` identifies a particular configured instance. Select the registered machine ID for that scope.
- Choose a completed UTC date range. The wizard defaults to the previous seven completed UTC days and accepts up to 31 inclusive days. A date range is not proof that every source file from those days still exists.

## Create a run

1. Open **Self-audit** and, under **Choose the evidence to compare**, select the UTC dates, source families, their configured source IDs and the registered machine.
2. Read **Readiness evidence**. Check the companion's last successful observation, diagnostics and any retained captures offered for this scope. Select a suitable retained capture, or choose **Request fresh captures · wait for offline machines**. A fresh request can wait up to 24 hours for an offline machine; a recent observation alone does not establish complete source history.
3. Select **Allow partial evidence after explicit acknowledgement** only if you may need to inspect an incomplete cohort. Selecting this option alone does not mark a run partial; if you later acknowledge missing evidence and choose **Proceed with partial evidence**, the overall verdict cannot be Matched, even if available numeric comparisons agree.
4. Select **Start audit**. The run may wait for capture, freeze its inputs and then verify them. Open it from **Retained audits** to see progress and, when finished, the outcome. Completion means processing ended; it does not mean the figures matched. If the run asks for an explicit decision, review the missing evidence before using **Proceed with partial evidence** or **Cancel audit**.

Creating another run is not a way to recover source files that have been deleted. Preserve source evidence and the companion's private identity and state according to your retention policy.

## Read the result

Read the **overall verdict**, then each check's verdict, coverage and findings. Coverage counts describe the records or comparison groups the verifier examined; they are not an accuracy percentage or a count of all historical sessions. A check can match on stored arithmetic while capture completeness remains unresolved.

| Verdict         | Meaning                                                                                                                                | Next step                                                                                                                                                       |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Matched**     | Every executed check in the declared scope matched, with no incomplete or unsupported check and no acknowledged partial evidence.      | Keep the run ID and report; still assess whether the selected sources and date range cover the decision you need to make.                                       |
| **Mismatched**  | At least one comparison disagreed. This takes precedence over other gaps.                                                              | Open its finding and compare the named scope, source records, pricing basis and frozen figures.                                                                 |
| **Incomplete**  | Evidence needed for a conclusive comparison is absent, stale, unverified or explicitly partial, or no check ran.                       | Inspect coverage and readiness codes; obtain fresh qualified evidence where possible and run again. Permanently lost history cannot be reconstructed by replay. |
| **Unsupported** | A check has no supported verifier or qualified source format. The overall run is **Incomplete** when a requested check is unsupported. | Treat the affected check as unverified and use a supported source or another validation method.                                                                 |

**Closed · Incomplete** in Retained audits means evaluation finished; it is not a pending task. If original historical files are permanently gone, record a separate closure reason such as **Closed — historical evidence unavailable** in the operator report. Keep the verifier's Incomplete verdict and findings unchanged. The dashboard cannot infer permanent loss from a gap code alone.

The checks answer different questions:

| Check                      | What it compares                                                                                 |
| -------------------------- | ------------------------------------------------------------------------------------------------ |
| **Source accounting**      | Independent source usage facts against the frozen Observatory source accounting.                 |
| **Daily attribution**      | Usage assigned to UTC days and models.                                                           |
| **Event aggregation**      | Stored event facts against the corresponding aggregate arithmetic.                               |
| **Pricing arithmetic**     | Rated amounts against frozen pricing applications and catalog evidence.                          |
| **Displayed totals**       | A recorded observation of the dashboard's summary and formatting against the frozen data.        |
| **Billing reconciliation** | Provider billing or invoice agreement, only where qualified evidence and a supported rule exist. |

For example, **Event aggregation: Matched** alongside **Source accounting: Incomplete** says the stored events add up but the available native files do not establish complete source coverage. A displayed amount of `£0.00` can be a correctly rounded tiny notional value; inspect the source, scope, basis, native amount and pricing evidence before treating it as zero spend. Billed spend, estimate and subscription notional are separate measures; see [truth and pricing](truth-and-pricing.md).

## Preserve or repeat evidence

On a completed run, **Download report** gives a readable summary. **Export evidence bundle** downloads the private frozen facts and recorded result for independent replay. Keep the ZIP, its checksum and a matching trusted verifier archive in a protected backup. The report alone cannot restore replay.

**Replay retained inputs** makes a linked evaluation of the retained facts; it does not collect missing source history or alter the original result. **Verify displayed totals** records a new, linked observation of the current frontend against the frozen snapshot when the run includes that check. It is a declared application observation, not external attestation. These actions need administrator access and retained detailed evidence.

Evidence normally expires 90 days after capture sealing; summaries normally expire 365 days after run completion. Policy changes affect future deadlines only. An administrator can review **Evidence retention** and deletion impact. Export before evidence expires; deletion or expiry can make replay unavailable while the readable summary remains.

For an offline replay command and exit codes, see [offline replay](self-audit.md#offline-replay). A valid replay of an **Incomplete** result exits `3`; verify the canonical result digest to distinguish a deterministic incomplete verdict from a replay failure.

## When something looks wrong

| Symptom                                                                                                  | What to check                                                                                                                                                                             |
| -------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Self-audit** is missing, disabled or shows an unavailable capability                                   | Ask the administrator to check feature settings, API health and your access; the feature is opt-in.                                                                                       |
| A fresh capture stays pending                                                                            | Check that the selected machine is online, the companion is polling with its own machine credential, the configured source ID matches, and the request has not passed its 24-hour window. |
| `capture_missing`, `source_unavailable`, `source_stale` or `source_refresh_failed`                       | Check readiness diagnostics and the local source/companion. A healthy API alone does not supply native evidence.                                                                          |
| `incomplete_capture`, `session_closure_unverified`, `revision_unverified` or `unsupported_source_format` | Inspect source qualification and capture findings. Do not interpret an open or unqualified source as a complete period.                                                                   |
| `pricing_unverified`, `pricing_stale` or missing pricing application evidence                            | Check the frozen pricing basis and catalog provenance; a later catalog refresh does not fill a historical gap automatically.                                                              |
| A bundle is unavailable but the report opens                                                             | Detailed evidence may have expired or been deleted. Use a previously saved private bundle if one exists.                                                                                  |

Share the run ID, date range, check names, verdicts and sanitized finding codes when asking for help. Do not share machine credentials, raw transcripts or private export bundles in an issue.
