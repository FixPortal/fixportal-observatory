# Notification settings design

Status: approved for implementation
Date: 2026-08-30

## Problem

Budget-threshold alerts (`BudgetAlertService`) deliver via `EmailAlertNotifier`, which reads
`BUDGET_ALERT_EMAIL_TO` from environment configuration. There is no way to set or change the
alert recipient from the app itself — it requires an infra-level App Service config edit. There
is also no second delivery channel; Slack was requested.

## Goals

- Let an admin set/change/remove the alert email recipient from the app UI.
- Add Slack as a second, independent delivery channel via an incoming webhook URL, also
  settable from the UI.
- Keep the existing single trigger (budget threshold exceeded) and its existing at-least-once
  delivery semantics unchanged — this is a delivery-configuration change, not a new trigger.

## Non-goals

- Multiple recipients per channel (one email + one webhook, matching today's single-recipient
  model).
- Notifications for other events (new Insights, source-status failures, etc.) — budget alerts
  only.
- SMTP server credentials (host/port/user/password) moving out of env vars — those are infra
  credentials, not per-preference config, and stay as they are.
- A full Slack App/OAuth bot — an incoming webhook is sufficient for a single fixed channel
  and needs no OAuth install flow.

## Data model

New entity `NotificationSettings`, one singleton row (no per-user/per-tenant scoping exists
anywhere else in this app either):

```csharp
public sealed class NotificationSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? AlertEmailTo { get; set; }
    public string? SlackWebhookUrl { get; set; }
    public Instant UpdatedAt { get; set; }
}
```

EF Core migration adds the table. The row is created lazily on first `PUT` (or read as "nothing
configured" if it doesn't exist yet on `GET`) — no seed migration needed.

## API

New `src/AiObservatory.Api/Endpoints/NotificationSettingsEndpoints.cs`, following the same
direct-DbContext-access pattern `BudgetRulesEndpoints.cs` already uses for its own admin-config
entity (no repository indirection for a single-row config table).

- `GET /api/notification-settings` → readonly-key readable, like every other GET in this app.
  Returns:
  ```json
  { "emailConfigured": true, "emailMasked": "ch***@fixportal.org",
    "slackConfigured": false, "slackMasked": null }
  ```
  Masking never returns the real value: email shows first 2 chars + `***@domain`; the webhook
  shows `https://hooks.slack.com/services/***` only.

- `PUT /api/notification-settings` → admin-key gated (the existing non-GET default). Body:
  ```json
  { "alertEmailTo": "string | null (optional)", "slackWebhookUrl": "string | null (optional)" }
  ```
  A field **omitted** from the body leaves that setting unchanged; a field present as `null` or
  `""` clears it. Validation: `alertEmailTo`, if non-empty, must parse as a `MailboxAddress`
  (MailKit is already a dependency, used identically in `EmailAlertNotifier`); `slackWebhookUrl`,
  if non-empty, must start with `https://hooks.slack.com/`. Either failure returns 400.

- `GET /api/budget-rules/email-status` is deleted, not kept alongside the new endpoint — it's
  fully superseded and nothing else references its specific shape.

## Delivery

- `EmailAlertNotifier` changes to read `AlertEmailTo` from `NotificationSettings` (via
  `AiObservatoryDbContext`) instead of `config["BUDGET_ALERT_EMAIL_TO"]`. SMTP
  host/port/user/pass/from stay exactly as they are (env vars).
- New `SlackAlertNotifier` (same `IAlertNotifier` shape internally, composed rather than
  registered directly — see below): if `SlackWebhookUrl` is unset, no-ops, matching
  `EmailAlertNotifier`'s existing behavior for an unset recipient. Otherwise POSTs a JSON
  `{"text": "..."}` payload (Slack's incoming-webhook message format) built from the same
  `BudgetAlertPayload` fields `EmailAlertNotifier` already uses, via a named `HttpClient`
  (registered in `Program.cs` alongside this app's other named clients).
- New `CompositeAlertNotifier` implements `IAlertNotifier` and is what's registered in DI in
  place of the current direct `EmailAlertNotifier` registration. It fans out to both channel
  notifiers; each is wrapped in its own try/catch so one channel's failure (e.g. SMTP timeout)
  never blocks the other, and both failures are logged, not swallowed silently.
  `BudgetAlertService`'s single `notifier.NotifyAsync(...)` call site is unchanged — it depends
  only on `IAlertNotifier`, never on which concrete channels exist behind it.

## Frontend

A small section added to `BudgetRulesPanel.tsx`, replacing its current `useEmailStatus()` call
(which hits the endpoint being deleted) with a new `useNotificationSettings()` hook against
`GET /api/notification-settings`. Two fields, each independently:
- **Configured**: shows the masked value + Edit + Remove (Remove uses the same two-click
  confirm/cancel pattern as `SpendLedgerTable`'s delete and `AdversarialReviewPanel`'s sweep
  removal, established earlier this session).
- **Not configured**: shows "Not set" + an Add affordance (a text input + Save).

Hidden entirely for `isReadonly` viewers, matching every other admin write-affordance in this
panel and the rest of the app (`SubscriptionPanel`, `BudgetRulesPanel`'s existing delete UI,
`AdversarialReviewPanel`).

## Testing

- `NotificationSettingsEndpointsTests` (or inline in an existing endpoint-test file, following
  house convention) covering: masking format, partial-update semantics (omitted vs. explicit
  null), and validation rejection for a malformed email/webhook URL.
- `EmailAlertNotifierTests` updated for the new DB-sourced recipient (repository/DbContext
  stubbed, not `IConfiguration`).
- New `SlackAlertNotifierTests`: no-op when unset, correct JSON payload shape when set, and that
  an HTTP failure doesn't throw past the notifier (mirrors `EmailAlertNotifier`'s
  finally-disconnect resilience pattern).
- New `CompositeAlertNotifierTests`: both channels called independently; one channel throwing
  doesn't prevent the other from being attempted.
- Frontend: extend `BudgetRulesPanel.test.tsx` for the new fields' configured/not-configured/
  edit/remove-confirm states, mirroring the existing rule-delete confirm test.

## Rollout

No feature flag — this is additive (a new optional delivery channel plus a UI for an existing
one) and the existing SMTP-based delivery keeps working unchanged for anyone who already has
`BUDGET_ALERT_EMAIL_TO` set... except that env var stops being read once this ships, since the
recipient moves to the DB. **This is the one behavior-changing edge case**: an existing deployed
instance with only the env var set (no DB row yet) will silently stop emailing until an admin
re-enters the email address via the new settings UI. Call this out explicitly when merging — it
needs a one-time manual step in prod (visit the new settings UI and paste the same recipient),
not a migration, since there's nowhere in the DB the current env var's value could be safely
copied from during a migration.
