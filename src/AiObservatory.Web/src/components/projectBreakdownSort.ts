import type { ProjectActivity } from '../api/client'

export type ProjectSortField = 'project' | 'sessions' | 'activeSeconds'
export type SortDirection = 'asc' | 'desc'

export function filterProjects<T extends ProjectActivity>(projects: T[], query: string): T[] {
  const q = query.trim().toLowerCase()
  if (!q) return projects
  return projects.filter((p) => p.project.toLowerCase().includes(q))
}

export function sortProjects<T extends ProjectActivity>(
  projects: T[], field: ProjectSortField, direction: SortDirection,
): T[] {
  return projects.toSorted((a, b) => {
    let comparison: number
    if (field === 'project') comparison = a.project.localeCompare(b.project)
    else if (field === 'sessions') comparison = a.sessionCount - b.sessionCount
    else comparison = a.activeSeconds - b.activeSeconds
    return direction === 'asc' ? comparison : -comparison
  })
}
