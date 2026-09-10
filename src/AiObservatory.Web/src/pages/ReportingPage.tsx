import { lazy, Suspense } from 'react'
import SpendRangeControls from '../components/SpendRangeControls'
import ReportingCards from '../components/ReportingCards'
import BudgetRulesPanel from '../components/BudgetRulesPanel'
import { useDateRange } from '../lib/dateRange'
import { localDate, useBilledReporting } from '../api/queries'

const BilledSpendChart = lazy(() => import('../components/BilledSpendChart'))
const BilledVendorSplit = lazy(() => import('../components/BilledVendorSplit'))

export default function ReportingPage() {
  const {
    from, to, preset, setPreset, setCustom,
    comparisonFrom, comparisonTo, comparisonMode, setComparison, compareWithPrevious,
  } = useDateRange()
  const { report, isLoading, isError } = useBilledReporting(from, to)
  const comparison = useBilledReporting(comparisonFrom, comparisonTo)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`
  const comparisonLabel = comparisonMode === 'previous' ? 'previous period' : 'comparison period'

  const loadError = isError || comparison.isError

  let charts
  if (isLoading || comparison.isLoading) {
    charts = <div className="chart-skeleton" />
  } else if ((report?.entryCount ?? 0) === 0 && (comparison.report?.entryCount ?? 0) === 0) {
    charts = <div className="panel"><p className="panel-empty">No billed spend reported for either period.</p></div>
  } else {
    charts = (
      <div className="main-grid">
        <div className="panel spend-chart">
          <div className="panel-title">Billed spend — {rangeLabel}</div>
          <Suspense fallback={<div className="chart-skeleton" />}>
            <BilledSpendChart
              data={report?.dailySeries ?? []}
              range={{ from, to }}
              comparisonData={comparison.report?.dailySeries ?? []}
              comparisonRange={{ from: comparisonFrom, to: comparisonTo }}
              comparisonLabel={comparisonMode === 'previous' ? 'Previous period' : 'Comparison period'}
            />
          </Suspense>
        </div>
        <div className="panel">
          <div className="panel-title">Billed spend by vendor</div>
          {!report || report.entryCount === 0 ? (
            <p className="panel-empty">No billed spend reported for the selected period.</p>
          ) : (
            <Suspense fallback={<div className="chart-skeleton" />}>
              <BilledVendorSplit data={report.vendorSeries} />
            </Suspense>
          )}
        </div>
      </div>
    )
  }

  return (
    <div className="reporting-page">
      <SpendRangeControls
        from={from} to={to} preset={preset}
        comparisonFrom={comparisonFrom} comparisonTo={comparisonTo} comparisonMode={comparisonMode}
        onPreset={setPreset} onCustom={setCustom} onComparison={setComparison}
        onPreviousComparison={compareWithPrevious}
      />
      {/* Inline banner, not an early return: the range controls stay mounted so a range
          the API rejects can be edited, and BudgetRulesPanel does not depend on billed
          reporting at all. */}
      {loadError ? (
        <div className="error-banner" role="alert">Couldn’t load billed reporting. Check the API service and try refreshing.</div>
      ) : (
        <>
          <ReportingCards report={report} comparisonReport={comparison.report} comparisonLabel={comparisonLabel} />
          {charts}
        </>
      )}
      <BudgetRulesPanel />
    </div>
  )
}
