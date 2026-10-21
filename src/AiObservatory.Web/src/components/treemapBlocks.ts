import type { ProjectActivity } from '../api/client'

export interface TreemapBlock {
  project: string
  activeSeconds: number
  percent: number
  // True only for the synthetic overflow bucket. Distinguishes it from a real project
  // literally named "Other" (which would otherwise collide on the React key and get
  // wrongly disabled / recoloured).
  isOther?: boolean
}

export function buildTreemapBlocks(projects: ProjectActivity[], maxBlocks = 8): TreemapBlock[] {
  const sorted = projects.toSorted((a, b) => b.activeSeconds - a.activeSeconds)
  const total = sorted.reduce((sum, p) => sum + p.activeSeconds, 0)
  if (total === 0) return []

  const top = sorted.slice(0, maxBlocks)
  const rest = sorted.slice(maxBlocks)

  const blocks: TreemapBlock[] = top.map((p) => ({
    project: p.project,
    activeSeconds: p.activeSeconds,
    percent: Math.round((p.activeSeconds / total) * 1000) / 10,
  }))

  if (rest.length > 0) {
    const restSeconds = rest.reduce((sum, p) => sum + p.activeSeconds, 0)
    blocks.push({
      project: 'Other',
      activeSeconds: restSeconds,
      percent: Math.round((restSeconds / total) * 1000) / 10,
      isOther: true,
    })
  }

  return blocks
}
