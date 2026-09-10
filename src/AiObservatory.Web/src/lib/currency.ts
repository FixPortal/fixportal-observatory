import { useQuery } from '@tanstack/react-query'

// Costs are stored USD-native; the UI presents GBP. Fallback (~recent USD->GBP)
// keeps the dashboard rendering if the FX service is unreachable.
const FALLBACK_USD_TO_GBP = 0.79

/**
 * Live USD->GBP rate (ECB reference rates via frankfurter.app — free, no key,
 * CORS-enabled), cached ~12h by react-query.
 */
export function useUsdToGbp(): number {
  const { data } = useQuery({
    queryKey: ['fx', 'usd-gbp'],
    queryFn: async () => {
      const res = await fetch('https://api.frankfurter.dev/v1/latest?from=USD&to=GBP')
      if (!res.ok) throw new Error(`FX rate fetch failed: ${res.status}`)
      const json = (await res.json()) as { rates?: { GBP?: number } }
      const rate = json.rates?.GBP
      if (rate == null) throw new Error('GBP rate missing in response')
      return rate
    },
    placeholderData: FALLBACK_USD_TO_GBP,
    staleTime: 1000 * 60 * 60 * 12,
    gcTime: 1000 * 60 * 60 * 24,
    retry: 1,
  })
  return data ?? FALLBACK_USD_TO_GBP
}

const formatters: Record<number, Intl.NumberFormat> = {
  2: new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP', minimumFractionDigits: 2, maximumFractionDigits: 2 }),
  3: new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP', minimumFractionDigits: 3, maximumFractionDigits: 3 }),
}

/** Format an amount that is already in GBP, e.g. `12.34` -> `£12.34`. */
export function gbp(value: number, digits: 2 | 3 = 2): string {
  return formatters[digits].format(value)
}

/** Convert a USD amount to GBP and format it, e.g. `formatGbp(10, 0.8)` -> `£8.00`. */
export function formatGbp(usd: number, rate: number, digits: 2 | 3 = 2): string {
  return gbp(usd * rate, digits)
}

const IntlNumberFormat = Intl.NumberFormat
const currencyFormatters = new Map<string, Intl.NumberFormat>()

/** Format an amount in the specified currency. */
export function formatCurrency(value: number, currency: string): string {
  const code = currency.toUpperCase()
  try {
    let formatter = currencyFormatters.get(code)
    if (!formatter) {
      formatter = new IntlNumberFormat('en-GB', {
        style: 'currency',
        currency: code,
        minimumFractionDigits: 2,
        maximumFractionDigits: 2,
      })
      currencyFormatters.set(code, formatter)
    }
    return formatter.format(value)
  } catch {
    return `${code} ${value.toFixed(2)}`
  }
}
