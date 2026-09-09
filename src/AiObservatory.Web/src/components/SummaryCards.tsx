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

interface BilledSpendCardProps {
  loading: boolean
  error: boolean
  billedGbp: number | null
}

function BilledSpendCard({ loading, error, billedGbp }: BilledSpendCardProps) {
  return (
    <Card>
      <div className="card-label card-label--row">
        Billed spend · {AGGREGATES_DAYS_RANGE} days
        <InfoPopover id="billed-spend-info" title="Billed spend" className="info-popover--summary">
          <p>Financial ledger entries reported by a provider or recorded as spend. It does not include token-rate estimates or subscription notional value.</p>
          <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
        </InfoPopover>
      </div>
      <div className="card-value card-value--lead">
        {error ? 'Unavailable' : moneyCardValue(loading, billedGbp, 1)}
      </div>
      {error && <div className="card-sub">Couldn’t load billed spend — try refreshing</div>}
    </Card>
  )
}

interface MoneyEstimateCardProps {
  label: string
  popoverId: string
  description: string
  loading: boolean
  usd: number | null
  rate: number
  unknownObservations: number
}

function MoneyEstimateCard({ label, popoverId, description, loading, usd, rate, unknownObservations }: MoneyEstimateCardProps) {
  return (
    <Card>
      <div className="card-label card-label--row">
        {label}
        <InfoPopover id={popoverId} title={label} className="info-popover--summary">
          <p>{description}</p>
          <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
        </InfoPopover>
      </div>
      <div className="card-value">{moneyCardValue(loading, usd, rate)}</div>
      <div className="card-sub">USD basis; shown in GBP when reported</div>
      {!loading && unknownObservations > 0 && (
        <div className="card-sub">{unknownObservations} observation{unknownObservations === 1 ? '' : 's'} not reported</div>
      )}
    </Card>
  )
}

interface TokensCardProps {
  loading: boolean
  hasAggregates: boolean
  totalInputTokens: number
  totalOutputTokens: number
  totalCacheRead: number
  cacheSavingsUsd: number | null
  unknownCacheSavingsObservations: number
  rate: number
}

function TokensCard({ loading, hasAggregates, totalInputTokens, totalOutputTokens, totalCacheRead, cacheSavingsUsd, unknownCacheSavingsObservations, rate }: TokensCardProps) {
  const totalTokens = totalInputTokens + totalOutputTokens
  const cacheHitRate = totalInputTokens > 0 ? totalCacheRead / totalInputTokens : 0
  return (
    <Card>
      <div className="card-label">Tokens</div>
      <div className="card-value">{tokensCardValue(loading, hasAggregates, totalTokens)}</div>
      {totalTokens > 0 && (
        <div className="card-sub">
          <div>{formatInt(totalInputTokens)} in / {formatInt(totalOutputTokens)} out</div>
          {totalCacheRead > 0 && (
            <div className="card-cache">
              <div>{cacheHitRate.toLocaleString(undefined, { style: 'percent', maximumFractionDigits: 0 })} cache hit</div>
              <div>Cache savings: {cacheSavingsUsd === null ? notReported : `${formatGbp(cacheSavingsUsd, rate)} (server-reported, USD-derived)`}</div>
              {unknownCacheSavingsObservations > 0 && <div>{unknownCacheSavingsObservations} savings observation{unknownCacheSavingsObservations === 1 ? '' : 's'} not reported</div>}
              <InfoPopover id="cache-info" title="Prompt cache" className="info-popover--summary">
                <p>Cache hit is the share of observed prompt tokens served from cache. Savings are shown only when the server reported them.</p>
              </InfoPopover>
            </div>
          )}
        </div>
      )}
    </Card>
  )
}

interface InsightsCardProps {
  loading: boolean
  unread: number
}

function InsightsCard({ loading, unread }: InsightsCardProps) {
  return (
    <Card>
      <div className="card-label">New insights</div>
      <div className="card-value">{loading ? '…' : unread}</div>
    </Card>
  )
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

  return (
    <div className="summary-cards">
      <BilledSpendCard loading={billedLoading} error={billedError} billedGbp={billedGbp} />
      <MoneyEstimateCard
        label="List-price estimate"
        popoverId="list-price-info"
        description="API usage rated from public list prices. USD is converted for display; this is not billed spend."
        loading={aggregatesLoading}
        usd={summary.listPriceEstimateUsd}
        rate={rate}
        unknownObservations={summary.unknownListPriceObservations}
      />
      <MoneyEstimateCard
        label="Provider estimate"
        popoverId="provider-estimate-info"
        description="A provider-produced estimate, not an invoice. USD is converted for display."
        loading={aggregatesLoading}
        usd={summary.providerEstimateUsd}
        rate={rate}
        unknownObservations={summary.unknownProviderEstimateObservations}
      />
      <MoneyEstimateCard
        label="Subscription notional"
        popoverId="subscription-notional-info"
        description="API-list-price comparison for subscription or local activity. No corresponding money changed hands. USD is converted for display."
        loading={aggregatesLoading}
        usd={summary.notionalUsd}
        rate={rate}
        unknownObservations={summary.unknownNotionalObservations}
      />
      <TokensCard
        loading={aggregatesLoading}
        hasAggregates={aggregates.length > 0}
        totalInputTokens={totalInputTokens}
        totalOutputTokens={totalOutputTokens}
        totalCacheRead={totalCacheRead}
        cacheSavingsUsd={summary.cacheSavingsUsd}
        unknownCacheSavingsObservations={summary.unknownCacheSavingsObservations}
        rate={rate}
      />
      <InsightsCard loading={insightsLoading} unread={unread} />
      {summary.unclassifiedUsd !== null && (
        <p className="panel-note summary-cards__unclassified">
          {formatGbp(summary.unclassifiedUsd, rate)} reported under a cost basis the cards above don’t
          categorize — see the Model breakdown table below for the full figure.
        </p>
      )}
    </div>
  )
}
