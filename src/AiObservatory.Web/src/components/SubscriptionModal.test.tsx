import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, test, vi } from 'vitest'
// eslint-disable-next-line sonarjs/no-wildcard-import -- vi.spyOn requires the live module namespace
import * as client from '../api/client'
import SubscriptionModal from './SubscriptionModal'

vi.mock('../api/queries', () => ({
  useSubscriptions: () => ({ subscriptions: [] }),
}))

describe('SubscriptionModal', () => {
  beforeEach(() => vi.restoreAllMocks())

  test('saves an annual subscription with its renewal month', async () => {
    const create = vi.spyOn(client, 'createSubscription').mockResolvedValue({ id: 'google-one' } as client.Subscription)
    render(
      <QueryClientProvider client={new QueryClient()}>
        <SubscriptionModal open onClose={vi.fn()} />
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByRole('button', { name: /add subscription/i }))
    fireEvent.change(screen.getByLabelText('Plan name'), { target: { value: 'Annual plan' } })
    fireEvent.change(screen.getByLabelText('Monthly cost'), { target: { value: '189.99' } })
    fireEvent.change(screen.getByLabelText('Billing interval'), { target: { value: 'annual' } })
    fireEvent.change(screen.getByLabelText('Renewal month'), { target: { value: '7' } })
    fireEvent.change(screen.getByLabelText('Billing day'), { target: { value: '2' } })
    fireEvent.change(screen.getByLabelText('Active from'), { target: { value: '2026-07-02' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(create).toHaveBeenCalledOnce())
    expect(create.mock.calls[0][0]).toMatchObject({ billingInterval: 'annual', billingMonth: 7 })
  })
})
