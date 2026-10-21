param serverName string
param allowedIps array = []

// Referenced as existing, not created/managed, by the recurring infra deploy.
// The flexible server (admin password, SKU, storage, version) is provisioned
// once at bootstrap; re-asserting `administratorLoginPassword` on every deploy
// would reset the live password and desync the Key Vault `db-connection` secret.
// Only the firewall rules are managed here (idempotent).
resource db 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' existing = {
  name: serverName
}

resource firewallRules 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = [for (ip, i) in allowedIps: {
  parent: db
  name: 'allow-app-${i}'
  properties: {
    startIpAddress: ip
    endIpAddress: ip
  }
}]

output fqdn string = db.properties.fullyQualifiedDomainName
