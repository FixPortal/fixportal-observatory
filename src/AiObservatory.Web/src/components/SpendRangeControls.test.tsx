import { fireEvent, render, screen } from '@testing-library/react'
import { expect, test, vi } from 'vitest'
import SpendRangeControls from './SpendRangeControls'

test('ignores a cleared date input until the user supplies a valid date', () => {
  const onCustom = vi.fn()
  const onComparison = vi.fn()
  render(
    <SpendRangeControls
      from={new Date('2026-08-01T00:00:00')}
      to={new Date('2026-08-28T00:00:00')}
      preset="thisMonth"
      comparisonFrom={new Date('2026-07-01T00:00:00')}
      comparisonTo={new Date('2026-07-31T00:00:00')}
      comparisonMode="previous"
      onPreset={vi.fn()}
      onCustom={onCustom}
      onComparison={onComparison}
      onPreviousComparison={vi.fn()}
    />,
  )

  fireEvent.change(screen.getByLabelText('Selected from'), { target: { value: '' } })
  fireEvent.change(screen.getByLabelText('Compare to'), { target: { value: '' } })

  expect(onCustom).not.toHaveBeenCalled()
  expect(onComparison).not.toHaveBeenCalled()
})
