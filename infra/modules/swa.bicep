param swaName string

// Referenced as existing, not managed by the recurring infra deploy. The Static
// Web App is provisioned at bootstrap; its GitHub linkage, deployment-token auth
// policy and platform-assigned inbound IP are live state that a sparse template
// would strip. Content is shipped via the deployment token in deploy.yml.
resource swa 'Microsoft.Web/staticSites@2023-01-01' existing = {
  name: swaName
}

// Custom domain is managed (validated via the CNAME at observatory.fixportal.org
// -> the SWA default hostname; Azure issues a free managed cert). Declared as a
// child of the existing SWA so the binding is in IaC without managing the rest
// of the resource.
resource customDomain 'Microsoft.Web/staticSites/customDomains@2023-01-01' = {
  parent: swa
  name: 'observatory.fixportal.org'
}

output url string = 'https://${swa.properties.defaultHostname}'
