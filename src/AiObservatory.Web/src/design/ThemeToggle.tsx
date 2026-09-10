import type { KeyboardEvent } from 'react'

export type ThemeMode = 'light' | 'dark' | 'system'

interface ThemeToggleProps {
  value: ThemeMode
  onChange: (mode: ThemeMode) => void
  /** 'sidebar' reads the theme-invariant sidebar palette — use when the
      toggle mounts inside the always-dark sidebar rail (A11Y-SFE-004). */
  variant?: 'default' | 'sidebar'
}

const OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
  { value: 'system', label: 'System' },
]

const ARROW_DELTAS: Record<string, number> = {
  ArrowRight: 1,
  ArrowDown: 1,
  ArrowLeft: -1,
  ArrowUp: -1,
}

export function ThemeToggle({ value, onChange, variant = 'default' }: ThemeToggleProps) {
  // Roving tabindex's other half: without this, the inactive options have
  // tabIndex -1 and no keyboard path to them. Per the WAI-ARIA radiogroup
  // pattern an arrow key both moves focus and selects, wrapping at the ends.
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const delta = ARROW_DELTAS[e.key] ?? 0
    if (delta === 0) return
    e.preventDefault()
    const current = OPTIONS.findIndex((opt) => opt.value === value)
    const next = (current + delta + OPTIONS.length) % OPTIONS.length
    onChange(OPTIONS[next].value)
    const buttons = e.currentTarget.querySelectorAll('button')
    ;(buttons[next] as HTMLButtonElement | undefined)?.focus()
  }

  return (
    <div
      role="radiogroup"
      aria-label="Theme"
      onKeyDown={onKeyDown}
      className={variant === 'sidebar' ? 'fpds-theme-toggle fpds-theme-toggle--sidebar' : 'fpds-theme-toggle'}
    >
      {OPTIONS.map(opt => (
        <button
          key={opt.value}
          type="button"
          role="radio"
          aria-checked={value === opt.value}
          tabIndex={value === opt.value ? 0 : -1}
          onClick={() => onChange(opt.value)}
          className="fpds-theme-toggle__btn"
        >
          {opt.label}
        </button>
      ))}
    </div>
  )
}
