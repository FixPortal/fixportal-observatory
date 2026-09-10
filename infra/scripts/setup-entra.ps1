<#
.SYNOPSIS
  One-time Entra (Azure AD) setup for AI Observatory dashboard sign-in.

  Creates (idempotently) a single app registration `fpaiobs-spa` that is BOTH the
  SPA client and the API it calls:
    - single-tenant (AzureADMyOrg)
    - SPA redirect URI https://observatory.fixportal.org
    - exposes scope api://<appId>/access_as_user, v2 access tokens
    - pre-authorises itself (no consent prompt for its own API)
    - enterprise app set to "user assignment required", with the signed-in user
      assigned — so only you can sign in.

  Prints the clientId / tenantId / scope to paste into the web .env.production and
  the Bicep aadClientId param. Re-runnable: existing objects are reused, not duplicated.

.NOTES
  Requires: az CLI logged in (`az login`) as a user who can create app registrations
  in the target tenant. Graph writes go via Invoke-RestMethod (not `az rest`) to
  dodge the PowerShell/az JSON-quoting traps on Windows.
#>
[CmdletBinding()]
param(
  [string]$DisplayName = 'fpaiobs-spa',
  [string]$RedirectUri = 'https://observatory.fixportal.org'
)

$ErrorActionPreference = 'Stop'

function Invoke-Graph {
  param(
    [Parameter(Mandatory)][string]$Method,
    [Parameter(Mandatory)][string]$Uri,
    [object]$Body
  )
  $token = az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv
  if ($LASTEXITCODE -ne 0) { throw 'Failed to get a Microsoft Graph access token (is az logged in?)' }
  $headers = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }
  if ($null -ne $Body) {
    $json = $Body | ConvertTo-Json -Depth 10
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -Body $json
  }
  return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
}

Write-Host '== AI Observatory Entra setup ==' -ForegroundColor Cyan

$tenantId = az account show --query tenantId -o tsv
if ($LASTEXITCODE -ne 0) { throw 'az not logged in. Run `az login` first.' }
Write-Host "Tenant: $tenantId"

# --- Signed-in user (reliable for #EXT# guests via /me) ---
$me = Invoke-Graph -Method GET -Uri 'https://graph.microsoft.com/v1.0/me'
$userOid = $me.id
Write-Host "Assigning sign-in to: $($me.userPrincipalName) ($userOid)"

# --- App registration (create or reuse) ---
$existing = Invoke-Graph -Method GET -Uri "https://graph.microsoft.com/v1.0/applications?`$filter=displayName eq '$DisplayName'"
if ($existing.value.Count -gt 0) {
  $app = $existing.value[0]
  Write-Host "Reusing app registration $DisplayName ($($app.appId))"
} else {
  $app = Invoke-Graph -Method POST -Uri 'https://graph.microsoft.com/v1.0/applications' -Body @{
    displayName    = $DisplayName
    signInAudience = 'AzureADMyOrg'
  }
  Write-Host "Created app registration $DisplayName ($($app.appId))" -ForegroundColor Green
}
$appObjId = $app.id
$appId    = $app.appId

# --- Reuse an existing scope id if present, else mint one ---
$scopeId = $null
if ($app.api -and $app.api.oauth2PermissionScopes) {
  $scope = $app.api.oauth2PermissionScopes | Where-Object { $_.value -eq 'access_as_user' } | Select-Object -First 1
  if ($scope) { $scopeId = $scope.id }
}
if (-not $scopeId) { $scopeId = [guid]::NewGuid().ToString() }

# --- PATCH 1: identifier URI, SPA redirect, exposed scope, requested permissions ---
$patch = @{
  identifierUris = @("api://$appId")
  spa            = @{ redirectUris = @($RedirectUri) }
  api            = @{
    requestedAccessTokenVersion = 2
    oauth2PermissionScopes      = @(@{
      id                      = $scopeId
      value                   = 'access_as_user'
      type                    = 'User'
      isEnabled               = $true
      adminConsentDisplayName = 'Access AI Observatory'
      adminConsentDescription = 'Allow the signed-in user to call the AI Observatory API.'
      userConsentDisplayName  = 'Access AI Observatory'
      userConsentDescription  = 'Allow this app to call the AI Observatory API on your behalf.'
    })
  }
  requiredResourceAccess = @(
    @{ resourceAppId = $appId; resourceAccess = @(@{ id = $scopeId; type = 'Scope' }) }
    # Microsoft Graph User.Read (sign-in + profile)
    @{ resourceAppId = '00000003-0000-0000-c000-000000000000'; resourceAccess = @(@{ id = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'; type = 'Scope' }) }
  )
}
Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$appObjId" -Body $patch | Out-Null
Write-Host 'Configured identifier URI, SPA redirect, and exposed scope.'

# --- PATCH 2: pre-authorise self for the scope (skips the consent prompt) ---
Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$appObjId" -Body @{
  api = @{ preAuthorizedApplications = @(@{ appId = $appId; delegatedPermissionIds = @($scopeId) }) }
} | Out-Null
Write-Host 'Pre-authorised the SPA for its own API scope.'

# --- Service principal (enterprise app) ---
$spResp = Invoke-Graph -Method GET -Uri "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '$appId'"
if ($spResp.value.Count -gt 0) {
  $sp = $spResp.value[0]
} else {
  $sp = Invoke-Graph -Method POST -Uri 'https://graph.microsoft.com/v1.0/servicePrincipals' -Body @{ appId = $appId }
  Write-Host 'Created service principal.' -ForegroundColor Green
}
$spId = $sp.id

# --- Require assignment, then assign the signed-in user ---
Invoke-Graph -Method PATCH -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spId" -Body @{ appRoleAssignmentRequired = $true } | Out-Null
Write-Host 'Set user-assignment-required on the enterprise app.'

$assignments = Invoke-Graph -Method GET -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignedTo"
$already = $assignments.value | Where-Object { $_.principalId -eq $userOid }
if ($already) {
  Write-Host 'User already assigned.'
} else {
  Invoke-Graph -Method POST -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignedTo" -Body @{
    principalId = $userOid
    resourceId  = $spId
    appRoleId   = '00000000-0000-0000-0000-000000000000'  # default access (no custom app role)
  } | Out-Null
  Write-Host 'Assigned the signed-in user.' -ForegroundColor Green
}

Write-Host ''
Write-Host '== DONE — paste these into config ==' -ForegroundColor Cyan
Write-Host "VITE_AAD_CLIENT_ID = $appId"
Write-Host "VITE_AAD_TENANT_ID = $tenantId"
Write-Host "VITE_AAD_API_SCOPE = api://$appId/access_as_user"
Write-Host "Bicep aadClientId  = $appId"
