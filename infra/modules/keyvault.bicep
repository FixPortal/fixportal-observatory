param location string
param kvName string

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    // App Service KV references do not use the AzureServices trusted bypass —
    // they authenticate via managed identity but arrive from shared App Service
    // infrastructure IPs that are not in KV's trusted-service list. A Deny ACL
    // with no explicit IP/VNet rules blocks reference resolution at startup,
    // causing the raw "@Microsoft.KeyVault(...)" string to reach the app.
    // RBAC (enableRbacAuthorization) is the correct protection layer here.
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

output kvName string = kv.name
output kvUri string = kv.properties.vaultUri
