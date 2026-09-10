import { useState, useRef, useEffect, type ReactNode } from 'react'

interface Props {
  id: string
  title: string
  children: ReactNode
  className?: string
}

export function InfoPopover({ id, title, children, className }: Props) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const popoverClass = className ? `info-popover ${className}` : 'info-popover'

  useEffect(() => {
    if (!open) return undefined
    const onOutside = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false)
    }
    const onEsc = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', onOutside)
    document.addEventListener('keydown', onEsc)
    return () => {
      document.removeEventListener('mousedown', onOutside)
      document.removeEventListener('keydown', onEsc)
    }
  }, [open])

  return (
    <div ref={ref} className="info-anchor">
      <button
        type="button"
        className="info-btn"
        aria-label={title}
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        onClick={() => setOpen(o => !o)}
      >
        i
      </button>
      {open && (
        <section id={id} aria-label={title} className={popoverClass}>
          <div className="info-popover__title">{title}</div>
          {children}
          <div className="info-popover__caret" aria-hidden="true" />
        </section>
      )}
    </div>
  )
}
