import { useState, lazy, Suspense } from 'react'
import SpendRangeControls from '../components/SpendRangeControls'
import ProjectBreakdown from '../components/ProjectBreakdown'
import { useDateRange } from '../lib/dateRange'
import { useActivityByProject } from '../api/queries'

const ActivityTrendChart = lazy(() => import('../components/ActivityTrendChart'))
const ProjectTreemap = lazy(() => import('../components/ProjectTreemap'))

export default function ActivityPage() {
  const {
    from, to, preset, setPreset, setCustom,
    comparisonFrom, comparisonTo, comparisonMode, setComparison, compareWithPrevious,
  } = useDateRange()
  const primary = useActivityByProject(from, to)
  const comparison = useActivityByProject(comparisonFrom, comparisonTo)
  const [selectedProject, setSelectedProject] = useState<string | null>(null)
  const comparisonLabel = comparisonMode === 'previous' ? 'Previous period' : 'Comparison period'

  return (
    // Reuses the Reporting page's layout class — it's generic (flex column +
    // spacing), not Reporting-specific, so there's nothing to extract.
    <div className="reporting-page">
      <SpendRangeControls
        from={from}
        to={to}
        preset={preset}
        comparisonFrom={comparisonFrom}
        comparisonTo={comparisonTo}
        comparisonMode={comparisonMode}
        onPreset={setPreset}
        onCustom={setCustom}
        onComparison={setComparison}
        onPreviousComparison={compareWithPrevious}
      />
      {(primary.isError || comparison.isError) && (
        <div className="error-banner" role="alert">
          Couldn’t load activity data. It may be unavailable or you may not be authorised — try refreshing.
        </div>
      )}
      <div className="panel">
        <div className="panel-title">Active time</div>
        <Suspense fallback={<div className="chart-skeleton" />}>
          <ActivityTrendChart
            from={from}
            to={to}
            comparisonFrom={comparisonFrom}
            comparisonTo={comparisonTo}
            comparisonLabel={comparisonLabel}
          />
        </Suspense>
      </div>
      <div className="panel">
        <div className="panel-title">Project share</div>
        <Suspense fallback={<div className="chart-skeleton" />}>
          <ProjectTreemap projects={primary.projects} selectedProject={selectedProject} onSelectProject={setSelectedProject} isLoading={primary.isLoading} />
        </Suspense>
      </div>
      <div className="panel">
        <div className="panel-title">Time by project</div>
        <ProjectBreakdown
          projects={primary.projects}
          comparisonProjects={comparison.projects}
          selectedProject={selectedProject}
          onSelectProject={setSelectedProject}
          isLoading={primary.isLoading || comparison.isLoading}
        />
      </div>
    </div>
  )
}
