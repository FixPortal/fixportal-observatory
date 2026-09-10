import type React from 'react'

type CardProps = React.HTMLAttributes<HTMLDivElement>

export function Card({ className, children, ...rest }: CardProps) {
  const cls = ['fpds-card', className].filter(Boolean).join(' ')
  return <div className={cls} {...rest}>{children}</div>
}
