import { afterEach, expect, test, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import DemoBanner from './DemoBanner'

afterEach(() => vi.unstubAllEnvs())

test('shows the synthetic-data notice when VITE_DEMO_BANNER is true', () => {
  vi.stubEnv('VITE_DEMO_BANNER', 'true')
  render(<DemoBanner />)
  expect(screen.getByRole('status')).toHaveTextContent(/synthetic/i)
  expect(screen.getByRole('link', { name: /github/i })).toHaveAttribute(
    'href',
    'https://github.com/FixPortal/fixportal-observatory',
  )
})

test.each(['', 'false', 'TRUE'])('renders nothing when VITE_DEMO_BANNER is %j', value => {
  vi.stubEnv('VITE_DEMO_BANNER', value)
  const { container } = render(<DemoBanner />)
  expect(container).toBeEmptyDOMElement()
})
