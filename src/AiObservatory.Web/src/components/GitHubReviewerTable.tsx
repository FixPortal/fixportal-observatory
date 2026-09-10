import type { GitHubReviewerSummary } from '../api/client'
import { StatusBadge } from '../design/StatusBadge'
import { formatTurnaround } from '../lib/duration'

interface GitHubReviewerTableProps {
  reviewers: GitHubReviewerSummary[]
  isLoading?: boolean
  isError?: boolean
}

export default function GitHubReviewerTable({
  reviewers,
  isLoading = false,
  isError = false,
}: GitHubReviewerTableProps) {
  if (isLoading) return <p className="panel-empty">Loading review activity...</p>
  if (isError) return <p className="panel-empty">Couldn’t load review activity.</p>
  if (reviewers.length === 0) return <p className="panel-empty">No reviews submitted in this period.</p>

  return (
    <div className="model-table-wrap">
      <table className="project-table github-table">
        <thead>
          <tr>
            <th>Repo</th>
            <th>Reviewer</th>
            <th>PRs</th>
            <th>Reviews</th>
            <th>Approved</th>
            <th>Changes requested</th>
            <th>First review</th>
          </tr>
        </thead>
        <tbody>
          {reviewers.map((r) => (
            <tr key={`${r.repo}:${r.reviewer}`}>
              <td>{r.repo}</td>
              <td>
                {r.reviewer}
                {r.isBot && (
                  <>
                    {' '}
                    <StatusBadge variant="info" label="Agent" />
                  </>
                )}
              </td>
              <td>{r.pullRequestCount}</td>
              <td>{r.reviewCount}</td>
              <td>{r.approvedCount}</td>
              <td>{r.changesRequestedCount}</td>
              <td>{formatTurnaround(r.avgFirstReviewHours)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
