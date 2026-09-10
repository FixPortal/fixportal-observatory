import { useState, useMemo } from 'react'
import type { CSSProperties } from 'react'
import type { GitHubCiSummary } from '../api/client'
import { sortCiSummaries } from './githubSort'
import type { CiSortField, SortDirection } from './githubSort'
import GitHubSortableHeader from './GitHubSortableHeader'

const SUCCESS_RATE_WARN_THRESHOLD = 80

// Visually-hidden but screen-reader-visible text, so the low-success-rate warning
// isn't conveyed by red text alone (WCAG 1.4.1 use-of-color).
const srOnlyStyle: CSSProperties = {
  position: 'absolute',
  width: 1,
  height: 1,
  padding: 0,
  margin: -1,
  overflow: 'hidden',
  clip: 'rect(0, 0, 0, 0)',
  whiteSpace: 'nowrap',
  border: 0,
}

interface GitHubCiTableProps {
  ci: GitHubCiSummary[]
  isLoading?: boolean
  isError?: boolean
}

export default function GitHubCiTable({ ci, isLoading = false, isError = false }: GitHubCiTableProps) {
  const [sortField, setSortField] = useState<CiSortField>('successRate')
  const [sortDirection, setSortDirection] = useState<SortDirection>('asc')

  const visible = useMemo(
    () => sortCiSummaries(ci, sortField, sortDirection),
    [ci, sortField, sortDirection],
  )

  if (isLoading) return <p className="panel-empty">Loading CI activity...</p>
  if (isError) return <p className="panel-empty">Couldn’t load CI activity.</p>
  if (ci.length === 0) return <p className="panel-empty">No CI activity for this period.</p>

  const handleSort = (field: CiSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('asc') }
  }

  return (
    <div className="model-table-wrap">
      <table className="project-table github-table">
        <thead>
          <tr>
            <GitHubSortableHeader field="repo" label="Repo" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            <th>Workflow</th>
            <GitHubSortableHeader field="totalRuns" label="Runs" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            <th>Failed</th>
            <GitHubSortableHeader field="successRate" label="Success rate" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
          </tr>
        </thead>
        <tbody>
          {visible.map((c) => (
            <tr key={`${c.repo}:${c.workflowName}`}>
              <td>{c.repo}</td>
              <td>{c.workflowName}</td>
              <td>{c.totalRuns}</td>
              <td>{c.failedRuns}</td>
              <td style={{ color: c.successRate < SUCCESS_RATE_WARN_THRESHOLD ? 'var(--bad-text)' : undefined }}>
                {c.successRate.toFixed(0)}%
                {c.successRate < SUCCESS_RATE_WARN_THRESHOLD && <span style={srOnlyStyle}> (low success rate)</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
