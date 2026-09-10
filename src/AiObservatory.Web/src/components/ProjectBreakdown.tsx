import { useState, useMemo } from 'react'
import type { ProjectActivity } from '../api/client'
import { filterProjects, sortProjects } from './projectBreakdownSort'
import type { ProjectSortField, SortDirection } from './projectBreakdownSort'
import { formatActiveTime } from '../lib/duration'
import SearchIcon from '../design/SearchIcon'

interface SortableHeaderProps {
  field: ProjectSortField
  label: string
  sortField: ProjectSortField
  sortDirection: SortDirection
  onSort: (field: ProjectSortField) => void
}

const SortableHeader = ({ field, label, sortField, sortDirection, onSort }: SortableHeaderProps) => {
  const isActive = sortField === field
  let ariaSort: 'ascending' | 'descending' | 'none' = 'none'
  if (isActive) ariaSort = sortDirection === 'asc' ? 'ascending' : 'descending'
  let indicatorSymbol = '↕'
  if (isActive) indicatorSymbol = sortDirection === 'asc' ? '▲' : '▼'

  // Real <button> inside the <th>: native keyboard/focus semantics instead of a
  // tabIndex/onKeyDown-decorated cell, and the <th> keeps its column-header role.
  return (
    <th className="sortable-header" aria-sort={ariaSort}>
      <button type="button" className="sortable-header__content" onClick={() => onSort(field)}>
        {label}
        <span className={`sort-indicator ${isActive ? 'sort-indicator--active' : ''}`} aria-hidden="true">
          {indicatorSymbol}
        </span>
      </button>
    </th>
  )
}

interface Props {
  projects: ProjectActivity[]
  comparisonProjects?: ProjectActivity[]
  selectedProject: string | null
  onSelectProject: (project: string | null) => void
  isLoading?: boolean
}

function formatComparisonChange(activeSeconds: number, comparisonActiveSeconds: number | null) {
  if (comparisonActiveSeconds == null) return '—'
  if (comparisonActiveSeconds === 0) return activeSeconds > 0 ? 'New' : '—'
  const percentage = Math.round((activeSeconds - comparisonActiveSeconds) / comparisonActiveSeconds * 100)
  return `${percentage >= 0 ? '+' : ''}${percentage}%`
}

export default function ProjectBreakdown({ projects, comparisonProjects, selectedProject, onSelectProject, isLoading = false }: Props) {
  const [searchQuery, setSearchQuery] = useState('')
  const [sortField, setSortField] = useState<ProjectSortField>('activeSeconds')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const comparableProjects = useMemo(() => {
    if (!comparisonProjects) return projects.map(project => ({ ...project, comparisonActiveSeconds: null }))
    const comparison = new Map(comparisonProjects.map(project => [project.project, project.activeSeconds]))
    const currentNames = new Set(projects.map(project => project.project))
    return [
      ...projects.map(project => ({ ...project, comparisonActiveSeconds: comparison.get(project.project) ?? 0 })),
      ...comparisonProjects
        .filter(project => !currentNames.has(project.project))
        .map(project => ({ project: project.project, sessionCount: 0, activeSeconds: 0, sharePercent: 0, comparisonActiveSeconds: project.activeSeconds })),
    ]
  }, [comparisonProjects, projects])

  const visible = useMemo(() => {
    const base = selectedProject ? comparableProjects.filter((p) => p.project === selectedProject) : comparableProjects
    return sortProjects(filterProjects(base, searchQuery), sortField, sortDirection)
  }, [comparableProjects, selectedProject, searchQuery, sortField, sortDirection])

  const maxActiveSeconds = useMemo(
    () => projects.reduce((m, p) => (p.activeSeconds > m ? p.activeSeconds : m), 1),
    [projects],
  )

  if (isLoading) return <div className="chart-skeleton" />
  if (comparableProjects.length === 0) return <p className="panel-empty">No activity data for either period.</p>

  const handleSort = (field: ProjectSortField) => {
    if (sortField === field) {
      setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortField(field)
      setSortDirection('desc')
    }
  }

  return (
    <>
      <div className="filter-row breakdown-controls" role="group" aria-label="Project filters">
        <div className="breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search projects..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="breakdown-search"
            aria-label="Search projects"
          />
        </div>
        {selectedProject && (
          <div className="breakdown-filters">
            <button
              type="button"
              className="filter-chip"
              aria-label={`Clear filter: ${selectedProject}`}
              onClick={() => onSelectProject(null)}
            >
              Filtered: {selectedProject} ✕
            </button>
          </div>
        )}
      </div>

      {visible.length === 0 ? (
        <p className="panel-empty">No matching projects found.</p>
      ) : (
        <div className="model-table-wrap">
          <table className="project-table">
          <thead>
            <tr>
              <SortableHeader field="project" label="Project" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="sessions" label="Sessions" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="activeSeconds" label="Active time" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              {comparisonProjects && <th>Previous active</th>}
              {comparisonProjects && <th>Change</th>}
              <th>Share</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((p) => (
              <tr
                key={p.project}
                className={p.project === selectedProject ? 'project-table__row--selected' : undefined}
              >
                <td>
                  {/* Real <button> for the interactive cell: keeps the <tr>/<td> table
                      semantics intact (a <tr role="button"> flattens the row for AT). */}
                  <button
                    type="button"
                    className="project-table__select"
                    aria-pressed={p.project === selectedProject}
                    onClick={() => onSelectProject(p.project === selectedProject ? null : p.project)}
                  >
                    {p.project}
                  </button>
                </td>
                <td>{p.sessionCount.toLocaleString()}</td>
                <td>{formatActiveTime(p.activeSeconds)}</td>
                {comparisonProjects && <td>{formatActiveTime(p.comparisonActiveSeconds ?? 0)}</td>}
                {comparisonProjects && <td>{formatComparisonChange(p.activeSeconds, p.comparisonActiveSeconds)}</td>}
                <td>
                  <div className="project-table__share">
                    <div className="project-table__bar-track">
                      <div className="project-table__bar" style={{ width: `${(p.activeSeconds / maxActiveSeconds) * 100}%` }} />
                    </div>
                    <span>{p.sharePercent.toFixed(0)}%</span>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
          </table>
        </div>
      )}
    </>
  )
}
