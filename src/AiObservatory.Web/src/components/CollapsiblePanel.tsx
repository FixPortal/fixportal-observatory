import { useEffect, useState, type ReactNode } from 'react'
import { safeStorage } from '../lib/safeStorage'

interface CollapsiblePanelProps {
  id: string
  title: string
  summary?: ReactNode
  children: ReactNode
}

export function CollapsiblePanel({ id, title, summary, children }: CollapsiblePanelProps) {
  const [open, setOpen] = useState(() => safeStorage.get(`panel-${id}-expanded`) === 'true')
  const bodyId = `panel-${id}-body`

  useEffect(() => {
    safeStorage.set(`panel-${id}-expanded`, String(open))
  }, [id, open])

  function toggle() {
    setOpen(prev => !prev)
  }

  return (
    <div className="collapsible-panel">
      <button
        type="button"
        className="collapsible-panel__header"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={toggle}
      >
        <span className="collapsible-panel__dot" aria-hidden="true" />
        <span className="collapsible-panel__title">{title}</span>
        {summary !== undefined && (
          <span className="collapsible-panel__summary">{summary}</span>
        )}
        <span
          className={`collapsible-panel__chevron${open ? ' collapsible-panel__chevron--open' : ''}`}
          aria-hidden="true"
        >
          ▾
        </span>
      </button>
      <div
        id={bodyId}
        className={`collapsible-panel__body-outer${open ? ' collapsible-panel__body-outer--open' : ''}`}
        aria-hidden={!open}
        inert={!open}
      >
        <div className="collapsible-panel__body-inner">
          {children}
        </div>
      </div>
    </div>
  )
}
