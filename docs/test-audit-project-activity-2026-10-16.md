# Test audit A20: real by-project activity projection

Three real HTTP/PostgreSQL cases pin per-project session counts, repeated-session upsert/store identity, fractional66.7/33.3 shares, zero-activity rows and empty-range200/empty-array behavior. Inputs use fixed dates; each async case has one linked30-second diagnostic ceiling.

Five independent compiling production mutations fail their intended assertions: global session counts, missing rounding, integer division, incorrect zero-total fallback and empty-range404. Restored3cases pass, zero skips. Full solution1644passed0failed1existingpricingqualificationskip before the final deadline-only change; final targeted3cases and formatting pass afterward. Build0warnings/errors and321-file formatting pass. No production changes.

Removing Distinct alone is equivalent under the actual migrated unique SessionId constraint: the deliberate mutation remained green. This is explicitly refuted as a harmful regression; no impossible duplicate rows or dropped index were used. Actual count meaning is independently caught.

Tests used owned SDK/PostgreSQL guests on an internal network with no host mounts/socket/ports/production credentials; both are stopped. Full hosted CI remains the integration gate. Other Observatory findings remain separate.
