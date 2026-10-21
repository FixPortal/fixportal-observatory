import { expect, test } from 'vitest'
import { TokenAcquisitionTimeoutError } from '../auth/msal'
import { queryClient } from './queryClient'

test('does not retry a stalled token acquisition', () => {
  const retry = queryClient.getDefaultOptions().queries?.retry

  expect(retry).toBeTypeOf('function')
  if (typeof retry !== 'function') return

  expect(retry(0, new TokenAcquisitionTimeoutError())).toBe(false)
  expect(retry(0, new Error('temporary failure'))).toBe(true)
  expect(retry(3, new Error('temporary failure'))).toBe(false)
})
