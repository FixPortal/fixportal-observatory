import { useState, useMemo } from 'react'
import type { SpendCategory, SpendEntry, SpendVendor } from '../api/client'
import { gbp, formatCurrency } from '../lib/currency'
import { Button } from '../design/Button'
import GitHubSortableHeader from './GitHubSortableHeader'
import type { SortDirection } from './githubSort'

type SortKey = 'occurredOn' | 'vendor' | 'category' | 'amountGbp'

interface Props {
  entries: SpendEntry[]
  categories: SpendCategory[]
  vendors: SpendVendor[]
  onDelete: (id: string) => void
  canEdit: boolean
  /** True while a delete is in flight, so a fast double-click can't fire a second
   * DELETE for a row that has already gone -- which would 404 after a successful
   * delete and surface a spurious failure message. */
  isDeleting?: boolean
}

/** Region 6. Sortable on any column; the rows are already filtered by SpendPage. */
export default function SpendLedgerTable({ entries, categories, vendors, onDelete, canEdit, isDeleting = false }: Props) {
  const [sortField, setSortField] = useState<SortKey>('occurredOn')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null)

  const categoryName = useMemo(
    () => new Map(categories.map(c => [c.id, c.displayName])), [categories])
  const vendorName = useMemo(
    () => new Map(vendors.map(v => [v.id, v.displayName])), [vendors])

  const sorted = useMemo(() => {
    const value = (e: SpendEntry): string | number => {
      if (sortField === 'vendor') return vendorName.get(e.vendorId) ?? ''
      if (sortField === 'category') return categoryName.get(e.categoryId) ?? ''
      if (sortField === 'amountGbp') return e.amountGbp
      return e.occurredOn
    }
    return entries.toSorted((a, b) => {
      const av = value(a), bv = value(b)
      const cmp = typeof av === 'number' && typeof bv === 'number'
        ? av - bv
        : String(av).localeCompare(String(bv))
      return sortDirection === 'asc' ? cmp : -cmp
    })
  }, [entries, sortField, sortDirection, categoryName, vendorName])

  const handleSort = (field: SortKey) => {
    if (sortField === field) setSortDirection(prev => prev === 'asc' ? 'desc' : 'asc')
    else { setSortField(field); setSortDirection('desc') }
  }

  if (entries.length === 0) {
    return <p className="spend-ledger__empty" role="status">No spend recorded for this filter.</p>
  }

  return (
    <div className="spend-ledger__wrapper">
      <table className="spend-ledger" aria-label="Spend ledger">
        <caption className="visually-hidden">Every billed spend entry matching the current filter</caption>
        <thead>
          <tr>
            <GitHubSortableHeader field="occurredOn" label="Date" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            <GitHubSortableHeader field="vendor" label="Vendor" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            <GitHubSortableHeader field="category" label="Category" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            <th>Description</th>
            <GitHubSortableHeader field="amountGbp" label="Amount" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} className="spend-ledger__num" />
            <th>Source</th>
            {canEdit && <th><span className="visually-hidden">Actions</span></th>}
          </tr>
        </thead>
        <tbody>
          {sorted.map(e => (
            <tr key={e.id}>
              <td>{e.occurredOn}</td>
              <td>{vendorName.get(e.vendorId) ?? '—'}</td>
              <td>{categoryName.get(e.categoryId) ?? '—'}</td>
              <td>{e.description ?? ''}</td>
              {/* Refunds are negative amounts, so Intl already renders the minus sign. The
                  extra class colours it and the visually-hidden word states it outright —
                  a lone "-" is easy to miss when scanning a column of figures, and reading
                  a refund as a charge is exactly the misread this row must not invite. */}
              <td className={`spend-ledger__num${e.amountGbp < 0 ? ' spend-ledger__num--refund' : ''}`}>
                {e.amountGbp < 0 && <span className="visually-hidden">Refund: </span>}
                {gbp(e.amountGbp)}
                {e.currency !== 'GBP' && (
                  <span className="spend-ledger__native"> ({formatCurrency(e.amount, e.currency)})</span>
                )}
              </td>
              <td>{e.source}</td>
              {canEdit && (
                <td>
                  {confirmDeleteId === e.id ? (
                    <span className="budget-rules__actions">
                      <Button
                        variant="danger"
                        size="sm"
                        onClick={() => { onDelete(e.id); setConfirmDeleteId(null) }}
                        disabled={isDeleting}
                        aria-label={`Confirm delete entry from ${e.occurredOn}`}
                      >
                        {isDeleting ? 'Removing…' : 'Confirm'}
                      </Button>
                      <Button variant="ghost" size="sm" onClick={() => setConfirmDeleteId(null)}>Cancel</Button>
                    </span>
                  ) : (
                    <Button
                      variant="danger"
                      size="sm"
                      onClick={() => setConfirmDeleteId(e.id)}
                      disabled={isDeleting}
                      aria-label={`Delete entry from ${e.occurredOn}`}
                    >
                      Delete
                    </Button>
                  )}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
