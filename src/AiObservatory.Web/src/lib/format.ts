// Shared number/date formatting. Currency lives in currency.ts; this is the
// non-currency side (token counts, chart date ticks).

const intFmt = new Intl.NumberFormat('en-GB')

/** Whole number with thousands separators, e.g. 1162998 -> "1,162,998". */
export const formatInt = (n: number): string => intFmt.format(Math.round(n))

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/**
 * Format an ISO `yyyy-MM-dd` date as a short `"29 May"` axis label. Sliced by
 * fixed offsets (not `new Date()`) so a UTC date never shifts a day under local
 * time. Returns the input unchanged if it is not an ISO date.
 */
export function formatShortDate(iso: string): string {
  if (iso.length < 10 || iso[4] !== '-' || iso[7] !== '-') return iso
  const day = Number(iso.slice(8, 10))
  const month = MONTHS[Number(iso.slice(5, 7)) - 1]
  if (Number.isNaN(day) || month === undefined) return iso
  return `${day} ${month}`
}
