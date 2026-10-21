interface StatusBadgeProps {
  variant: 'ok' | 'warn' | 'bad' | 'info'
  label: string
}

export function StatusBadge({ variant, label }: StatusBadgeProps) {
  return <span className={`fpds-badge fpds-badge--${variant}`}>{label}</span>
}
