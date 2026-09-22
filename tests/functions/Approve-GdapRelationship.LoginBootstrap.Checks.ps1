[CmdletBinding()]
param(
    [string]$ApprovalScript = "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1",
    [string]$ModulePath = "$PSScriptRoot/../../M365Internals/M365Internals.psd1",
    [ValidateSet('Direct', 'PortalRedirect', 'UnsafeRedirect', 'MissingRedirect')][string]$LoginRoute = 'Direct'
)
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$module = Import-Module $ModulePath -PassThru
& $module {
    param($Route)
    $script:gdapLoginRoute = $Route
    $script:gdapLoginProbe = @{ Requests = 0; Launched = $false; UserAgent = $null }
    function script:Resolve-M365BrowserPath { @{ Name = 'Microsoft Edge'; Path = 'synthetic-browser' } }
    function script:Get-M365BrowserFreeTcpPort { 37101 }
    function script:Resolve-M365BrowserProfileConfiguration { @{ ProfilePath = 'synthetic-profile'; CleanupProfileOnExit = $false } }
    function script:Invoke-WebRequest {
        param($Uri, $WebSession, [string]$UserAgent, $MaximumRedirection)
        # Keep real URL/bootstrap helpers. At the HTTP edge use the same .NET
        # header parser, without opening sockets or authenticating to any tenant.
        $message = [System.Net.Http.HttpRequestMessage]::new()
        try { $message.Headers.UserAgent.ParseAdd($UserAgent) } finally { $message.Dispose() }
        if ([string]::IsNullOrWhiteSpace($UserAgent)) { throw 'Empty bootstrap User-Agent' }
        if ($UserAgent -cne (Get-M365DefaultUserAgent) -or $WebSession.UserAgent -cne $UserAgent) { throw 'Bootstrap User-Agent differs from the browser session' }
        $script:gdapLoginProbe.UserAgent = $UserAgent
        $script:gdapLoginProbe.Requests++
        if ($script:gdapLoginProbe.Requests -eq 1 -and $Uri -eq 'https://admin.cloud.microsoft/') {
            if ($script:gdapLoginRoute -ne 'Direct') {
                return @{ Content = "var loginURL = 'https://admin.cloud.microsoft/login?ru=%2Fadminportal';" }
            }
            return @{ Content = "var loginURL = 'https://login.microsoftonline.com/common/oauth2/authorize?client_id=synthetic';" }
        }
        if ($script:gdapLoginRoute -ne 'Direct' -and $Uri -like 'https://admin.cloud.microsoft/login?*') {
            $effective = if ($script:gdapLoginRoute -eq 'UnsafeRedirect') { 'https://example.invalid/common/oauth2/authorize?SECRET_SENTINEL' } else { 'https://login.microsoftonline.com/organizations/oauth2/authorize?client_id=synthetic&state=opaque%2Bstate&nonce=opaque%2Fnonce&redirect_uri=https%3A%2F%2Fadmin.cloud.microsoft%2Flanding' }
            if ($script:gdapLoginRoute -eq 'MissingRedirect') { return @{ Content = '$Config={};' } }
            return @{ Content = '$Config={};'; BaseResponse = @{ RequestMessage = @{ RequestUri = [uri]$effective } } }
        }
        if ($script:gdapLoginProbe.Requests -eq 2 -and $Uri -eq 'https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/authorize?client_id=synthetic') {
            return @{ Content = '$Config={};' }
        }
        throw 'Unexpected bootstrap request'
    }
    function script:Start-M365BrowserProcess {
        param($BrowserPath, $ArgumentList, $SuppressBrowserOutput)
        $expectedUrl = 'https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/authorize?client_id=synthetic&prompt=select_account'
        $expectedRequests = 2
        if ($script:gdapLoginRoute -ne 'Direct') {
            $expectedRequests = 3
            $expectedUrl = 'https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/authorize?client_id=synthetic&state=opaque%2Bstate&nonce=opaque%2Fnonce&redirect_uri=https%3A%2F%2Fadmin.cloud.microsoft%2Flanding&prompt=select_account'
        }
        if ($script:gdapLoginProbe.Requests -ne $expectedRequests -or $expectedUrl -notin $ArgumentList) { throw 'Browser did not receive the tenant-scoped account-selection URL with original OAuth parameters intact' }
        if (($ArgumentList -join ' ') -notlike "*$($script:gdapLoginProbe.UserAgent)*") { throw 'Browser User-Agent differs from bootstrap' }
        $script:gdapLoginProbe.Launched = $true
        throw 'GDAP_LOGIN_BOOTSTRAP_REACHED_BROWSER'
    }
} $LoginRoute
try {
    & $ApprovalScript -RelationshipId 'bootstrap-test' -ExpectedTenantId '11111111-1111-1111-1111-111111111111' -ExpectedPartnerTenantId '22222222-2222-2222-2222-222222222222' -WhatIf
} catch {
    if ($LoginRoute -in @('UnsafeRedirect', 'MissingRedirect')) {
        if ($_.Exception.Message -notlike '*Cannot establish a tenant-targeted Microsoft sign-in URL*' -or $_.Exception.Message -match 'SECRET_SENTINEL') { throw }
        if ((& $module { $script:gdapLoginProbe.Launched })) { throw 'Unsafe redirect launched browser' }
        Write-Output "PASS: $LoginRoute stops before browser launch without raw URL output"
        exit 0
    }
    if ($_.Exception.Message -ne 'GDAP_LOGIN_BOOTSTRAP_REACHED_BROWSER') { throw }
}
$probe = & $module { $script:gdapLoginProbe }
if (-not $probe.Launched) { throw 'Login bootstrap did not reach browser launch' }
Write-Output 'PASS: real login bootstrap receives a nonempty User-Agent matching the session and browser; tenant/account selection preserved'
