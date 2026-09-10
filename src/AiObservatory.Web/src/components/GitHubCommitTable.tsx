import { useState, useMemo } from 'react'
import type { GitHubCommitSummary } from '../api/client'
import { sortCommitSummaries } from './githubSort'
import type { CommitSortField, SortDirection } from './githubSort'
import GitHubSortableHeader from './GitHubSortableHeader'

interface GitHubCommitTableProps {
  summary: GitHubCommitSummary[]
  isLoading?: boolean
  isError?: boolean
}

export default function GitHubCommitTable({ summary, isLoading = false, isError = false }: GitHubCommitTableProps) {
  const [sortField, setSortField] = useState<CommitSortField>('commitCount')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const visible = useMemo(
    () => sortCommitSummaries(summary, sortField, sortDirection),
    [summary, sortField, sortDirection],
  )

  if (isLoading) return <p className="panel-empty">Loading commit activity...</p>
  if (isError) return <p className="panel-empty">Couldn’t load commit activity.</p>
  if (summary.length === 0) return <p className="panel-empty">No commit activity for this period.</p>

  const handleSort = (field: CommitSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('desc') }
  }

  return (
    <div className="model-table-wrap">
      <table className="project-table github-table">
        <thead>
          <tr>
            <GitHubSortableHeader field="repo" label="Repo" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            <GitHubSortableHeader field="commitCount" label="Commits" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            <th>Churn</th>
          </tr>
        </thead>
        <tbody>
          {visible.map((s) => (
            <tr key={s.repo}>
              <td>{s.repo}</td>
              <td>{s.commitCount}</td>
              <td>+{s.additions} / -{s.deletions}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
