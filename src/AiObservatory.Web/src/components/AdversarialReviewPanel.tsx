import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useAdversarialReviewRuns, useAdversarialReviewStats } from '../api/queries'
import { deleteAdversarialReviewRun, type AdversarialReviewStats } from '../api/client'
import { participantColor } from '../theme/providerColors'
import { CollapsiblePanel } from './CollapsiblePanel'
import { Button } from '../design/Button'
import { isReadonly } from '../auth/msal'
import { groupRuns, formatSeconds, formatMinutes, bankersRound, type RunGroup } from './adversarialReviewGrouping'
import { filterStats, sortStats, filterRunGroups, filterRunGroupsByStatus, sortRunGroups } from './adversarialReviewSort'
import type { StatsSortField, RunSortField, SortDirection, CompletenessFilter, ValidityFilter } from './adversarialReviewSort'
import GitHubSortableHeader from './GitHubSortableHeader'
import SearchIcon from '../design/SearchIcon'

const PUTATIVE_NOTE = '~ putative cost — estimated from a combined token count (subscription model, no exact per-call billing)'

// recordedAt is a UTC (Z-suffixed) NodaTime Instant. Slicing the string showed the UTC
// wall-clock as if local (an hour early under BST); format it in the viewer's local zone.
const RECORDED_AT_FMT = new Intl.DateTimeFormat(undefined, {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false,
})
function formatRecordedAt(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso.slice(0, 16).replace('T', ' ') : RECORDED_AT_FMT.format(d)
}

// Anthropic reviewer/judge costs are blended estimates from the Agent tool's
// combined token count; OpenAI and Google costs are exact from their APIs.
function isPutativeCost(reviewer: string): boolean {
  return reviewer === 'anthropic'
}

function formatCost(n: number | null | undefined, estimated = false): string {
  if (n == null) return '—'
  return `${estimated ? '~' : ''}$${bankersRound(n, 2).toFixed(2)}`
}
function formatCount(n: number | null | undefined): string {
  if (n == null) return '—'
  return `${bankersRound(n, 0)}`
}
function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1)
}

function RunSummary({ group }: { group: RunGroup }) {
  const t = group.totals
  const hasPutativeCost = group.participants.some(p => isPutativeCost(p.reviewer))
  return (
    <span className="adv-run__summary">
      {group.summary && <span className="adv-run__meta">{formatRecordedAt(group.recordedAt)}</span>}
      {group.repo && <span className="adv-run__meta">{group.repo}</span>}
      {group.chunkCount != null && (
        <span className="adv-run__meta" title="Batched review: a large diff split into cohesive chunks, each a full panel run, summed per participant.">
          aggregate of {group.chunkCount} chunks
        </span>
      )}
      <span className={`adv-run__badge ${group.isComplete ? 'adv-run__badge--ok' : 'adv-run__badge--warn'}`}>
        {group.isComplete ? 'complete' : 'incomplete'}
      </span>
      {!group.isComplete && <span className="adv-run__meta">{group.statusReason}</span>}
      <span className="adv-run__totals">
        raised <b>{t.raised}</b> · accepted <b>{t.accepted}</b> · <b title={hasPutativeCost ? PUTATIVE_NOTE : undefined}>{formatCost(t.costUsd, hasPutativeCost)}</b> · <b>{formatSeconds(t.durationMs)}</b>
      </span>
    </span>
  )
}

interface StatsTableProps {
  stats: AdversarialReviewStats[]
  isLoading: boolean
  isError: boolean
}

function StatsTable({ stats, isLoading, isError }: StatsTableProps) {
  const [query, setQuery] = useState('')
  const [sortField, setSortField] = useState<StatsSortField>('reviewer')
  const [sortDirection, setSortDirection] = useState<SortDirection>('asc')
  const visible = useMemo(
    () => sortStats(filterStats(stats, query), sortField, sortDirection),
    [stats, query, sortField, sortDirection],
  )
  const handleSort = (field: StatsSortField) => {
    if (sortField === field) setSortDirection(prev => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('desc') }
  }

  if (isLoading) return <p className="panel-empty">Loading review stats...</p>
  if (isError) return <p className="panel-empty">Couldn’t load review stats — try refreshing.</p>
  if (stats.length === 0) return <p className="panel-empty">No adversarial-review runs recorded yet.</p>

  return (
    <>
      <div className="breakdown-controls">
        <div className="breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search reviewer or model..."
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            className="breakdown-search"
            aria-label="Search stats by reviewer or model"
          />
        </div>
      </div>
      {visible.length === 0 ? (
        <p className="panel-empty">No matching stats found.</p>
      ) : (
        <div className="model-table-wrap">
          <table className="model-table">
          <thead>
            <tr>
              <GitHubSortableHeader field="reviewer" label="Reviewer" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <GitHubSortableHeader field="model" label="Model" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <GitHubSortableHeader field="runCount" label="Runs" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <GitHubSortableHeader field="avgCostPerRun" label="Avg cost/run" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <GitHubSortableHeader field="avgIssuesRaised" label="Avg raised" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <GitHubSortableHeader field="avgIssuesAccepted" label="Avg accepted" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <GitHubSortableHeader field="avgCostPerAcceptedFinding" label="Avg cost/finding" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <GitHubSortableHeader field="avgDurationMs" label="Avg dur" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            </tr>
          </thead>
          <tbody>
            {visible.map(s => (
              <tr key={`${s.reviewer}|${s.model}|${s.role}`}>
                <td>
                  <span className="model-table__dot" style={{ background: participantColor(s.reviewer, s.role) }} title={`${s.reviewer} ${s.role}`} />
                  {capitalize(s.reviewer)}{s.role === 'judge' && ' (judge)'}
                </td>
                <td>{s.model}</td>
                <td>{s.runCount}</td>
                <td title={isPutativeCost(s.reviewer) ? PUTATIVE_NOTE : undefined}>{formatCost(s.avgCostPerRun, isPutativeCost(s.reviewer))}</td>
                <td>{formatCount(s.avgIssuesRaised)}</td>
                <td>{formatCount(s.avgIssuesAccepted)}</td>
                <td title={isPutativeCost(s.reviewer) ? PUTATIVE_NOTE : undefined}>{formatCost(s.avgCostPerAcceptedFinding, isPutativeCost(s.reviewer))}</td>
                <td>{formatMinutes(s.avgDurationMs)}</td>
              </tr>
            ))}
          </tbody>
          </table>
        </div>
      )}
    </>
  )
}

interface RunsListProps {
  groups: RunGroup[]
  isLoading: boolean
  isError: boolean
}

function RunsList({ groups, isLoading, isError }: RunsListProps) {
  const qc = useQueryClient()
  const [query, setQuery] = useState('')
  const [sortField, setSortField] = useState<RunSortField>('recordedAt')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')
  const [completeness, setCompleteness] = useState<CompletenessFilter>('all')
  const [validity, setValidity] = useState<ValidityFilter>('all')
  const [confirmDeleteRunId, setConfirmDeleteRunId] = useState<string | null>(null)
  const visible = useMemo(
    () => sortRunGroups(filterRunGroupsByStatus(filterRunGroups(groups, query), completeness, validity), sortField, sortDirection),
    [groups, query, completeness, validity, sortField, sortDirection],
  )

  const deleteRun = useMutation({
    mutationFn: deleteAdversarialReviewRun,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['adversarial-review-runs'] })
      qc.invalidateQueries({ queryKey: ['adversarial-review-stats'] })
      setConfirmDeleteRunId(null)
    },
  })

  if (isLoading) return <p className="panel-empty">Loading runs...</p>
  if (isError) return <p className="panel-empty">Couldn’t load runs — try refreshing.</p>
  if (groups.length === 0) return <p className="panel-empty">No runs recorded yet.</p>

  return (
    <>
      <div className="breakdown-controls">
        <div className="breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search repo or summary..."
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            className="breakdown-search"
            aria-label="Search runs by repo or summary"
          />
        </div>
        <select
          value={completeness}
          onChange={(e) => setCompleteness(e.target.value as CompletenessFilter)}
          className="breakdown-sort__field"
          aria-label="Filter runs by completeness"
        >
          <option value="all">All runs</option>
          <option value="complete">Complete only</option>
          <option value="incomplete">Incomplete only</option>
        </select>
        <select
          value={validity}
          onChange={(e) => setValidity(e.target.value as ValidityFilter)}
          className="breakdown-sort__field"
          aria-label="Filter runs by validity"
        >
          <option value="all">All runs</option>
          <option value="valid">Valid only</option>
          <option value="invalid">Invalid only</option>
        </select>
        <div className="breakdown-sort">
          <select
            value={sortField}
            onChange={(e) => setSortField(e.target.value as RunSortField)}
            className="breakdown-sort__field"
            aria-label="Sort runs by"
          >
            <option value="recordedAt">Date</option>
            <option value="repo">Repo</option>
            <option value="raised">Raised</option>
            <option value="accepted">Accepted</option>
            <option value="costUsd">Cost</option>
            <option value="durationMs">Duration</option>
          </select>
          <button
            type="button"
            className="breakdown-sort__direction"
            onClick={() => setSortDirection(prev => (prev === 'asc' ? 'desc' : 'asc'))}
            aria-label={`Sort direction: ${sortDirection === 'asc' ? 'ascending' : 'descending'}`}
          >
            {sortDirection === 'asc' ? '▲' : '▼'}
          </button>
        </div>
      </div>
      {visible.length === 0 ? (
        <p className="panel-empty">No matching runs found.</p>
      ) : (
        visible.map(group => (
          <CollapsiblePanel
            key={group.runId}
            id={`adv-run-${group.runId}`}
            title={group.summary ?? formatRecordedAt(group.recordedAt)}
            summary={<RunSummary group={group} />}
          >
            {!isReadonly && (
              <div className="adv-run__actions">
                {confirmDeleteRunId === group.runId ? (
                  <span>
                    Remove this sweep and every participant's numbers?{' '}
                    <Button
                      variant="danger"
                      onClick={() => deleteRun.mutate(group.runId)}
                      disabled={deleteRun.isPending}
                    >
                      {deleteRun.isPending ? 'Removing…' : 'Confirm remove'}
                    </Button>{' '}
                    <Button variant="ghost" onClick={() => setConfirmDeleteRunId(null)}>Cancel</Button>
                  </span>
                ) : (
                  <Button variant="ghost" onClick={() => setConfirmDeleteRunId(group.runId)}>Remove sweep</Button>
                )}
                {deleteRun.isError && deleteRun.variables === group.runId && (
                  <p className="panel-empty">Couldn’t remove the sweep — try again.</p>
                )}
              </div>
            )}
            <div className="model-table-wrap">
              <table className="model-table">
              <thead>
                <tr>
                  <th>Reviewer</th><th>Model</th><th>Raised</th><th>Accepted</th>
                  <th>Cost</th><th>Cost/finding</th><th>Duration</th>
                </tr>
              </thead>
              <tbody>
                {group.participants.map(p => (
                  <tr key={p.id}>
                    <td>
                      <span className="model-table__dot" style={{ background: participantColor(p.reviewer, p.role) }} title={`${p.reviewer} ${p.role}`} />
                      {capitalize(p.reviewer)}{p.role === 'judge' && ' (judge)'}
                    </td>
                    <td>{p.model}</td>
                    <td>{p.role === 'judge' ? '—' : p.issuesRaised}</td>
                    <td>{p.role === 'judge' ? '—' : p.issuesAccepted}</td>
                    <td title={isPutativeCost(p.reviewer) ? PUTATIVE_NOTE : undefined}>{formatCost(p.costUsd, isPutativeCost(p.reviewer))}</td>
                    <td title={isPutativeCost(p.reviewer) ? PUTATIVE_NOTE : undefined}>{p.role === 'judge' ? '—' : formatCost(p.costPerAcceptedFinding, isPutativeCost(p.reviewer))}</td>
                    <td>{formatSeconds(p.reviewDurationMs)}</td>
                  </tr>
                ))}
              </tbody>
              </table>
            </div>
          </CollapsiblePanel>
        ))
      )}
    </>
  )
}

export default function AdversarialReviewPanel() {
  const { stats, isError: statsError, isLoading: statsLoading } = useAdversarialReviewStats()
  const { runs, isError: runsError, isLoading: runsLoading } = useAdversarialReviewRuns()
  const groups = useMemo(() => groupRuns(runs), [runs])

  return (
    <div className="adv-review-panel">
      <div className="panel">
        <div className="panel-title">Stats by reviewer &amp; model</div>
        <StatsTable stats={stats} isLoading={statsLoading} isError={statsError} />
      </div>

      <div className="panel">
        <div className="panel-title">Recent runs</div>
        <RunsList groups={groups} isLoading={runsLoading} isError={runsError} />
      </div>
      {(stats.some(s => isPutativeCost(s.reviewer)) || groups.some(g => g.participants.some(p => isPutativeCost(p.reviewer)))) && (
        <p className="panel-note">{PUTATIVE_NOTE}</p>
      )}
    </div>
  )
}
