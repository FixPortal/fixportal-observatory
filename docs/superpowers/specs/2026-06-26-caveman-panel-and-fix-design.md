# Caveman Panel + Session Fix — Design

Date: 2026-06-26

## Problem

Two issues, one spec:

1. **Caveman sessions stale.** `observe-sweep.ps1` has a Caveman arm that reads `~/.claude/.caveman-history.jsonl` and POSTs to `/api/caveman-sessions`. That file only grows when the user explicitly runs `/caveman-stats`. No automatic hook writes to it at session end, so the Observatory has seen exactly one session (the one time `/caveman-stats` was run manually) and no updates since.

2. **CavemanStatsPanel is always-visible card row.** Currently rendered inline between SummaryCards and SubscriptionPanel with no affordance to hide it. As more optional panels arrive (Budget Rules, future), they'd stack up without a consistent collapse pattern.

---

## Part 1 — Bug fix: auto-record Caveman sessions

### Root cause

`caveman-stats.js` appends to `.caveman-history.jsonl` only when called via the `/caveman-stats` UserPromptSubmit hook. There is no Stop hook that calls it automatically.

`observe-sweep.ps1` is correctly wired as a Stop hook and already has a Caveman arm, but the source file (`~/.claude/.caveman-history.jsonl`) stays stale, so the Observatory never sees new sessions.

### Fix

Add a new Stop hook script at `~/.claude/hooks/caveman-stop.ps1` that:

1. Reads `CLAUDE_SESSION_ID` env var to locate the current transcript JSONL (same pattern as the disabled `observe-stop.ps1`).
2. Calls `caveman-stats.js --session-file <path>` via Node so it appends a snapshot to `.caveman-history.jsonl`.
3. Fails silently (fire-and-forget wrapper around the Node call).

Register it in `~/.claude/settings.json` as a **synchronous** Stop hook (no `async: true`, timeout 8s) listed **before** the `observe-sweep.ps1` entry. Because synchronous hooks block, caveman-stop will complete — and the history file will be current — before Claude Code dispatches the async observe-sweep. This eliminates the write/read race with zero complexity cost.

Runtime: `caveman-stats.js` reads one JSONL file and appends a line — well under 1s; blocking cost is negligible.

### What not to change

- `caveman-stats.js` — no modification needed; it already appends correctly when called with `--session-file`.
- `observe-sweep.ps1` Caveman arm — already correct; just needed the source file to stay current.
- The Observatory API (`/api/caveman-sessions`) — upsert semantics handle re-posts safely.

---

## Part 2 — UI: collapsible optional-panels zone

### Layout

```
SummaryCards          (always visible — unchanged)
─────────────────────────────────────────────────
[optional panel zone]
  • Caveman panel     (collapsed by default; state persisted per-panel in localStorage)
  • (future panels slot in here)
─────────────────────────────────────────────────
SubscriptionPanel     (unchanged)
Charts / grids        (unchanged)
```

No zone label. The panels sit in a gap between SummaryCards and SubscriptionPanel. Their visual language distinguishes them from the headline cards (lighter treatment, disclosure indicator) without needing a heading.

### CollapsiblePanel component

New component: `src/AiObservatory.Web/src/components/CollapsiblePanel.tsx`

Props:
```ts
interface CollapsiblePanelProps {
  id: string           // localStorage key: `panel-${id}-expanded`
  title: string
  summary?: string     // shown in header at all times (e.g. "142 sessions · £378 saved")
  children: ReactNode
}
```

**State and persistence:**
- Initial state: `localStorage.getItem('panel-${id}-expanded') === 'true'`. Defaults to `false` (collapsed) when absent.
- Toggle: flip boolean, write back to localStorage.

**Animation — `grid-template-rows` transition:**

The body uses a two-element wrapper pattern that requires no JS measurement:

```tsx
<div className="collapsible-panel__body-outer" aria-hidden={!open}>
  {/* inner has no overflow:hidden itself — the outer clips it */}
  <div className="collapsible-panel__body-inner">
    {children}
  </div>
</div>
```

```css
.collapsible-panel__body-outer {
  display: grid;
  grid-template-rows: 0fr;
  transition: grid-template-rows 0.2s ease;
}
.collapsible-panel__body-outer.open {
  grid-template-rows: 1fr;
}
.collapsible-panel__body-inner {
  overflow: hidden;   /* clips the child during the 0fr→1fr transition */
  border-top: 0px solid var(--border);
  transition: border-top-width 0s 0.2s; /* border only visible once open */
}
.collapsible-panel__body-outer.open .collapsible-panel__body-inner {
  border-top: 1px solid var(--border);
  transition: none;
}
```

The `0fr → 1fr` trick works in all modern browsers (Chrome 107+, Firefox 116+, Safari 16+). No `max-height` guessing, no JS layout reads.

**Accessibility:**

- Header element: `<button type="button">` (not `<div>`) so it's keyboard-focusable and activatable with Space/Enter natively.
- `aria-expanded={open}` on the button.
- `aria-controls={bodyId}` on the button; `id={bodyId}` on the body outer div.
- `aria-hidden={!open}` on the body outer so screen readers skip collapsed content.
- `prefers-reduced-motion`: when set, skip the transition entirely (add `@media (prefers-reduced-motion: reduce)` override that sets `transition: none`).

Visual design (matches v2 mockup):
- Container: `border: 1px solid var(--border)`, `border-radius: var(--r-panel)`, `overflow: hidden`.
- Header button: full-width, `background: var(--card-bg)`, hover → `var(--surface)`, padding `10px 14px`, `display: flex; align-items: center; gap: 10px`.
- Focus ring: `outline: 2px solid var(--brand); outline-offset: -2px` on `:focus-visible`.
- Teal dot: `6px × 6px`, `border-radius: 50%`, `background: var(--brand)`, `flex-shrink: 0`.
- Title: `0.75rem`, weight 600, `color: var(--text)`.
- Summary: `0.68rem`, `color: var(--text-muted)`.
- Chevron: Unicode `▾`, `color: var(--text-faint)`, `font-size: 10px`, `margin-left: auto`, `transition: transform 0.2s ease`, `rotate(180deg)` when open.
- Body inner: `background: var(--bg)` (recessed), padding `12px 14px`.

### CavemanStatsPanel refactor

`CavemanStatsPanel.tsx` currently renders a flat `.summary-cards` grid. Refactor:
1. Wrap in `<CollapsiblePanel id="caveman" title="Caveman" summary={summaryLine}>`.
2. Keep the 4 stat cards inside (body background is `var(--bg)` so use `var(--card-bg)` cards to maintain contrast — same as Option C mockup).
3. Remove the `style={{ marginTop: 0 }}` hack.
4. Drop the `if (!stats || stats.sessions === 0) return null` guard — always render the panel; summary shows `'No sessions yet'` when empty so users know the feature exists.

`summaryLine`:
```ts
const summaryLine = stats && stats.sessions > 0
  ? `${stats.sessions.toLocaleString()} sessions · ${formatGbp(stats.totalEstSavedUsd, rate)} saved`
  : 'No sessions yet'
```

`summaryLine` is always shown in the header (both collapsed and expanded), giving a useful at-a-glance figure without expanding.

### Dashboard.tsx

Wrap the panel in a zone div for spacing:
```tsx
<SummaryCards />
<div className="collapsible-panel-zone">
  <CavemanStatsPanel />
</div>
<SubscriptionPanel />
```

`SubscriptionPanel` already has `.sub-panel { margin-top: var(--space-6) }` so the gap below the zone is handled automatically.

### CSS additions to index.css

```css
/* ---- Collapsible optional panels ---- */
.collapsible-panel-zone {
  max-width: 1200px;
  width: 100%;
  margin-inline: auto;
  padding: 0 var(--space-6);
  margin-top: var(--space-6);
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.collapsible-panel {
  border: 1px solid var(--border);
  border-radius: var(--r-panel);
  overflow: hidden;
}
.collapsible-panel__header {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 14px;
  background: var(--card-bg);
  cursor: pointer;
  border: none;
  width: 100%;
  text-align: left;
  font: inherit;
  color: inherit;
  transition: background var(--transition-fast);
}
.collapsible-panel__header:hover { background: var(--surface); }
.collapsible-panel__header:focus-visible {
  outline: 2px solid var(--brand);
  outline-offset: -2px;
}
.collapsible-panel__dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--brand);
  flex-shrink: 0;
}
.collapsible-panel__title { font-size: 0.75rem; font-weight: 600; color: var(--text); }
.collapsible-panel__summary { font-size: 0.68rem; color: var(--text-muted); }
.collapsible-panel__chevron {
  margin-left: auto;
  color: var(--text-faint);
  font-size: 10px;
  flex-shrink: 0;
  transition: transform 0.2s ease;
}
.collapsible-panel__chevron--open { transform: rotate(180deg); }

/* Body — grid-template-rows animation (no max-height guessing) */
.collapsible-panel__body-outer {
  display: grid;
  grid-template-rows: 0fr;
  transition: grid-template-rows 0.2s ease;
}
.collapsible-panel__body-outer--open { grid-template-rows: 1fr; }
.collapsible-panel__body-inner {
  overflow: hidden;
  background: var(--bg);
  padding: 0 14px;
  transition: padding 0.2s ease;
}
.collapsible-panel__body-outer--open .collapsible-panel__body-inner {
  padding: 12px 14px;
  border-top: 1px solid var(--border);
}

@media (prefers-reduced-motion: reduce) {
  .collapsible-panel__body-outer { transition: none; }
  .collapsible-panel__chevron { transition: none; }
}
```

---

## Testing

**Hook fix:**
1. End a Claude Code session (triggers Stop hooks).
2. Confirm `.caveman-history.jsonl` has a new entry for the session ID.
3. Run `observe-sweep.ps1 -Force -Verbose` — confirm POST to `/api/caveman-sessions` succeeds and `state.caveman.mtime` is saved in the state file.

**CollapsiblePanel — browser manual:**
1. First visit: Caveman panel is collapsed.
2. Expand → hard-refresh → still expanded.
3. Collapse → hard-refresh → collapsed.
4. Keyboard: Tab to header, Space or Enter toggles; chevron animates; focus ring visible.
5. Reduced-motion: toggle snaps instantly (no transition).

---

## Out of scope

- Drag-to-reorder optional panels.
- Server-side persistence of panel state (localStorage per-browser is sufficient; the tool is personal).
- Additional panels (Budget Rules etc.) — this spec establishes the pattern; each is a separate PR.
