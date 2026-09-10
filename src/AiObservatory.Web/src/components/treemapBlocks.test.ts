import { test, expect } from 'vitest'
import { buildTreemapBlocks } from './treemapBlocks'
import type { ProjectActivity } from '../api/client'

const make = (project: string, activeSeconds: number): ProjectActivity =>
  ({ project, activeSeconds, sessionCount: 1, sharePercent: 0 })

test('returns empty array when there is no activity', () => {
  expect(buildTreemapBlocks([])).toEqual([])
})

test('returns all projects sorted descending when under the block cap', () => {
  const projects = [make('a', 100), make('b', 300), make('c', 200)]
  const blocks = buildTreemapBlocks(projects, 8)
  expect(blocks.map((b) => b.project)).toEqual(['b', 'c', 'a'])
})

test('computes percent of total active time', () => {
  const projects = [make('a', 25), make('b', 75)]
  const blocks = buildTreemapBlocks(projects, 8)
  expect(blocks.find((b) => b.project === 'a')!.percent).toBe(25)
  expect(blocks.find((b) => b.project === 'b')!.percent).toBe(75)
})

test('collapses projects beyond the cap into an Other bucket', () => {
  const projects = [make('a', 500), make('b', 400), make('c', 50), make('d', 30), make('e', 20)]
  const blocks = buildTreemapBlocks(projects, 2)
  expect(blocks.map((b) => b.project)).toEqual(['a', 'b', 'Other'])
  expect(blocks.find((b) => b.project === 'Other')!.activeSeconds).toBe(100) // 50+30+20
})

test('does not add an Other bucket when project count is within the cap', () => {
  const projects = [make('a', 100), make('b', 200)]
  const blocks = buildTreemapBlocks(projects, 8)
  expect(blocks.some((b) => b.project === 'Other')).toBe(false)
})
