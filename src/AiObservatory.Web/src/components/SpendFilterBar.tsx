import type { SpendCategory, SpendVendor } from '../api/client'
import { Button } from '../design/Button'

interface Props {
  categories: SpendCategory[]
  vendors: SpendVendor[]
  categoryId?: string
  vendorId?: string
  onCategoryChange: (id: string | undefined) => void
  onVendorChange: (id: string | undefined) => void
  onAddEntry: () => void
  onManageCatalog: () => void
  rangeLabel?: string
  canEdit: boolean
}

/** Region 1. One filter state, lifted to SpendPage, drives every other region. */
export default function SpendFilterBar({
  categories, vendors, categoryId, vendorId,
  onCategoryChange, onVendorChange, onAddEntry, onManageCatalog, rangeLabel, canEdit,
}: Props) {
  return (
    <div className="filter-row spend-filters" role="group" aria-label="Spend filters">
      <label className="filter-row__field">
        <span>Category</span>
        <select
          value={categoryId ?? ''}
          onChange={e => onCategoryChange(e.target.value || undefined)}
        >
          <option value="">All categories</option>
          {categories.map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}
        </select>
      </label>

      <label className="filter-row__field">
        <span>Vendor</span>
        <select
          value={vendorId ?? ''}
          onChange={e => onVendorChange(e.target.value || undefined)}
        >
          <option value="">All vendors</option>
          {vendors.map(v => <option key={v.id} value={v.id}>{v.displayName}</option>)}
        </select>
      </label>

      {rangeLabel && <span className="spend-filters__range">{rangeLabel}</span>}

      {canEdit && (
        <div className="spend-filters__actions" role="group" aria-label="Spend actions">
          <Button variant="ghost" onClick={onManageCatalog}>
            Manage catalog
          </Button>
          <Button onClick={onAddEntry}>Add entry</Button>
        </div>
      )}
    </div>
  )
}
