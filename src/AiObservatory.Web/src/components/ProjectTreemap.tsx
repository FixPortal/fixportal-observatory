import { useMemo } from 'react'
import { ResponsiveContainer, Treemap } from 'recharts'
import type { TreemapNode } from 'recharts/types/chart/Treemap'
import type { ProjectActivity } from '../api/client'
import { buildTreemapBlocks } from './treemapBlocks'
import { formatActiveTime } from '../lib/duration'

// Fixed palette cycled by index — projects are arbitrary strings, unlike
// providerColor's known provider set, so there's no semantic color to key off.
const PALETTE = Array.from({ length: 8 }, (_, index) => `var(--project-${index + 1})`)
// Neutral fill for the overflow bucket so a 9th block doesn't wrap back to the brand
// colour (PALETTE[8 % 8]) and read as the largest project.
const OTHER_COLOR = 'var(--project-other)'

interface Props {
  projects: ProjectActivity[]
  selectedProject: string | null
  onSelectProject: (project: string | null) => void
  isLoading?: boolean
}

export default function ProjectTreemap({ projects, selectedProject, onSelectProject, isLoading = false }: Props) {
  const blocks = useMemo(() => buildTreemapBlocks(projects), [projects])

  if (isLoading) return <div className="chart-skeleton" />
  if (blocks.length === 0) return <p className="panel-empty">No activity data for this period.</p>

  const data = blocks.map((block, index) => ({
    ...block,
    name: block.project,
    value: block.activeSeconds,
    color: block.isOther ? OTHER_COLOR : PALETTE[index % PALETTE.length],
  }))

  const renderCell = (node: TreemapNode) => {
    if (node.depth === 0) return <g />
    const isOther = Boolean(node.isOther)
    const project = String(node.name)
    const activeSeconds = Number(node.activeSeconds)
    const label = `${project} — ${formatActiveTime(activeSeconds)} (${node.percent}%)`
    const select = () => { if (!isOther) onSelectProject(project === selectedProject ? null : project) }
    return (
      <g
        role="button"
        aria-label={label}
        aria-disabled={isOther || undefined}
        tabIndex={isOther ? -1 : 0}
        className={`activity-treemap__cell${project === selectedProject ? ' activity-treemap__cell--selected' : ''}`}
        style={{ background: String(node.color) }}
        onClick={select}
        onKeyDown={event => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault()
            select()
          }
        }}
      >
        <title>{label}</title>
        <rect x={node.x} y={node.y} width={node.width} height={node.height} fill={String(node.color)} />
        {node.width >= 96 && node.height >= 44 && (
          <text x={node.x + 10} y={node.y + 22} className="activity-treemap__label">
            <tspan>{project}</tspan>
            <tspan x={node.x + 10} dy="18" className="activity-treemap__value">{formatActiveTime(activeSeconds)}</tspan>
          </text>
        )}
      </g>
    )
  }

  return (
    <div className="activity-treemap" role="group" aria-label="Project share treemap">
      <ResponsiveContainer width="100%" height={240} initialDimension={{ width: 1000, height: 240 }}>
        <Treemap
          data={data}
          dataKey="value"
          nameKey="name"
          nodeGap={2}
          isAnimationActive={false}
          content={renderCell}
        />
      </ResponsiveContainer>
    </div>
  )
}
