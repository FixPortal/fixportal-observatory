param location string
param planName string
param appName string
param kvName string
param aiConnectionString string
param anthropicBillingSecretName string = ''
param copilotOrgSecretName string = ''
param githubActivityOrgSecretName string = ''

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

var requiredAppSettings = [
  { name: 'DB_CONNECTION', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=db-connection)' }
  { name: 'GITHUB_TOKEN', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=github-token)' }
  // Comma-delimited owner/repo list for GitHub Activity ingestion, and an OVERRIDE rather
  // than the source of truth: leave the secret empty and set githubActivityOrgSecretName
  // instead, and the ingest discovers every non-archived repo in that organisation each
  // cycle, so a new repo needs no deploy-time change. Set it to pin an explicit subset, or
  // to reach repos outside the organisation, which discovery cannot.
  //
  // Held in Key Vault rather than appsettings.json because most of the repos are private and
  // this repo is public. Absent secret => the reference stays unresolved (the literal
  // '@Microsoft.KeyVault(...)' string), which the app's guard rejects => the allowlist reads
  // as empty, and discovery takes over if an organisation is configured.
  { name: 'Ingest__GitHubRepoAllowlist', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=github-repo-allowlist)' }
  { name: 'GOOGLE_BILLING_ACCOUNT_ID', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=google-billing-account-id)' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: aiConnectionString }
]

var optionalAppSettings = concat(
  empty(anthropicBillingSecretName) ? [] : [
    { name: 'ANTHROPIC_BILLING_KEY', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=${anthropicBillingSecretName})' }
  ],
  empty(copilotOrgSecretName) ? [] : [
    { name: 'COPILOT_ORG', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=${copilotOrgSecretName})' }
  ],
  // Organisation whose repositories GitHub Activity discovers when the allowlist above is
  // empty. Optional and its own setting rather than reusing the billing org, so turning the
  // billing arm off cannot silently stop activity ingest.
  empty(githubActivityOrgSecretName) ? [] : [
    { name: 'GITHUB_ACTIVITY_ORG', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=${githubActivityOrgSecretName})' }
  ]
)

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: kvName
}

resource plan 'Microsoft.Web/serverfarms@2023-01-01' existing = {
  name: planName
}

resource app 'Microsoft.Web/sites@2023-01-01' = {
  name: appName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      // This is a background worker, but Linux App Service is HTTP-first: it kills any
      // container that does not answer a startup probe on port 8080. The worker therefore
      // hosts a minimal Kestrel serving only /healthz (see AiObservatory.Ingest/Program.cs).
      // Pointing healthCheckPath at it makes that endpoint load-bearing rather than
      // decorative: App Service then reports an unhealthy instance instead of a container
      // that is technically running while doing nothing.
      healthCheckPath: '/healthz'
      appSettings: concat(requiredAppSettings, optionalAppSettings)
    }
  }
}

resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(app.id, kv.id, keyVaultSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output possibleOutboundIpAddresses string = app.properties.possibleOutboundIpAddresses
