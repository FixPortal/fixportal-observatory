import { useMemo, useState } from 'react'
import type { DailyAggregate } from '../api/client'
import { useAggregates } from '../api/queries'
import {
  PROVIDER_ORDER,
  costBasisDisplayName,
  providerDisplayName,
  sourceDisplayName,
  usageScopeDisplayName,
} from '../config/providers'
import SearchIcon from '../design/SearchIcon'
import { formatGbp, useUsdToGbp } from '../lib/currency'
import { observedTokens } from '../lib/costSummary'
import { providerColor } from '../theme/providerColors'

/* eslint-disable react-refresh/only-export-components -- focused tests exercise the table's pure evidence grouping */

type SortField = 'model' | 'requests' | 'cost' | 'cpm'
type SortDirection = 'asc' | 'desc'

export interface ModelRow {
  key: string
  provider: string
  providerLabel: string
  model: string
  sourceId: string
  sourceLabel: string
  usageScope: string
  scopeLabel: string
  costBasis: string
  basisLabel: string
  cost: number
  requests: number
  tokens: number
  unknownCostCount: number
  costReported: boolean
  cpm: number | null
}

const grainKey = (parts: string[]) => parts.map(part => `${part.length}:${part}`).join('')

export function groupModelRows(aggregates: DailyAggregate[]): ModelRow[] {
  const grouped = new Map<string, Omit<ModelRow, 'costReported' | 'cpm'>>()
  for (const aggregate of aggregates) {
    const key = grainKey([aggregate.provider, aggregate.model, aggregate.sourceId, aggregate.usageScope, aggregate.costBasis])
    const row = grouped.get(key) ?? {
      key,
      provider: aggregate.provider,
      providerLabel: providerDisplayName(aggregate.provider),
      model: aggregate.model,
      sourceId: aggregate.sourceId,
      sourceLabel: sourceDisplayName(aggregate.sourceId),
      usageScope: aggregate.usageScope,
      scopeLabel: usageScopeDisplayName(aggregate.usageScope),
      costBasis: aggregate.costBasis,
      basisLabel: costBasisDisplayName(aggregate.costBasis),
      cost: 0,
      requests: 0,
      tokens: 0,
      unknownCostCount: 0,
    }
    row.cost += aggregate.costUsd
    row.requests += aggregate.requestCount
    row.tokens += observedTokens(aggregate)
    row.unknownCostCount += aggregate.unknownCostCount
    grouped.set(key, row)
  }

  return [...grouped.values()].map(row => {
    const costReported = row.unknownCostCount < row.requests
    return { ...row, costReported, cpm: costReported && row.unknownCostCount === 0 && row.tokens > 0 ? (row.cost / row.tokens) * 1_000_000 : null }
  })
}

interface SortableHeaderProps {
  field: SortField
  label: string
  hint?: string
  sortField: SortField
  sortDirection: SortDirection
  onSort: (field: SortField) => void
}

function SortableHeader({ field, label, hint, sortField, sortDirection, onSort }: SortableHeaderProps) {
  const isActive = sortField === field
  const ariaSort = !isActive ? 'none' : sortDirection === 'asc' ? 'ascending' : 'descending'
  const indicator = !isActive ? '↕' : sortDirection === 'asc' ? '▲' : '▼'
  return (
    <th className="sortable-header" aria-sort={ariaSort}>
      <button
        type="button"
        className="sortable-header__content"
        onClick={() => onSort(field)}
        title={hint}
        aria-label={`Sort by ${label}, currently ${ariaSort}`}
      >
        {label}<span className={`sort-indicator ${isActive ? 'sort-indicator--active' : ''}`} aria-hidden="true">{indicator}</span>
      </button>
    </th>
  )
}

const providerOrder = (provider: string) => {
  const index = PROVIDER_ORDER.findIndex(known => known === provider)
  return index < 0 ? Number.MAX_SAFE_INTEGER : index
}

export default function ModelBreakdown() {
  const { aggregates, isLoading: aggregatesLoading } = useAggregates()
  const rate = useUsdToGbp()
  const [searchQuery, setSearchQuery] = useState('')
  const [selectedProvider, setSelectedProvider] = useState('all')
  const [sortField, setSortField] = useState<SortField>('cost')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')
  const rows = useMemo(() => groupModelRows(aggregates), [aggregates])

  const providers = useMemo(() => [...new Set(rows.map(row => row.provider))].toSorted((a, b) =>
    providerOrder(a) - providerOrder(b) || providerDisplayName(a).localeCompare(providerDisplayName(b))), [rows])

  const visibleRows = useMemo(() => {
    const search = searchQuery.trim().toLowerCase()
    return rows
      .filter(row => selectedProvider === 'all' || row.provider === selectedProvider)
      .filter(row => !search || [row.model, row.providerLabel, row.sourceLabel, row.scopeLabel, row.basisLabel]
        .some(value => value.toLowerCase().includes(search)))
      .toSorted((a, b) => {
        const values: Record<SortField, [string | number, string | number]> = {
          model: [a.model, b.model], requests: [a.requests, b.requests],
          cost: [a.costReported ? a.cost : Number.NEGATIVE_INFINITY, b.costReported ? b.cost : Number.NEGATIVE_INFINITY],
          cpm: [a.cpm ?? Number.NEGATIVE_INFINITY, b.cpm ?? Number.NEGATIVE_INFINITY],
        }
        const [left, right] = values[sortField]
        const comparison = typeof left === 'string' ? left.localeCompare(String(right)) : left - Number(right)
        return sortDirection === 'asc' ? comparison : -comparison
      })
  }, [rows, searchQuery, selectedProvider, sortDirection, sortField])

  const maxCost = useMemo(() => Math.max(...rows.filter(row => row.costReported).map(row => row.cost), Number.EPSILON), [rows])

  if (aggregatesLoading) return <div className="chart-skeleton" />
  if (rows.length === 0) return <p className="panel-empty">No usage data for this period.</p>

  const sort = (field: SortField) => {
    if (sortField === field) setSortDirection(current => current === 'asc' ? 'desc' : 'asc')
    else { setSortField(field); setSortDirection(field === 'model' ? 'asc' : 'desc') }
  }

  return (
    <>
      <div className="breakdown-controls">
        <div className="breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search evidence..."
            value={searchQuery}
            onChange={event => setSearchQuery(event.target.value)}
            className="breakdown-search"
            aria-label="Search models"
          />
        </div>
        <div className="breakdown-filters" aria-label="Filter by provider" role="group">
          <button type="button" onClick={() => setSelectedProvider('all')} className="filter-chip" aria-pressed={selectedProvider === 'all'}>All</button>
          {providers.map(provider => (
            <button
              key={provider}
              type="button"
              onClick={() => setSelectedProvider(provider)}
              className="filter-chip"
              aria-pressed={selectedProvider === provider}
            >
              <span className="filter-chip__dot" style={{ background: providerColor(provider) }} aria-hidden="true" />
              {providerDisplayName(provider)}
            </button>
          ))}
        </div>
      </div>

      {visibleRows.length === 0 ? <p className="panel-empty">No matching models found.</p> : (
        <div className="model-table-wrap">
          <table className="model-table">
            <thead><tr>
              <SortableHeader field="model" label="Model" sortField={sortField} sortDirection={sortDirection} onSort={sort} />
              <th>Source</th><th>Scope</th><th>Basis</th>
              <SortableHeader field="requests" label="Requests" sortField={sortField} sortDirection={sortDirection} onSort={sort} />
              <SortableHeader field="cost" label="Cost" sortField={sortField} sortDirection={sortDirection} onSort={sort} />
              <SortableHeader field="cpm" label="Cost / 1M" hint="Cost per 1 million input and output tokens" sortField={sortField} sortDirection={sortDirection} onSort={sort} />
            </tr></thead>
            <tbody>{visibleRows.map(row => (
              <tr key={row.key}>
                <td>
                  <span className="model-table__identity">
                    <span className="model-table__dot" style={{ background: providerColor(row.provider) }} aria-hidden="true" />
                    <span>{row.model}</span>
                  </span>
                  <span className="model-table__provider">{row.providerLabel}</span>
                </td>
                <td>{row.sourceLabel}</td><td>{row.scopeLabel}</td><td>{row.basisLabel}</td>
                <td>{row.requests.toLocaleString()}</td>
                <td>
                  {row.costReported ? <>{formatGbp(row.cost, rate)}<div className="bar-mini" style={{ width: `${(row.cost / maxCost) * 100}%` }} /></> : 'Not reported'}
                  {row.costReported && row.unknownCostCount > 0 && <span className="model-table__qualification">{row.unknownCostCount.toLocaleString()} {row.unknownCostCount === 1 ? 'observation' : 'observations'} not reported</span>}
                </td>
                <td>{row.cpm == null ? 'Not reported' : formatGbp(row.cpm, rate)}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
    </>
  )
}
