<#
.SYNOPSIS
  One-time (re-runnable) operator bootstrap for the public read-only demo stack.

  Creates, in order: resource group, PostgreSQL flexible server + database, Static
  Web App, the Bicep deployment (App Service, Key Vault, App Insights, no ingest
  worker, demo mode on), Key Vault secrets, and the `demo` GitHub environment with
  its variables and secrets.

  Re-running is safe: existing resources are reused. Keys and the database password
  are regenerated every run and re-applied everywhere, so the stack stays
  consistent, and any previously issued demo key stops working.

  Nothing secret is printed. Secret values reach GitHub through stdin and Key Vault
  through a short-lived temp file, never through a logged command line. The one
  exception is the database admin password, which `az postgres flexible-server
  create` only accepts as an argument; it is visible in this machine's process list
  for the duration of that call.

.PARAMETER DeployClientId
  Client id of the Entra app registration the demo workflows sign in with (OIDC).
  Its federated credential subject must be
  repo:<Repo>:environment:demo and it needs Contributor on the demo resource group.
  Optional: when omitted AZURE_CLIENT_ID is not set and a reminder is printed.

.NOTES
  Requires: az CLI and gh CLI, both logged in, pwsh 7.2+.
  This script creates paid Azure resources. See docs/demo.md.
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
  [string]$Location = 'westeurope',
  [ValidatePattern('^[a-z][a-z0-9]{2,13}$')]
  [string]$Prefix = 'fpaiodemo',
  [string]$Repo = 'FixPortal/fixportal-observatory',
  [string]$DeployClientId = ''
)

$ErrorActionPreference = 'Stop'

# Production uses the fpaiobs prefix. Refuse it, and anything that starts with it.
if ($Prefix -like 'fpaiobs*') {
  throw "Refusing prefix '$Prefix': fpaiobs* is the production stack."
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bicep = Join-Path $repoRoot 'infra/main.bicep'
if (-not (Test-Path $bicep)) { throw "Cannot find $bicep" }

$rg = "$Prefix-rg"
$dbServer = "$Prefix-db"
$swaName = "$Prefix-swa"
$kvName = "$Prefix-kv"
$apiName = "$Prefix-api"
$dbName = 'aiobservatory'
$dbAdmin = 'aiobsadmin'
$envName = 'demo'

function New-HexKey {
  param([int]$Bytes = 24)
  return [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes($Bytes))
}

Write-Host "== Demo bootstrap: prefix '$Prefix' in $Location ==" -ForegroundColor Cyan

# 1. Signed-in context
$subscriptionId = az account show --query id -o tsv
if ($LASTEXITCODE -ne 0) { throw 'az not logged in. Run `az login` first.' }
$tenantId = az account show --query tenantId -o tsv
if ($LASTEXITCODE -ne 0) { throw 'Could not read the tenant id.' }
gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'gh not logged in. Run `gh auth login` first.' }

# 2. Resource group
az group create --name $rg --location $Location --output none
if ($LASTEXITCODE -ne 0) { throw "Failed to create resource group $rg" }

# 3. Secrets, generated now and never printed
$adminKey = New-HexKey
$readonlyKey = New-HexKey
$ideKey = New-HexKey
# Azure needs three of four character classes. Hex gives digits + uppercase; the suffix
# adds a lowercase letter and a non-alphanumeric. No ; = quote space backtick or $, so it
# is safe inside an Npgsql connection string.
$dbPassword = "$(New-HexKey)a!"

# 4. PostgreSQL flexible server (create, or reset the password of the existing one)
$existingDb = az postgres flexible-server show --resource-group $rg --name $dbServer --query name --output tsv 2>$null
if ($LASTEXITCODE -eq 0 -and $existingDb) {
  Write-Host "Reusing PostgreSQL server $dbServer; resetting its admin password."
  az postgres flexible-server update --resource-group $rg --name $dbServer --admin-password $dbPassword --output none
  if ($LASTEXITCODE -ne 0) { throw "Failed to reset the admin password on $dbServer" }
} else {
  Write-Host "Creating PostgreSQL server $dbServer (Burstable B1ms, v16, 32 GB)."
  az postgres flexible-server create --resource-group $rg --name $dbServer --location $Location --tier Burstable --sku-name Standard_B1ms --version 16 --storage-size 32 --admin-user $dbAdmin --admin-password $dbPassword --public-access None --yes --output none
  if ($LASTEXITCODE -ne 0) { throw "Failed to create PostgreSQL server $dbServer" }
}
az postgres flexible-server db create --resource-group $rg --server-name $dbServer --database-name $dbName --output none
if ($LASTEXITCODE -ne 0) { throw "Failed to create database $dbName" }

# 5. Static Web App (Free), then its deployment token and default hostname
$existingSwa = az staticwebapp show --resource-group $rg --name $swaName --query name --output tsv 2>$null
if (-not ($LASTEXITCODE -eq 0 -and $existingSwa)) {
  az staticwebapp create --resource-group $rg --name $swaName --location $Location --sku Free --output none
  if ($LASTEXITCODE -ne 0) { throw "Failed to create Static Web App $swaName" }
}
$swaToken = az staticwebapp secrets list --resource-group $rg --name $swaName --query properties.apiKey --output tsv
if ($LASTEXITCODE -ne 0 -or -not $swaToken) { throw 'Failed to read the Static Web App deployment token' }
$swaHost = az staticwebapp show --resource-group $rg --name $swaName --query defaultHostname --output tsv
if ($LASTEXITCODE -ne 0 -or -not $swaHost) { throw 'Failed to read the Static Web App hostname' }

# swaOrigin is a CORS EXACT-MATCH origin: scheme + host, no trailing slash, no path.
# A trailing slash does not error; the browser simply never matches and every call fails.
$swaOrigin = "https://$swaHost"

# 6. Bicep deployment. A parameters file avoids all native-argument quoting of the
#    empty strings and booleans.
$parameters = [ordered]@{
  '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
  contentVersion = '1.0.0.0'
  parameters     = [ordered]@{
    prefix          = @{ value = $Prefix }
    deployIngest    = @{ value = $false }
    demoMode        = @{ value = $true }
    aadTenantId     = @{ value = '' }
    aadClientId     = @{ value = '' }
    swaCustomDomain = @{ value = '' }
    swaOrigin       = @{ value = $swaOrigin }
  }
}
$paramFile = Join-Path ([IO.Path]::GetTempPath()) "demo-params-$([guid]::NewGuid().ToString('N')).json"
try {
  $parameters | ConvertTo-Json -Depth 6 | Set-Content -Path $paramFile -Encoding utf8NoBOM
  az deployment group create --resource-group $rg --name "$Prefix-infra" --template-file $bicep --parameters "@$paramFile" --output none
  if ($LASTEXITCODE -ne 0) { throw 'Bicep deployment failed' }
} finally {
  Remove-Item -Path $paramFile -Force -ErrorAction SilentlyContinue
}

# 7. Key Vault: grant the signed-in user, then set the secrets
$userOid = az ad signed-in-user show --query id --output tsv
if ($LASTEXITCODE -ne 0 -or -not $userOid) { throw 'Failed to read the signed-in user object id' }
$kvId = az keyvault show --name $kvName --query id --output tsv
if ($LASTEXITCODE -ne 0 -or -not $kvId) { throw "Failed to find Key Vault $kvName" }
az role assignment create --assignee-object-id $userOid --assignee-principal-type User --role 'Key Vault Secrets Officer' --scope $kvId --output none
if ($LASTEXITCODE -ne 0) { throw 'Failed to grant Key Vault Secrets Officer' }

# Let the deploy identity (OIDC app) deploy to the demo stack: Contributor on the demo
# resource group only. Re-assigning an identical assignment is a no-op.
if ($DeployClientId) {
  $rgId = az group show --name $rg --query id --output tsv
  if ($LASTEXITCODE -ne 0 -or -not $rgId) { throw "Failed to read the id of $rg" }
  az role assignment create --assignee $DeployClientId --role Contributor --scope $rgId --output none
  if ($LASTEXITCODE -ne 0) { throw "Failed to grant Contributor on $rg to the deploy identity" }
}

function Set-VaultSecret {
  param([string]$Name, [string]$Value)
  # --file keeps the value off the command line. No trailing newline is written.
  $file = Join-Path ([IO.Path]::GetTempPath()) "kv-$([guid]::NewGuid().ToString('N')).txt"
  try {
    [IO.File]::WriteAllText($file, $Value, [Text.UTF8Encoding]::new($false))
    # A fresh role assignment can take a minute to propagate; retry on failure.
    for ($attempt = 1; $attempt -le 8; $attempt++) {
      az keyvault secret set --vault-name $kvName --name $Name --file $file --encoding utf-8 --output none 2>$null
      if ($LASTEXITCODE -eq 0) { Write-Host "Set Key Vault secret $Name"; return }
      Write-Host "Key Vault secret $Name not accepted yet (attempt $attempt of 8); waiting for the role to propagate."
      Start-Sleep -Seconds 20
    }
    throw "Failed to set Key Vault secret $Name"
  } finally {
    Remove-Item -Path $file -Force -ErrorAction SilentlyContinue
  }
}

$dbConnection = "Host=$dbServer.postgres.database.azure.com;Database=$dbName;Username=$dbAdmin;Password=$dbPassword;Ssl Mode=Require"
Set-VaultSecret -Name 'db-connection' -Value $dbConnection
Set-VaultSecret -Name 'observatory-api-key' -Value $adminKey
Set-VaultSecret -Name 'observatory-readonly-api-key' -Value $readonlyKey
Set-VaultSecret -Name 'observatory-ide-api-key' -Value $ideKey

# 8. Restart so the Key Vault references re-resolve
az webapp restart --resource-group $rg --name $apiName
if ($LASTEXITCODE -ne 0) { throw "Failed to restart $apiName" }

# 9. GitHub environment, restricted to main, then variables and secrets
$apiUrl = "https://$apiName.azurewebsites.net"

$envBody = '{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}'
$envBody | gh api --method PUT "repos/$Repo/environments/$envName" --input - | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to create GitHub environment $envName" }

$policies = gh api "repos/$Repo/environments/$envName/deployment-branch-policies" --jq '.branch_policies[].name'
if ($LASTEXITCODE -ne 0) { throw 'Failed to list deployment branch policies' }
if (@($policies) -notcontains 'main') {
  $policyBody = '{"name":"main","type":"branch"}'
  $policyBody | gh api --method POST "repos/$Repo/environments/$envName/deployment-branch-policies" --input - | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Failed to restrict the demo environment to main' }
}

$variables = [ordered]@{
  AZURE_TENANT_ID       = $tenantId
  AZURE_SUBSCRIPTION_ID = $subscriptionId
  DEMO_API_URL          = $apiUrl
  DEMO_READONLY_KEY     = $readonlyKey
}
if ($DeployClientId) { $variables['AZURE_CLIENT_ID'] = $DeployClientId }
foreach ($name in $variables.Keys) {
  # The read-only key is a variable on purpose: it is baked into the public SPA bundle.
  # A dotenv line on stdin ("-f -") is parsed per line, so the newline PowerShell
  # appends when piping cannot end up inside the stored value.
  "$name=$($variables[$name])" | gh variable set -f - --env $envName --repo $Repo
  if ($LASTEXITCODE -ne 0) { throw "Failed to set variable $name" }
}

$ghSecrets = [ordered]@{
  DEMO_ADMIN_KEY            = $adminKey
  DEMO_SWA_DEPLOYMENT_TOKEN = $swaToken
}
foreach ($name in $ghSecrets.Keys) {
  "$name=$($ghSecrets[$name])" | gh secret set -f - --env $envName --repo $Repo
  if ($LASTEXITCODE -ne 0) { throw "Failed to set secret $name" }
}

Write-Host ''
Write-Host '== DONE ==' -ForegroundColor Cyan
Write-Host "API URL      : $apiUrl"
Write-Host "SWA hostname : $swaHost"
Write-Host "SWA origin   : $swaOrigin"
if (-not $DeployClientId) {
  Write-Host 'AZURE_CLIENT_ID was not set: pass -DeployClientId, or set it on the demo environment by hand.' -ForegroundColor Yellow
}
Write-Host 'Next: add the DNS record if wanted, dispatch deploy-demo.yml, then demo-reset.yml. See docs/demo.md.'
