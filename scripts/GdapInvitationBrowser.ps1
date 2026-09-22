# Owned GDAP browser orchestration. Dot-source inside the pinned module's call
# scope to reuse its browser/cookie helpers without changing upstream files.
function Resolve-GdapTenantSignInUrl {
    param([string]$StartUrl, [string]$TenantId, [string]$Username, [string]$UserAgent)
    # Discovery mode deliberately leaves customer selection until after sign-in.
    if ([string]::IsNullOrWhiteSpace($TenantId)) { return $StartUrl }
    $expected = [guid]::Empty
    if (-not [guid]::TryParseExact($TenantId, 'D', [ref]$expected) -or $expected -eq [guid]::Empty) { throw 'Invalid expected customer tenant.' }
    $candidate = $null
    $failure = 'Cannot establish a tenant-targeted Microsoft sign-in URL. Browser was not launched; no invitation was opened.'
    if (-not [uri]::TryCreate($StartUrl, [UriKind]::Absolute, [ref]$candidate) -or
        $candidate.Scheme -ne 'https' -or -not $candidate.IsDefaultPort -or $candidate.UserInfo -or $candidate.Fragment) { throw $failure }
    if ($candidate.Host -in @('admin.cloud.microsoft', 'admin.microsoft.com')) {
        # The upstream helper targets /common or /organizations only when the
        # original loginURL is already an Entra authorize URL. Portal redirects
        # require resolving the effective authorize URL first. Do not invent
        # client IDs, redirect URIs, OAuth state, nonce or signed request values.
        if ($candidate.AbsolutePath -cne '/login') { throw $failure }
        $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        $session.UserAgent = $UserAgent
        try {
            $response = Invoke-WebRequest -Uri $candidate.AbsoluteUri -WebSession $session -UserAgent $UserAgent -MaximumRedirection 5
            $effective = $response.BaseResponse.RequestMessage.RequestUri
            $candidate = $null
            if (-not [uri]::TryCreate([string]$effective, [UriKind]::Absolute, [ref]$candidate)) { throw $failure }
        } catch { throw $failure }
    }
    if ($candidate.Scheme -ne 'https' -or -not $candidate.IsDefaultPort -or $candidate.UserInfo -or $candidate.Fragment -or
        $candidate.Host -ne 'login.microsoftonline.com' -or
        $candidate.AbsolutePath -cnotmatch '^/(?:common|organizations|[0-9a-fA-F-]{36})/oauth2(?:/v2\.0)?/authorize$') { throw $failure }
    $url = [regex]::Replace($candidate.AbsoluteUri, '(?i)^(https://login\.microsoftonline\.com)/[^/]+/', "https://login.microsoftonline.com/$($expected.ToString())/")
    if ($Username -and $url -notmatch '(?:\?|&)login_hint=') {
        $separator = if ($url.Contains('?')) { '&' } else { '?' }
        $url += $separator + 'login_hint=' + [uri]::EscapeDataString($Username)
    }
    if ($url -notmatch '(?:\?|&)prompt=') {
        $separator = if ($url.Contains('?')) { '&' } else { '?' }
        $url += $separator + $(if ($Username) { 'prompt=login' } else { 'prompt=select_account' })
    }
    Write-Host "[GDAP sign-in] Targeting expected customer tenant: $expected"
    return $url
}

function Get-GdapBrowserPageState {
    param([string]$WebSocketUrl)
    # Read routing/readiness only, never page contents or authentication data.
    $output = Invoke-M365BrowserCdpCommand -WebSocketUrl $WebSocketUrl -Method 'Runtime.evaluate' -Params @{
        expression = 'JSON.stringify({url:location.href,ready:document.readyState})'
        returnByValue = $true
    }
    # The pinned transport also emits VoidTaskResult objects. Only its actual
    # Runtime.evaluate response carries the result property.
    $probe = @($output) | Where-Object {
        $_ -and $(if ($_ -is [System.Collections.IDictionary]) { $_.Contains('result') } else { $null -ne $_.PSObject.Properties['result'] })
    } | Select-Object -Last 1
    if (-not $probe -or -not $probe.result -or -not $probe.result.value) { return $null }
    try { return ($probe.result.value | ConvertFrom-Json -ErrorAction Stop) }
    catch { return $null }
}

function Connect-GdapInvitationBrowser {
    [CmdletBinding()]
    param(
        [hashtable]$AuthenticationParameters,
        [string]$RelationshipId,
        [scriptblock]$ValidateSession,
        [scriptblock]$OnInvitationReady,
        [bool]$Preview = $true,
        [ValidateRange(5, 300)][int]$InvitationTimeoutSeconds = 60
    )
    $browser = Resolve-M365BrowserPath -BrowserPath $AuthenticationParameters.BrowserPath
    $port = Get-M365BrowserFreeTcpPort
    $profile = Resolve-M365BrowserProfileConfiguration -PrivateSession
    $process = $null
    $socket = $null
    try {
        $userAgent = Get-M365DefaultUserAgent
        $startUrl = Get-M365BrowserInteractiveStartUrl -Username $AuthenticationParameters.Username -TenantId $AuthenticationParameters.TenantId -UserAgent $userAgent
        $startUrl = Resolve-GdapTenantSignInUrl -StartUrl $startUrl -TenantId $AuthenticationParameters.TenantId -Username $AuthenticationParameters.Username -UserAgent $userAgent
        $arguments = Get-M365BrowserLaunchArgumentList -Browser $browser -UsePrivateSession $true -DebugPort $port -ProfileDirectory $profile.ProfilePath -StartUrl $startUrl -UserAgent $userAgent
        Write-Host "Launching $($browser.Name) for browser sign-in..."
        Write-Host 'Complete MFA in this private browser. The invitation will open automatically after tenant validation.'
        $process = Start-M365BrowserProcess -BrowserPath $browser.Path -ArgumentList $arguments -SuppressBrowserOutput:(Test-M365BrowserProcessOutputSuppression)
        $version = Get-M365BrowserCdpVersion -Port $port -TimeoutSeconds 20
        $socket = $version.webSocketDebuggerUrl
        $deadline = (Get-Date).AddSeconds($AuthenticationParameters.TimeoutSeconds)
        $portal = $null
        $authenticatedPageReady = $false
        do {
            Start-Sleep -Seconds 2
            $process.Refresh()
            if ($process.HasExited) { throw 'The browser window was closed before sign-in completed.' }
            try {
                $target = Get-M365BrowserPreferredTargetContext -Port $port -FallbackWebSocketUrl $socket
                $socket = $target.WebSocketUrl
                $cookies = @(Get-M365BrowserCookieJar -WebSocketUrl $socket)
                $portal = New-M365BrowserPortalWebSession -Cookies $cookies -UserAgent $userAgent
            } catch {
                Write-Verbose 'Waiting for the browser sign-in tab to settle.'
                continue
            }
            $landing = $null
            if ($portal -and [uri]::TryCreate([string]$target.Url, [UriKind]::Absolute, [ref]$landing) -and
                $landing.Scheme -eq 'https' -and $landing.IsDefaultPort -and -not $landing.UserInfo -and
                $landing.Host -in @('admin.microsoft.com', 'admin.cloud.microsoft')) {
                $authenticatedPageReady = $true
                break
            }
        } while ((Get-Date) -lt $deadline)
        if (-not $authenticatedPageReady) { throw 'Browser sign-in did not reach the admin portal with a portal session before the timeout expired.' }

        # Tenant validation precedes opening an invitation which may initialize
        # customer-side state. Never infer the customer from the invitation ID.
        Write-Host 'Portal session detected. Validating customer tenant...'
        $null = & $ValidateSession $portal
        $target = Get-M365BrowserPreferredTargetContext -Port $port -FallbackWebSocketUrl $socket
        $targetUri = $null
        if (-not [uri]::TryCreate([string]$target.Url, [UriKind]::Absolute, [ref]$targetUri) -or
            $targetUri.Scheme -ne 'https' -or -not $targetUri.IsDefaultPort -or $targetUri.UserInfo -or
            $targetUri.Host -notin @('admin.cloud.microsoft', 'admin.microsoft.com')) {
            throw 'The authenticated browser has not reached the Microsoft admin portal. Invitation was not opened.'
        }
        $socket = $target.WebSocketUrl
        # Keep the exact tab that was validated and navigated. Re-running the
        # upstream preference selector can switch to a different admin.cloud
        # tab while this tab is on admin.microsoft.com or following a redirect.
        $invitationSocket = $socket
        $invitationUrl = "https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/$RelationshipId"
        Write-Host "Opening customer invitation: $invitationUrl"
        $navigation = Invoke-M365BrowserCdpCommand -WebSocketUrl $socket -Method 'Page.navigate' -Params @{ url = $invitationUrl }
        foreach ($item in @($navigation)) {
            $hasError = $item -and $(if ($item -is [System.Collections.IDictionary]) { $item.Contains('errorText') } else { $null -ne $item.PSObject.Properties['errorText'] })
            if ($hasError -and $item.errorText) { throw 'The invitation page could not be opened.' }
        }
        Write-Host '[GDAP navigation] Navigation acknowledged. Waiting for the invitation in the same browser tab.'
        $deadline = (Get-Date).AddSeconds($InvitationTimeoutSeconds)
        $ready = $false
        $lastPageState = 'No readable page state'
        do {
            Start-Sleep -Seconds 1
            $process.Refresh()
            if ($process.HasExited) { throw 'The invitation browser was closed before inspection completed.' }
            $page = Get-GdapBrowserPageState -WebSocketUrl $invitationSocket
            if (-not $page) { $lastPageState = 'No readable page state'; continue }
            $pageUri = $null
            $isPortal = [uri]::TryCreate([string]$page.url, [UriKind]::Absolute, [ref]$pageUri) -and
                $pageUri.Scheme -eq 'https' -and $pageUri.IsDefaultPort -and -not $pageUri.UserInfo -and
                $pageUri.Host -in @('admin.microsoft.com', 'admin.cloud.microsoft')
            $matchesInvitation = $isPortal -and $pageUri.Fragment -ceq "#/partners/invitation/granularAdminRelationships/$RelationshipId"
            $route = if ($matchesInvitation) { 'expected invitation' } elseif ($isPortal -and -not $pageUri.Fragment) { 'portal without invitation fragment' } elseif ($isPortal) { 'different portal route' } else { 'outside admin portal' }
            $documentState = if ($page.ready -in @('loading', 'interactive', 'complete')) { $page.ready } else { 'unknown' }
            $pageState = "same tab; route=$route; document=$documentState"
            if ($pageState -cne $lastPageState) { Write-Host "[GDAP navigation] $pageState"; $lastPageState = $pageState }
            if ($matchesInvitation -and $page.ready -eq 'complete') {
                $ready = $true
                break
            }
        } while ((Get-Date) -lt $deadline)
        if (-not $ready) { throw "The browser did not finish navigating to this invitation. Last observation: $lastPageState. Approval stopped." }
        # Refresh cookies after navigation and revalidate the tenant. Loading
        # the route is not proof of acceptance or of an API-specific add step.
        $cookies = @(Get-M365BrowserCookieJar -WebSocketUrl $socket)
        $portal = New-M365BrowserPortalWebSession -Cookies $cookies -UserAgent $userAgent
        if (-not $portal) { throw 'The invitation page did not retain an authenticated portal session.' }
        Write-Host 'Invitation page loaded. Revalidating customer tenant...'
        $null = & $ValidateSession $portal
        Write-Host 'Invitation opened in the validated customer browser. Inspecting requested access; no approval has been submitted.'
        & {
            $WhatIfPreference = $Preview
            & $OnInvitationReady
        }
    } finally {
        try {
            if ($process) {
                try { Stop-M365BrowserProcess -Process $process -BrowserWebSocketUrl $socket }
                finally { Remove-M365BrowserProcessRedirectFiles -Process $process }
            }
        } finally {
            if ($profile.CleanupProfileOnExit) {
                Remove-Item -LiteralPath $profile.ProfilePath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
