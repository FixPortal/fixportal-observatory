import { useMemo } from 'react'
import { Card } from '../design/Card'
import { useAggregates, useBilledReporting, useInsights, AGGREGATES_DAYS_RANGE, dashboardDateRange } from '../api/queries'
import { useUsdToGbp, formatGbp } from '../lib/currency'
import { formatInt } from '../lib/format'
import { observedInputTokens, summarizeCosts } from '../lib/costSummary'
import { InfoPopover } from './InfoPopover'

const notReported = 'Not reported'

// Shared "loading / not-reported / value" rendering for the three USD estimate cards.
function moneyCardValue(loading: boolean, usd: number | null, rate: number): string {
  if (loading) return '…'
  if (usd === null) return notReported
  return formatGbp(usd, rate)
}

function tokensCardValue(loading: boolean, hasAggregates: boolean, totalTokens: number): string {
  if (loading) return '…'
  if (!hasAggregates) return notReported
  if (totalTokens === 0) return '0'
  return `${(totalTokens / 1_000_000).toFixed(1)}M`
}

export default function SummaryCards() {
  const range = useMemo(() => dashboardDateRange(), [])
  const { aggregates, isLoading: aggregatesLoading } = useAggregates(range.from, range.to)
  const { report: billedReporting, isLoading: billedLoading, isError: billedError } = useBilledReporting(range.from, range.to)
  const { insights, isLoading: insightsLoading } = useInsights()
  const rate = useUsdToGbp()
  const summary = summarizeCosts(aggregates, [])
  const billedGbp = billedReporting?.entryCount ? billedReporting.totalGbp : null

  const { totalInputTokens, totalOutputTokens, totalCacheRead } = useMemo(() => aggregates.reduce((total, aggregate) => ({
    totalInputTokens: total.totalInputTokens + observedInputTokens(aggregate),
    totalOutputTokens: total.totalOutputTokens + aggregate.outputTokens,
    totalCacheRead: total.totalCacheRead + aggregate.cacheReadTokens,
  }), { totalInputTokens: 0, totalOutputTokens: 0, totalCacheRead: 0 }), [aggregates])

  const unread = insights.filter(insight => !insight.acknowledged).length
  const totalTokens = totalInputTokens + totalOutputTokens
  const cacheHitRate = totalInputTokens > 0 ? totalCacheRead / totalInputTokens : 0

  return (
    <div className="summary-cards">
      <Card>
        <div className="card-label card-label--row">
          Billed spend · {AGGREGATES_DAYS_RANGE} days
          <InfoPopover id="billed-spend-info" title="Billed spend" className="info-popover--summary">
            <p>Financial ledger entries reported by a provider or recorded as spend. It does not include token-rate estimates or subscription notional value.</p>
            <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
          </InfoPopover>
        </div>
        <div className="card-value card-value--lead">
          {billedError ? 'Unavailable' : moneyCardValue(billedLoading, billedGbp, 1)}
        </div>
        {billedError && <div className="card-sub">Couldn’t load billed spend — try refreshing</div>}
      </Card>
      <Card>
        <div className="card-label card-label--row">
          List-price estimate
          <InfoPopover id="list-price-info" title="List-price estimate" className="info-popover--summary">
            <p>API usage rated from public list prices. USD is converted for display; this is not billed spend.</p>
            <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
          </InfoPopover>
        </div>
        <div className="card-value">{moneyCardValue(aggregatesLoading, summary.listPriceEstimateUsd, rate)}</div>
        <div className="card-sub">USD basis; shown in GBP when reported</div>
        {!aggregatesLoading && summary.unknownListPriceObservations > 0 && (
          <div className="card-sub">{summary.unknownListPriceObservations} observation{summary.unknownListPriceObservations === 1 ? '' : 's'} not reported</div>
        )}
      </Card>
      <Card>
        <div className="card-label card-label--row">
          Provider estimate
          <InfoPopover id="provider-estimate-info" title="Provider estimate" className="info-popover--summary">
            <p>A provider-produced estimate, not an invoice. USD is converted for display.</p>
            <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
          </InfoPopover>
        </div>
        <div className="card-value">{moneyCardValue(aggregatesLoading, summary.providerEstimateUsd, rate)}</div>
        <div className="card-sub">USD basis; shown in GBP when reported</div>
        {!aggregatesLoading && summary.unknownProviderEstimateObservations > 0 && (
          <div className="card-sub">{summary.unknownProviderEstimateObservations} observation{summary.unknownProviderEstimateObservations === 1 ? '' : 's'} not reported</div>
        )}
      </Card>
      <Card>
        <div className="card-label card-label--row">
          Subscription notional
          <InfoPopover id="subscription-notional-info" title="Subscription notional" className="info-popover--summary">
            <p>API-list-price comparison for subscription or local activity. No corresponding money changed hands. USD is converted for display.</p>
            <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
          </InfoPopover>
        </div>
        <div className="card-value">{moneyCardValue(aggregatesLoading, summary.notionalUsd, rate)}</div>
        <div className="card-sub">USD basis; shown in GBP when reported</div>
        {!aggregatesLoading && summary.unknownNotionalObservations > 0 && (
          <div className="card-sub">{summary.unknownNotionalObservations} observation{summary.unknownNotionalObservations === 1 ? '' : 's'} not reported</div>
        )}
      </Card>
      <Card>
        <div className="card-label">Tokens</div>
        <div className="card-value">{tokensCardValue(aggregatesLoading, aggregates.length > 0, totalTokens)}</div>
        {totalTokens > 0 && (
          <div className="card-sub">
            <div>{formatInt(totalInputTokens)} in / {formatInt(totalOutputTokens)} out</div>
            {totalCacheRead > 0 && (
              <div className="card-cache">
                <div>{cacheHitRate.toLocaleString(undefined, { style: 'percent', maximumFractionDigits: 0 })} cache hit</div>
                <div>Cache savings: {summary.cacheSavingsUsd === null ? notReported : `${formatGbp(summary.cacheSavingsUsd, rate)} (server-reported, USD-derived)`}</div>
                {summary.unknownCacheSavingsObservations > 0 && <div>{summary.unknownCacheSavingsObservations} savings observation{summary.unknownCacheSavingsObservations === 1 ? '' : 's'} not reported</div>}
                <InfoPopover id="cache-info" title="Prompt cache" className="info-popover--summary">
                  <p>Cache hit is the share of observed prompt tokens served from cache. Savings are shown only when the server reported them.</p>
                </InfoPopover>
              </div>
            )}
          </div>
        )}
      </Card>
      <Card>
        <div className="card-label">New insights</div>
        <div className="card-value">{insightsLoading ? '…' : unread}</div>
      </Card>
      {summary.unclassifiedUsd !== null && (
        <p className="panel-note summary-cards__unclassified">
          {formatGbp(summary.unclassifiedUsd, rate)} reported under a cost basis the cards above don’t
          categorize — see the Model breakdown table below for the full figure.
        </p>
      )}
    </div>
  )
}
