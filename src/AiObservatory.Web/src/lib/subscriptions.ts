interface NotionalAggregate {
  provider: string
  date: string
  costBasis: string
  costUsd: number
  requestCount: number
  unknownCostCount: number
}

const MONTHS = [
  'January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December',
]

export const billingMonthName = (month: number): string => MONTHS[month - 1] ?? ''

export function subscriptionUsage(aggregates: NotionalAggregate[], provider: string, from: string) {
  const period = aggregates.filter(a => a.provider === provider && a.date >= from && a.costBasis === 'notional')
  const requestCount = period.reduce((total, a) => total + a.requestCount, 0)
  const unknownCostCount = period.reduce((total, a) => total + a.unknownCostCount, 0)
  return {
    notionalUsd: requestCount === 0 || requestCount > unknownCostCount
      ? period.filter(a => a.requestCount > a.unknownCostCount).reduce((total, a) => total + a.costUsd, 0)
      : null,
    requestCount,
    unknownCostCount,
  }
}

/**
 * Returns the ISO date (yyyy-MM-dd) of the most recent occurrence of
 * billingDay on or before today, handling month-end clamping.
 *
 * E.g. billingDay=31 in February returns the last day of February.
 */
export function currentBillingPeriodStart(
  billingDay: number,
  today: string,
  billingInterval: 'monthly' | 'annual' = 'monthly',
  billingMonth: number | null = null,
): string {
  const [yearStr, monthStr, dayStr] = today.split('-')
  const year = parseInt(yearStr, 10)
  const month = parseInt(monthStr, 10) // 1–12
  const day = parseInt(dayStr, 10)

  // Days in month m (1-indexed) of year y.
  // new Date(y, m, 0) uses JS's 0-based month index; day 0 = last day of prior month.
  const daysIn = (y: number, m: number) => new Date(y, m, 0).getDate()
  const clamp = (y: number, m: number) => Math.min(billingDay, daysIn(y, m))

  if (billingInterval === 'annual') {
    if (billingMonth === null) throw new Error('Annual subscriptions require a billing month.')
    if (billingMonth < 1 || billingMonth > 12) throw new Error('billingMonth must be between 1 and 12.')
    const renewalDay = clamp(year, billingMonth)
    const renewalPassed = month > billingMonth || (month === billingMonth && day >= renewalDay)
    const startYear = renewalPassed ? year : year - 1
    return `${startYear}-${String(billingMonth).padStart(2, '0')}-${String(clamp(startYear, billingMonth)).padStart(2, '0')}`
  }

  const clampedThisMonth = clamp(year, month)
  if (day >= clampedThisMonth) {
    return `${year}-${monthStr}-${String(clampedThisMonth).padStart(2, '0')}`
  }

  // Billing day hasn't occurred yet — period started last month.
  const prevMonth = month === 1 ? 12 : month - 1
  const prevYear = month === 1 ? year - 1 : year
  const d = clamp(prevYear, prevMonth)
  return `${prevYear}-${String(prevMonth).padStart(2, '0')}-${String(d).padStart(2, '0')}`
}
