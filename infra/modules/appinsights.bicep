param location string
param aiName string

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${aiName}-law'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // Runaway guard, not a budget. This workspace was uncapped (dailyQuotaGb -1)
    // until 2026-08-08 and had quietly grown to GBP 17.81/mo, of which 79.7% was
    // two log categories left at Information — now filtered in both apps'
    // appsettings.json. Expected steady-state ingestion after that is ~0.08 GB/day,
    // so 1 GB/day is roughly 12x headroom: it will not clip normal operation, but
    // it bounds the cost of a future logging regression instead of letting one run
    // for weeks unnoticed. The sibling fpsim-law-prod workspace caps at 2 GB/day
    // for the same reason.
    //
    // Capping DISCARDS data for the rest of the UTC day once tripped; it does not
    // queue it. If a cap trip is ever observed, fix the log volume rather than
    // raising this number reflexively.
    workspaceCapping: {
      dailyQuotaGb: 1
    }
  }
}

resource ai 'Microsoft.Insights/components@2020-02-02' = {
  name: aiName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

output connectionString string = ai.properties.ConnectionString
