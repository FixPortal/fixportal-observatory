import { useState, useMemo } from 'react'
import type { GitHubPr } from '../api/client'
import { filterPrs, sortPrs } from './githubSort'
import type { PrSortField, SortDirection } from './githubSort'
import SearchIcon from '../design/SearchIcon'
import GitHubSortableHeader from './GitHubSortableHeader'

interface GitHubPrTableProps {
  prs: GitHubPr[]
  isLoading?: boolean
  isError?: boolean
}

export default function GitHubPrTable({ prs, isLoading = false, isError = false }: GitHubPrTableProps) {
  const [searchQuery, setSearchQuery] = useState('')
  const [sortField, setSortField] = useState<PrSortField>('createdAt')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const visible = useMemo(
    () => sortPrs(filterPrs(prs, searchQuery), sortField, sortDirection),
    [prs, searchQuery, sortField, sortDirection],
  )

  if (isLoading) return <p className="panel-empty">Loading PR activity...</p>
  if (isError) return <p className="panel-empty">Couldn’t load PR activity.</p>
  if (prs.length === 0) return <p className="panel-empty">No PR activity for this period.</p>

  const handleSort = (field: PrSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('desc') }
  }

  return (
    <>
      <div className="breakdown-controls">
        <div className="breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search PRs..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="breakdown-search"
            aria-label="Search pull requests"
          />
        </div>
      </div>
      {visible.length === 0 ? (
        <p className="panel-empty">No matching PRs found.</p>
      ) : (
        <div className="model-table-wrap">
          <table className="model-table github-table github-pr-table">
          <thead>
            <tr>
              <GitHubSortableHeader field="repo" label="Repo" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <th>Title</th>
              <th>Author</th>
              <th>State</th>
              <GitHubSortableHeader field="createdAt" label="Created" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <th>Merged</th>
              <GitHubSortableHeader field="reviewCount" label="Reviews" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <GitHubSortableHeader field="turnaroundHours" label="Turnaround" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            </tr>
          </thead>
          <tbody>
            {visible.map((p) => (
              <tr key={`${p.repo}#${p.number}`}>
                <td>{p.repo}</td>
                <td className="github-pr-table__title">#{p.number} {p.title}</td>
                <td>{p.author}</td>
                <td>{p.state}</td>
                <td>{new Date(p.createdAt).toLocaleDateString()}</td>
                <td>{p.mergedAt ? new Date(p.mergedAt).toLocaleDateString() : '—'}</td>
                <td>{p.reviewCount}</td>
                <td>{p.turnaroundHours == null ? '—' : `${p.turnaroundHours}h`}</td>
              </tr>
            ))}
          </tbody>
          </table>
        </div>
      )}
    </>
  )
}
