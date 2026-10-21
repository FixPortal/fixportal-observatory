# Caveman Panel + Session Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix stale caveman session data by adding an auto-capture synchronous Stop hook, and give CavemanStatsPanel a collapsible header with localStorage persistence.

**Architecture:** Part 1 is a new PowerShell Stop hook (`caveman-stop.ps1`) registered synchronously in `settings.json` before the async `observe-sweep.ps1`; this guarantees `.caveman-history.jsonl` is current when the sweep reads it, with no changes to `caveman-stats.js` or the API. Part 2 is a new `CollapsiblePanel` React component using CSS `grid-template-rows: 0fr → 1fr` animation and `localStorage` for per-panel state, which wraps `CavemanStatsPanel` in the Dashboard inside a `.collapsible-panel-zone` div.

**Tech Stack:** PowerShell 7+ (hook), React 18 + TypeScript (UI), Vitest + @testing-library/react + jsdom (tests), CSS custom properties + grid animation (no JS measurement), `localStorage` (panel state)

## Global Constraints

- caveman-stop.ps1 hook must have **no `async: true`** (synchronous) and be listed before `observe-sweep.ps1` in the Stop array
- Hook fails silently — never interrupts a session
- `grid-template-rows: 0fr → 1fr` animation only — no `max-height`
- `localStorage` key format: `panel-${id}-expanded` (string `'true'` / `'false'`)
- CollapsiblePanel default state: **collapsed** (unless localStorage has `'true'`)
- CavemanStatsPanel must **always render** — no early-return guard; show `'No sessions yet'` when empty
- No emoji anywhere in code or UI copy

---

## File Map

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `~/.claude/hooks/caveman-stop.ps1` | Synchronous Stop hook; appends caveman snapshot before sweep |
| Modify | `~/.claude/settings.json` | Insert caveman-stop entry before observe-sweep in Stop array |
| Create | `src/AiObservatory.Web/src/components/CollapsiblePanel.tsx` | Generic collapsible panel; localStorage + grid animation |
| Create | `src/AiObservatory.Web/src/components/CollapsiblePanel.test.tsx` | Unit tests for toggle, persistence, aria |
| Modify | `src/AiObservatory.Web/src/components/CavemanStatsPanel.tsx` | Wrap in CollapsiblePanel; remove early-exit guard; add summary line |
| Modify | `src/AiObservatory.Web/src/pages/Dashboard.tsx` | Wrap CavemanStatsPanel in `.collapsible-panel-zone` div |
| Modify | `src/AiObservatory.Web/src/index.css` | Append collapsible panel CSS block |

> **Note:** `~/.claude/` changes (hook + settings.json) are personal config files — they are **not part of the repository** and will not appear in the PR. They must be applied directly to `~/.claude/`.

---

### Task 1: Feature branch

**Files:** git only

- [ ] **Step 1: Create feature branch from origin/main**

  Run in the repo root (`<repo root>`):

  ```powershell
  git fetch origin
  ```

  ```powershell
  git checkout -b feat/caveman-panel-and-fix origin/main
  ```

  Expected: `Switched to a new branch 'feat/caveman-panel-and-fix'`

---

### Task 2: caveman-stop.ps1 + settings.json

**Files:**
- Create: `~/.claude/hooks/caveman-stop.ps1`
- Modify: `~/.claude/settings.json` (Stop hooks array, ~line 462)

**Interfaces:**
- Reads: `CLAUDE_SESSION_ID` env var, falls back to most-recently-modified JSONL in `~/.claude/projects/`
- Calls: `node ~/.claude/hooks/caveman-stats.js --session-file <transcript.jsonl>`
- Side-effect: appends one line to `~/.claude/.caveman-history.jsonl`

- [ ] **Step 1: Create caveman-stop.ps1**

  Write `~/.claude/hooks/caveman-stop.ps1`:

  ```powershell
  #Requires -Version 7
  # caveman-stop.ps1 — synchronous Stop hook.
  # Appends this session's caveman snapshot to .caveman-history.jsonl BEFORE
  # observe-sweep.ps1 (async) reads it, eliminating the write/read race.
  # Fails silently — never interrupts the session.

  $ErrorActionPreference = 'SilentlyContinue'
  try {
      $sessionId   = $env:CLAUDE_SESSION_ID
      $projectsDir = Join-Path $env:USERPROFILE '.claude\projects'
      $statsJs     = Join-Path $env:USERPROFILE '.claude\hooks\caveman-stats.js'

      if (-not (Test-Path $statsJs)) { exit 0 }

      $transcriptPath = $null
      if ($sessionId) {
          $transcriptPath = Get-ChildItem -Path $projectsDir -Filter "$sessionId.jsonl" `
              -Recurse -ErrorAction SilentlyContinue |
              Select-Object -First 1 -ExpandProperty FullName
      }
      if (-not $transcriptPath) {
          $transcriptPath = Get-ChildItem -Path $projectsDir -Filter '*.jsonl' `
              -Recurse -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending |
              Select-Object -First 1 -ExpandProperty FullName
      }

      if ($transcriptPath -and (Test-Path $transcriptPath)) {
          & node $statsJs --session-file $transcriptPath 2>$null | Out-Null
      }
  } catch {
      # fire-and-forget
  }
  ```

- [ ] **Step 2: Verify script exits cleanly**

  ```powershell
  pwsh -NoProfile -ExecutionPolicy Bypass -File ~/.claude/hooks/caveman-stop.ps1; Write-Output "exit $LASTEXITCODE"
  ```

  Expected: `exit 0` (no error output)

- [ ] **Step 3: Insert caveman-stop entry in settings.json**

  In `~/.claude/settings.json`, find the Stop array. The current order is:
  1. `api-error-alert.sh` (async)
  2. `observe-stop.ps1` (async, disabled but wired — it exits 0 immediately)
  3. `observe-sweep.ps1` (async, 120s)
  4. `icm.exe hook end` (async)
  5. `notify.ps1` (async)

  Insert the new entry **between observe-stop.ps1 and observe-sweep.ps1**.

  Find this text (end of the observe-stop.ps1 entry):
  ```json
            "async": true
          }
        ]
      },
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NoProfile -ExecutionPolicy Bypass -File C:/Users/chris/.claude/hooks/backfill/observe-sweep.ps1",
  ```

  Replace with:
  ```json
            "async": true
          }
        ]
      },
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NoProfile -ExecutionPolicy Bypass -File C:/Users/chris/.claude/hooks/caveman-stop.ps1",
            "timeout": 8
          }
        ]
      },
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NoProfile -ExecutionPolicy Bypass -File C:/Users/chris/.claude/hooks/backfill/observe-sweep.ps1",
  ```

  **Key:** no `"async": true` on caveman-stop — it must block synchronously.

- [ ] **Step 4: Validate settings.json is parseable**

  ```powershell
  Get-Content ~/.claude/settings.json | ConvertFrom-Json | Out-Null; Write-Output "valid"
  ```

  Expected: `valid`

- [ ] **Step 5: Manual smoke test (end this session)**

  End this Claude Code session. Then check:

  ```powershell
  Get-Content "$env:USERPROFILE\.claude\.caveman-history.jsonl" -Tail 3
  ```

  Expected: a JSON line with `session_id`, `ts`, `output_tokens` fields for the just-ended session.

  Optional deeper verification:
  ```powershell
  & "$env:USERPROFILE\.claude\hooks\backfill\observe-sweep.ps1" -Force -Verbose
  ```
  Expected: `Verbose: ... caveman ... POST` (or dry-run message) — `state.caveman.mtime` updated.

---

### Task 3: CSS additions to index.css

**Files:**
- Modify: `src/AiObservatory.Web/src/index.css` (append at end)

**Interfaces:**
- Produces CSS classes consumed by CollapsiblePanel.tsx: `collapsible-panel`, `collapsible-panel__header`, `collapsible-panel__dot`, `collapsible-panel__title`, `collapsible-panel__summary`, `collapsible-panel__chevron`, `collapsible-panel__chevron--open`, `collapsible-panel__body-outer`, `collapsible-panel__body-outer--open`, `collapsible-panel__body-inner`
- Produces layout class consumed by Dashboard.tsx: `collapsible-panel-zone`

- [ ] **Step 1: Append collapsible panel CSS to index.css**

  Append to the end of `src/AiObservatory.Web/src/index.css`:

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

- [ ] **Step 2: Commit**

  ```
  git add src/AiObservatory.Web/src/index.css
  git commit -m "style: add collapsible-panel CSS (grid-template-rows animation)"
  ```

---

### Task 4: CollapsiblePanel component + tests

**Files:**
- Create: `src/AiObservatory.Web/src/components/CollapsiblePanel.tsx`
- Create: `src/AiObservatory.Web/src/components/CollapsiblePanel.test.tsx`

**Interfaces:**
- Produces (named export):
  ```ts
  export function CollapsiblePanel(props: {
    id: string        // localStorage key: `panel-${id}-expanded`; body div id: `panel-${id}-body`
    title: string
    summary?: string  // always visible in header; omit to render no summary span
    children: ReactNode
  }): JSX.Element
  ```

- [ ] **Step 1: Write failing tests**

  Create `src/AiObservatory.Web/src/components/CollapsiblePanel.test.tsx`:

  ```tsx
  import { render, screen, fireEvent } from '@testing-library/react'
  import { CollapsiblePanel } from './CollapsiblePanel'

  beforeEach(() => localStorage.clear())

  test('collapsed by default when no localStorage entry', () => {
    render(
      <CollapsiblePanel id="test" title="Test panel">
        <p>body content</p>
      </CollapsiblePanel>
    )
    expect(screen.getByRole('button')).toHaveAttribute('aria-expanded', 'false')
  })

  test('expanded when localStorage has true', () => {
    localStorage.setItem('panel-test-expanded', 'true')
    render(
      <CollapsiblePanel id="test" title="Test panel">
        <p>body content</p>
      </CollapsiblePanel>
    )
    expect(screen.getByRole('button')).toHaveAttribute('aria-expanded', 'true')
  })

  test('click toggles open state and writes localStorage', () => {
    render(
      <CollapsiblePanel id="test" title="Test panel">
        <p>body content</p>
      </CollapsiblePanel>
    )
    const btn = screen.getByRole('button')
    fireEvent.click(btn)
    expect(btn).toHaveAttribute('aria-expanded', 'true')
    expect(localStorage.getItem('panel-test-expanded')).toBe('true')
    fireEvent.click(btn)
    expect(btn).toHaveAttribute('aria-expanded', 'false')
    expect(localStorage.getItem('panel-test-expanded')).toBe('false')
  })

  test('aria-controls points to body element id', () => {
    render(
      <CollapsiblePanel id="test" title="Test panel">
        <p>body content</p>
      </CollapsiblePanel>
    )
    expect(screen.getByRole('button')).toHaveAttribute('aria-controls', 'panel-test-body')
    expect(document.getElementById('panel-test-body')).toBeInTheDocument()
  })

  test('summary shown in header when provided', () => {
    render(
      <CollapsiblePanel id="test" title="Test panel" summary="5 sessions · £20 saved">
        <p>body content</p>
      </CollapsiblePanel>
    )
    expect(screen.getByText('5 sessions · £20 saved')).toBeInTheDocument()
  })

  test('no summary span rendered when summary prop omitted', () => {
    render(
      <CollapsiblePanel id="test" title="Test panel">
        <p>body content</p>
      </CollapsiblePanel>
    )
    expect(screen.getByRole('button').querySelector('.collapsible-panel__summary')).toBeNull()
  })
  ```

  > `@testing-library/react` and `@testing-library/jest-dom` must be set up in the project's vitest config (standard for Vite React apps). If `toHaveAttribute` / `toBeInTheDocument` are not recognised, check `vitest.config.ts` for `setupFiles` pointing to a file that imports `@testing-library/jest-dom`.

- [ ] **Step 2: Run tests to confirm they fail**

  From `src/AiObservatory.Web/`:
  ```
  npx vitest run src/components/CollapsiblePanel.test.tsx
  ```

  Expected: FAIL — `Cannot find module './CollapsiblePanel'`

- [ ] **Step 3: Create CollapsiblePanel.tsx**

  Create `src/AiObservatory.Web/src/components/CollapsiblePanel.tsx`:

  ```tsx
  import { useState, type ReactNode } from 'react'

  interface CollapsiblePanelProps {
    id: string
    title: string
    summary?: string
    children: ReactNode
  }

  export function CollapsiblePanel({ id, title, summary, children }: CollapsiblePanelProps) {
    const [open, setOpen] = useState(() => localStorage.getItem(`panel-${id}-expanded`) === 'true')
    const bodyId = `panel-${id}-body`

    function toggle() {
      setOpen(prev => {
        const next = !prev
        localStorage.setItem(`panel-${id}-expanded`, String(next))
        return next
      })
    }

    return (
      <div className="collapsible-panel">
        <button
          type="button"
          className="collapsible-panel__header"
          aria-expanded={open}
          aria-controls={bodyId}
          onClick={toggle}
        >
          <span className="collapsible-panel__dot" aria-hidden="true" />
          <span className="collapsible-panel__title">{title}</span>
          {summary !== undefined && (
            <span className="collapsible-panel__summary">{summary}</span>
          )}
          <span
            className={`collapsible-panel__chevron${open ? ' collapsible-panel__chevron--open' : ''}`}
            aria-hidden="true"
          >
            ▾
          </span>
        </button>
        <div
          id={bodyId}
          className={`collapsible-panel__body-outer${open ? ' collapsible-panel__body-outer--open' : ''}`}
          aria-hidden={!open}
        >
          <div className="collapsible-panel__body-inner">
            {children}
          </div>
        </div>
      </div>
    )
  }
  ```

- [ ] **Step 4: Run tests to confirm they pass**

  From `src/AiObservatory.Web/`:
  ```
  npx vitest run src/components/CollapsiblePanel.test.tsx
  ```

  Expected: PASS — 6 tests

- [ ] **Step 5: Commit**

  ```
  git add src/AiObservatory.Web/src/components/CollapsiblePanel.tsx src/AiObservatory.Web/src/components/CollapsiblePanel.test.tsx
  git commit -m "feat: add CollapsiblePanel component (grid-template-rows animation, localStorage state)"
  ```

---

### Task 5: Refactor CavemanStatsPanel

**Files:**
- Modify: `src/AiObservatory.Web/src/components/CavemanStatsPanel.tsx`

**Interfaces:**
- Consumes: `CollapsiblePanel` (named export from `./CollapsiblePanel`)
- `useCavemanStats()` returns `null | { sessions: number, totalOutputTokens: number, totalEstSavedTokens: number, totalEstSavedUsd: number }`
- `formatGbp(usd: number, rate: number | undefined): string` (from `../lib/currency`)

- [ ] **Step 1: Replace CavemanStatsPanel.tsx in full**

  Full replacement for `src/AiObservatory.Web/src/components/CavemanStatsPanel.tsx`:

  ```tsx
  import { CollapsiblePanel } from './CollapsiblePanel'
  import { Card } from '../design/Card'
  import { useCavemanStats } from '../api/queries'
  import { useUsdToGbp, formatGbp } from '../lib/currency'

  function fmtTokens(n: number): string {
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
    if (n >= 1_000) return `${(n / 1_000).toFixed(0)}k`
    return String(n)
  }

  export default function CavemanStatsPanel() {
    const stats = useCavemanStats()
    const rate = useUsdToGbp()

    const summaryLine = stats && stats.sessions > 0
      ? `${stats.sessions.toLocaleString()} sessions · ${formatGbp(stats.totalEstSavedUsd, rate)} saved`
      : 'No sessions yet'

    const totalTokens = (stats?.totalOutputTokens ?? 0) + (stats?.totalEstSavedTokens ?? 0)
    const compressionPct = totalTokens > 0
      ? Math.round(((stats?.totalEstSavedTokens ?? 0) / totalTokens) * 100)
      : 0

    return (
      <CollapsiblePanel id="caveman" title="Caveman" summary={summaryLine}>
        <div className="summary-cards">
          <Card>
            <div className="card-label">Caveman sessions</div>
            <div className="card-value">{stats?.sessions.toLocaleString() ?? '—'}</div>
          </Card>
          <Card>
            <div className="card-label">Tokens saved (est.)</div>
            <div className="card-value">{stats ? fmtTokens(stats.totalEstSavedTokens) : '—'}</div>
            <div className="card-sub">of {stats ? fmtTokens(totalTokens) : '—'} total output</div>
          </Card>
          <Card>
            <div className="card-label">Compression rate (est.)</div>
            <div className="card-value">{stats ? `${compressionPct}%` : '—'}</div>
            <div className="card-sub">full mode benchmark</div>
          </Card>
          <Card>
            <div className="card-label">Est. value saved</div>
            <div className="card-value card-value--lead">
              {stats ? formatGbp(stats.totalEstSavedUsd, rate) : '—'}
            </div>
          </Card>
        </div>
      </CollapsiblePanel>
    )
  }
  ```

  Changes from the original:
  - Removed `if (!stats || stats.sessions === 0) return null`
  - Removed `style={{ marginTop: 0 }}` from the inner div
  - Added `summaryLine` passed to CollapsiblePanel
  - All card values show `'—'` when `stats` is null (no data yet)

- [ ] **Step 2: Run full test suite**

  From `src/AiObservatory.Web/`:
  ```
  npx vitest run
  ```

  Expected: all pass

- [ ] **Step 3: Commit**

  ```
  git add src/AiObservatory.Web/src/components/CavemanStatsPanel.tsx
  git commit -m "feat: wrap CavemanStatsPanel in CollapsiblePanel, always render"
  ```

---

### Task 6: Dashboard.tsx zone wrapper

**Files:**
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx` (line 91-93)

**Interfaces:**
- Consumes: `.collapsible-panel-zone` CSS class (added in Task 3)
- No import changes needed — `CavemanStatsPanel` is already imported

- [ ] **Step 1: Wrap CavemanStatsPanel in zone div**

  In `src/AiObservatory.Web/src/pages/Dashboard.tsx`, change lines 91-93:

  ```tsx
              <SummaryCards />
              <CavemanStatsPanel />
              <SubscriptionPanel />
  ```

  to:

  ```tsx
              <SummaryCards />
              <div className="collapsible-panel-zone">
                <CavemanStatsPanel />
              </div>
              <SubscriptionPanel />
  ```

- [ ] **Step 2: Commit**

  ```
  git add src/AiObservatory.Web/src/pages/Dashboard.tsx
  git commit -m "feat: wrap collapsible panels in zone div on Dashboard"
  ```

---

### Task 7: Pre-push gate + PR

**Files:** none new

All four checks must pass before pushing. Run from `src/AiObservatory.Web/`.

- [ ] **Step 1: TypeScript**

  ```
  npx tsc -b --noEmit
  ```

  Expected: no errors

- [ ] **Step 2: Lint**

  ```
  npm run lint
  ```

  Expected: no errors (warnings are fine — only errors block CI)

- [ ] **Step 3: Tests**

  ```
  npx vitest run
  ```

  Expected: all pass

- [ ] **Step 4: Build**

  ```
  npx vite build
  ```

  Expected: build succeeds with no errors

- [ ] **Step 5: Push**

  ```
  git push -u origin feat/caveman-panel-and-fix
  ```

- [ ] **Step 6: Create PR**

  ```
  gh pr create --title "feat: caveman collapsible panel + auto-record hook" --body "$(cat <<'EOF'
  ## Summary

  - **Part 1 (global config, not in this repo):** Add `~/.claude/hooks/caveman-stop.ps1` — a synchronous Stop hook registered before `observe-sweep.ps1` in `settings.json`. Ensures `.caveman-history.jsonl` is current before the sweep reads it, fixing the stale-session bug.
  - **Part 2:** New `CollapsiblePanel` component with `grid-template-rows: 0fr → 1fr` CSS animation and per-panel `localStorage` state (`panel-${id}-expanded`, default collapsed).
  - **Part 2:** `CavemanStatsPanel` wrapped in `CollapsiblePanel id="caveman"`; early-return guard removed so the panel always renders (shows "No sessions yet" when empty); summary line `"N sessions · £X saved"` visible in collapsed header.
  - **Part 2:** Dashboard wraps `CavemanStatsPanel` in `.collapsible-panel-zone` div for spacing and future optional panels.

  ## Test plan

  - [ ] First visit (no localStorage): Caveman panel is collapsed
  - [ ] Expand panel → hard-refresh → still expanded
  - [ ] Collapse panel → hard-refresh → collapsed
  - [ ] Keyboard: Tab to header button, Space/Enter toggles; chevron rotates; focus ring visible
  - [ ] Reduced-motion (`prefers-reduced-motion: reduce`): toggle snaps instantly
  - [ ] No data state: panel renders with "No sessions yet" summary line
  - [ ] `npx tsc -b --noEmit` passes
  - [ ] `npm run lint` passes
  - [ ] `npx vitest run` passes (includes 6 CollapsiblePanel unit tests)
  - [ ] `npx vite build` passes
  EOF
  )" --head feat/caveman-panel-and-fix --base main
  ```

---

## Self-review

**Spec coverage check:**

| Spec requirement | Task |
|---|---|
| caveman-stop.ps1 synchronous Stop hook | Task 2 |
| Listed before observe-sweep.ps1 | Task 2 step 3 |
| Reads CLAUDE_SESSION_ID, calls caveman-stats.js --session-file | Task 2 step 1 |
| Fails silently | Task 2 step 1 (`$ErrorActionPreference = 'SilentlyContinue'`, catch) |
| No changes to caveman-stats.js / observe-sweep.ps1 / API | (no such task — correct) |
| CollapsiblePanel component, props id/title/summary/children | Task 4 |
| localStorage persistence, default collapsed | Task 4 |
| grid-template-rows animation | Task 3 + Task 4 |
| button aria-expanded, aria-controls, aria-hidden on body | Task 4 |
| prefers-reduced-motion override | Task 3 |
| Teal dot + title + summary + chevron layout (Option C) | Task 4 |
| CavemanStatsPanel wrapped in CollapsiblePanel id="caveman" | Task 5 |
| Always render, "No sessions yet" when empty | Task 5 |
| summaryLine = "N sessions · £X saved" | Task 5 |
| Remove style={{ marginTop: 0 }} | Task 5 |
| Dashboard zone div .collapsible-panel-zone | Task 6 |
| Full CSS block from spec | Task 3 |
| Feature branch from origin/main | Task 1 |
