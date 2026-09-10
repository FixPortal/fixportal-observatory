targetScope = 'resourceGroup'

param location string = resourceGroup().location
param computeLocation string = 'westeurope'
param prefix string = 'fpaiobs'

// Entra (Azure AD) app registration that fronts the dashboard sign-in. Non-secret
// (tenant + client IDs are public). Empty clientId leaves JWT auth off, so the API
// falls back to API-key auth — the state before sign-in was wired. Set after the
// one-time Entra setup script (infra/scripts/setup-entra.ps1) creates the app.
param aadTenantId string = 'c5eac41f-0525-4692-8705-7822be64d5ae'
param aadClientId string = 'f3a9736e-1ba6-43b6-89f7-e799a9f93e9a'
// These default to the existing Key Vault secret names so a bare `az deployment
// group create` (which is all infra.yml runs) never silently REMOVES the two
// app settings — siteConfig.appSettings is a full replace. Pass '' explicitly
// to disable the corresponding ingestion lane.
param anthropicBillingSecretName string = 'anthropic-billing-key'
param copilotOrgSecretName string = 'copilot-org'

module kv 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: { location: location, kvName: '${prefix}-kv' }
}

module postgresql 'modules/postgresql.bicep' = {
  name: 'postgresql'
  params: {
    serverName: '${prefix}-db'
    allowedIps: union(
      split(appservice.outputs.possibleOutboundIpAddresses, ','),
      split(ingest.outputs.possibleOutboundIpAddresses, ',')
    )
  }
}

module appservice 'modules/appservice.bicep' = {
  name: 'appservice'
  params: {
    location: computeLocation
    appName: '${prefix}-api'
    kvName: kv.outputs.kvName
    aiConnectionString: appinsights.outputs.connectionString
    aadTenantId: aadTenantId
    aadClientId: aadClientId
  }
}

module appinsights 'modules/appinsights.bicep' = {
  name: 'appinsights'
  params: { location: location, aiName: '${prefix}-ai' }
}

module swa 'modules/swa.bicep' = {
  name: 'swa'
  params: { swaName: '${prefix}-swa' }
}

module ingest 'modules/ingest.bicep' = {
  name: 'ingest'
  // ingest references the App Service plan by name (`existing`), so there is no
  // implicit dependency; sequence it after appservice creates `${prefix}-api-plan`.
  dependsOn: [appservice]
  params: {
    location: computeLocation
    planName: '${prefix}-api-plan'
    appName: '${prefix}-ingest'
    kvName: kv.outputs.kvName
    aiConnectionString: appinsights.outputs.connectionString
    anthropicBillingSecretName: anthropicBillingSecretName
    copilotOrgSecretName: copilotOrgSecretName
  }
}
