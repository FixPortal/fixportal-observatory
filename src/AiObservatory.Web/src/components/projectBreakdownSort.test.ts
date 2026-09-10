import { test, expect } from 'vitest'
import { filterProjects, sortProjects } from './projectBreakdownSort'
import type { ProjectActivity } from '../api/client'

const projects: ProjectActivity[] = [
  { project: 'fixportal-ai-observatory', sessionCount: 14, activeSeconds: 24000, sharePercent: 52 },
  { project: 'fixportal-quickfixn', sessionCount: 7, activeSeconds: 11400, sharePercent: 25 },
  { project: 'Training', sessionCount: 2, activeSeconds: 2700, sharePercent: 7 },
]

test('filterProjects matches case-insensitively', () => {
  expect(filterProjects(projects, 'FIXPORTAL').map(p => p.project)).toEqual([
    'fixportal-ai-observatory', 'fixportal-quickfixn',
  ])
})

test('filterProjects with blank query returns all projects unchanged', () => {
  expect(filterProjects(projects, '  ')).toEqual(projects)
})

test('filterProjects with no matches returns empty array', () => {
  expect(filterProjects(projects, 'nope')).toEqual([])
})

test('sortProjects by project name ascending', () => {
  const sorted = sortProjects(projects, 'project', 'asc')
  expect(sorted.map(p => p.project)).toEqual(['fixportal-ai-observatory', 'fixportal-quickfixn', 'Training'])
})

test('sortProjects by activeSeconds descending', () => {
  const sorted = sortProjects(projects, 'activeSeconds', 'desc')
  expect(sorted.map(p => p.project)).toEqual(['fixportal-ai-observatory', 'fixportal-quickfixn', 'Training'])
})

test('sortProjects by sessions ascending', () => {
  const sorted = sortProjects(projects, 'sessions', 'asc')
  expect(sorted.map(p => p.project)).toEqual(['Training', 'fixportal-quickfixn', 'fixportal-ai-observatory'])
})

test('sortProjects does not mutate the input array', () => {
  const original = [...projects]
  sortProjects(projects, 'project', 'asc')
  expect(projects).toEqual(original)
})
