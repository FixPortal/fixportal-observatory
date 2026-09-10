import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, test, vi } from 'vitest'
import type { BudgetRule, Insight, NotificationSettings } from '../api/client'
import BudgetRulesPanel from './BudgetRulesPanel'

const data = vi.hoisted(() => ({
  rules: [] as BudgetRule[],
  insights: [] as Insight[],
  settings: { emailConfigured: false, emailMasked: null, slackConfigured: false, slackMasked: null } as NotificationSettings,
  settingsError: false,
}))

vi.mock('../api/queries', () => ({
  useBudgetRules: () => ({ rules: data.rules, isLoading: false, isError: false }),
  useInsights: () => ({ insights: data.insights, isError: false, isLoading: false }),
  useNotificationSettings: () => ({ settings: data.settings, isLoading: false, isError: data.settingsError }),
}))

const updateNotificationSettings = vi.hoisted(() => vi.fn(() => Promise.resolve({
  emailConfigured: true, emailMasked: 'ch***@fixportal.org', slackConfigured: false, slackMasked: null,
})))
vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  updateNotificationSettings,
}))

vi.mock('../auth/msal', () => ({ isReadonly: false }))

function renderPanel() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <BudgetRulesPanel />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  data.rules = []
  data.insights = []
  data.settings = { emailConfigured: false, emailMasked: null, slackConfigured: false, slackMasked: null }
  data.settingsError = false
  updateNotificationSettings.mockClear()
  updateNotificationSettings.mockImplementation(() => Promise.resolve({
    emailConfigured: true, emailMasked: 'ch***@fixportal.org', slackConfigured: false, slackMasked: null,
  }))
})

describe('BudgetRulesPanel notification settings', () => {
  test('shows "Not set" and an Add control for each unconfigured channel', () => {
    renderPanel()

    expect(screen.getByText('Email')).toBeInTheDocument()
    expect(screen.getByText('Slack')).toBeInTheDocument()
    expect(screen.getAllByText('Not set')).toHaveLength(2)
  })

  test('shows the masked value and Edit/Remove for a configured channel', () => {
    data.settings = { emailConfigured: true, emailMasked: 'ch***@fixportal.org', slackConfigured: false, slackMasked: null }
    renderPanel()

    expect(screen.getByText('ch***@fixportal.org')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /edit email/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /remove email/i })).toBeInTheDocument()
  })

  test('adding an email calls updateNotificationSettings with alertEmailTo only', async () => {
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /add email/i }))
    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'chris@fixportal.org' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(updateNotificationSettings).toHaveBeenCalledWith({ alertEmailTo: 'chris@fixportal.org' }, expect.anything()),
    )
  })

  test('shows an error message instead of silently rendering nothing when the GET fails', () => {
    data.settingsError = true
    renderPanel()

    expect(screen.getByText(/failed to load notification settings/i)).toBeInTheDocument()
    expect(screen.queryByText('Email')).not.toBeInTheDocument()
  })

  test('a rejected save surfaces an error message', async () => {
    updateNotificationSettings.mockImplementation(() => Promise.reject(new Error('Bad Request')))
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /add email/i }))
    fireEvent.change(screen.getByLabelText('Email address'), { target: { value: 'chris@fixportal.org' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(screen.getByText(/couldn.t save notification settings/i)).toBeInTheDocument(),
    )
    // The row must stay in edit mode with the typed value intact -- closing on click
    // unconditionally used to discard a rejected edit with no way to see or fix it.
    expect(screen.getByLabelText('Email address')).toHaveValue('chris@fixportal.org')
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument()
  })

  test('removing a configured Slack webhook requires a confirm click', async () => {
    data.settings = {
      emailConfigured: false, emailMasked: null,
      slackConfigured: true, slackMasked: 'https://hooks.slack.com/services/***',
    }
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /remove slack/i }))
    expect(updateNotificationSettings).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }))
    await waitFor(() =>
      expect(updateNotificationSettings).toHaveBeenCalledWith({ slackWebhookUrl: null }, expect.anything()),
    )
  })

  // Must remain the last test in this file -- vi.doMock('../auth/msal', ...) below is never
  // restored, so a test appended after this one would silently inherit isReadonly: true.
  test('hides every notification control for a readonly viewer', async () => {
    vi.doMock('../auth/msal', () => ({ isReadonly: true }))
    vi.resetModules()
    const { default: ReadonlyPanel } = await import('./BudgetRulesPanel')
    render(
      <QueryClientProvider client={new QueryClient()}>
        <ReadonlyPanel />
      </QueryClientProvider>,
    )

    expect(screen.queryByRole('button', { name: /add email/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /add slack/i })).not.toBeInTheDocument()
  })
})
