import { test, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import ProjectTreemap from './ProjectTreemap'
import type { ProjectActivity } from '../api/client'

const projects: ProjectActivity[] = [
  { project: 'fixportal-ai-observatory', sessionCount: 14, activeSeconds: 24000, sharePercent: 67 },
  { project: 'Training', sessionCount: 2, activeSeconds: 2700, sharePercent: 8 },
]

test('renders a block per project', () => {
  render(<ProjectTreemap projects={projects} selectedProject={null} onSelectProject={vi.fn()} />)
  expect(screen.getByRole('group', { name: 'Project share treemap' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: /fixportal-ai-observatory/ })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: /Training/ })).toBeInTheDocument()
})

test('clicking a block selects that project', () => {
  const onSelectProject = vi.fn()
  render(<ProjectTreemap projects={projects} selectedProject={null} onSelectProject={onSelectProject} />)
  fireEvent.click(screen.getByRole('button', { name: /Training/ }))
  expect(onSelectProject).toHaveBeenCalledWith('Training')
})

test('clicking the already-selected block deselects it', () => {
  const onSelectProject = vi.fn()
  render(<ProjectTreemap projects={projects} selectedProject="Training" onSelectProject={onSelectProject} />)
  fireEvent.click(screen.getByRole('button', { name: /Training/ }))
  expect(onSelectProject).toHaveBeenCalledWith(null)
})

test('shows a loading skeleton instead of the empty state while the fetch is pending', () => {
  render(<ProjectTreemap projects={[]} selectedProject={null} onSelectProject={vi.fn()} isLoading />)
  expect(screen.queryByText('No activity data for this period.')).not.toBeInTheDocument()
  expect(screen.queryByRole('group', { name: 'Project share treemap' })).not.toBeInTheDocument()
})

test('shows empty state when there is no activity', () => {
  render(<ProjectTreemap projects={[]} selectedProject={null} onSelectProject={vi.fn()} />)
  expect(screen.getByText('No activity data for this period.')).toBeInTheDocument()
})

test('uses the project palette for the overflow bucket', () => {
  const manyProjects = Array.from({ length: 9 }, (_, index) => ({
    project: `project-${index + 1}`,
    sessionCount: 1,
    activeSeconds: 9 - index,
    sharePercent: 0,
  }))

  render(<ProjectTreemap projects={manyProjects} selectedProject={null} onSelectProject={vi.fn()} />)

  expect(screen.getByRole('button', { name: /Other/ })).toHaveStyle({ background: 'var(--project-other)' })
})
