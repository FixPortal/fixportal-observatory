param location string
param appName string
param kvName string
param aiConnectionString string
param aadTenantId string = ''
param aadClientId string = ''

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: kvName
}

resource plan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: '${appName}-plan'
  location: location
  sku: { name: 'B1', tier: 'Basic' }
  kind: 'linux'
  properties: { reserved: true }
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
      appSettings: [
        { name: 'DB_CONNECTION', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=db-connection)' }
        { name: 'ANTHROPIC_API_KEY', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=anthropic-api-key)' }
        { name: 'OBSERVATORY_API_KEY', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=observatory-api-key)' }
        { name: 'OBSERVATORY_READONLY_API_KEY', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=observatory-readonly-api-key)' }
        { name: 'OBSERVATORY_IDE_API_KEY', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=observatory-ide-api-key)' }

        // GitHub billing sync. The org bill is not paid from the account the spend CSV
        // exports, so this is the only route by which GitHub spend reaches the ledger.
        // Shares the github-token secret with the ingest worker, but needs billing read
        // ("Plan" read on a fine-grained PAT) on top of that token's activity scopes.
        // Either secret absent => the reference stays unresolved (the literal
        // '@Microsoft.KeyVault(...)' string), which GitHubBillingRegistration's
        // IsConfigured guard rejects => the sync stays disabled.
        { name: 'GITHUB_TOKEN', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=github-token)' }
        { name: 'GITHUB_BILLING_ORG', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=github-billing-org)' }

        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: aiConnectionString }
        { name: 'SWA_ORIGIN', value: 'https://observatory.fixportal.org' }
        // Entra JWT auth (non-secret). Empty AzureAd__ClientId => auth off, API-key only.
        { name: 'AzureAd__Instance', value: environment().authentication.loginEndpoint }
        { name: 'AzureAd__TenantId', value: aadTenantId }
        { name: 'AzureAd__ClientId', value: aadClientId }
      ]
    }
  }
}

resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // Deterministic name (same scheme as ingest.bicep). Keyed on resource IDs, not
  // the principal: ARM forbids reference() in a resource name, so the identity's
  // principalId cannot participate. Consequence: if the app is ever recreated,
  // the stale assignment (same name, old principalId) must be deleted by hand
  // before redeploy — ARM refuses to update an assignment's principal.
  name: guid(app.id, kv.id, keyVaultSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output possibleOutboundIpAddresses string = app.properties.possibleOutboundIpAddresses
