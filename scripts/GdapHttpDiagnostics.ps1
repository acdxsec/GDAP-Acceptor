# Dot-source only in the owned module call scope, never module script/global
# scope. Preserve the upstream module and restore its command resolution when
# the call returns. Do not emit request URLs, headers, bodies or raw exceptions.
param(
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 30,
    [Parameter(Mandatory)][hashtable]$State
)
$gdapHttpTimeout = $TimeoutSeconds
$gdapHttpState = $State
$gdapHttpTransport = Get-Command Invoke-WebRequest -ErrorAction Stop
if ($gdapHttpTransport -is [System.Management.Automation.FunctionInfo]) {
    # FunctionInfo can be updated in place when a name is shadowed in the same
    # scope. Capture its implementation, not the mutable command entry.
    $gdapHttpTransport = $gdapHttpTransport.ScriptBlock
}
function Invoke-WebRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][uri]$Uri,
        $WebSession,
        [hashtable]$Headers,
        [ValidateSet('Get','Post','Put','Patch','Delete','Head','Options')][string]$Method = 'Get',
        [string]$UserAgent,
        [int]$MaximumRedirection,
        $Body,
        [string]$ContentType
    )
    if ($gdapHttpState.TimedOut) {
        throw [TimeoutException]::new('An earlier portal request timed out. Further requests are blocked in this run.')
    }
    $label = switch ($Uri.AbsolutePath.ToLowerInvariant()) {
        '/adminportal/home/classicmodernadmindatastream' { 'tenant bootstrap'; break }
        '/admin/api/coordinatedbootstrap/shellinfo' { 'portal shell identity'; break }
        '/admin/api/tenant/datalocationandcommitments' { 'tenant data-location bootstrap'; break }
        '/admin/api/navigation' { 'portal navigation check'; break }
        '/admin/api/features/all' { 'portal features check'; break }
        '/api/instrument/logclient' { 'portal bootstrap telemetry'; break }
        '/adminportal' { 'portal landing document'; break }
        '/login' { 'portal login bootstrap'; break }
        '/' { 'portal home document'; break }
        default {
            if ($Uri.AbsolutePath -match '^/fd/commerceMgmt2/partnermanage/gdapInvitations/') { 'invitation inspection' }
            elseif ($Uri.AbsolutePath -match '^/fd/GdapPartnerManage/CustomerServiceAdminApi/Web/v1/GranularAdminRelationships/.+/UpdateStatus$') { 'GDAP approval submission' }
            else { 'portal request' }
        }
    }
    $gdapHttpState.Sequence++
    $tag = "[GDAP HTTP #$($gdapHttpState.Sequence)]"
    $forward = @{}
    foreach ($key in $PSBoundParameters.Keys) { $forward[$key] = $PSBoundParameters[$key] }
    # Explicit settings override session/caller defaults. No transport retries,
    # including on approval writes. These are connect/read limits, not an overall
    # wall-clock limit for the entire multi-request validation or browser CDP.
    $forward.ConnectionTimeoutSeconds = $gdapHttpTimeout
    $forward.OperationTimeoutSeconds = $gdapHttpTimeout
    $forward.MaximumRetryCount = 0
    $forward.ErrorAction = 'Stop'
    $forward.Verbose = $false
    $forward.Debug = $false
    Write-Host "$tag Starting $($Method.ToUpperInvariant()) $label (connect/read timeout: ${gdapHttpTimeout}s)."
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = & $gdapHttpTransport @forward
        Write-Host "$tag Completed $label in $([math]::Round($timer.Elapsed.TotalSeconds, 1))s."
        return $response
    } catch {
        $errorRecord = $_
        $exception = $errorRecord.Exception
        $timeout = $false
        while ($exception) {
            if ($exception -is [OperationCanceledException] -or $exception -is [TimeoutException]) { $timeout = $true }
            $exception = $exception.InnerException
        }
        # PowerShell exposes some response-read timeouts as a WebCmdlet error ID
        # rather than a TimeoutException. Never print that ID or raw message.
        if ($errorRecord.FullyQualifiedErrorId -match 'OperationTimeout|ConnectionTimeout') { $timeout = $true }
        if ($timeout) { $gdapHttpState.TimedOut = $true }
        $reason = if ($timeout) { 'timed out' } else { 'failed' }
        $message = "$tag $label $reason after $([math]::Round($timer.Elapsed.TotalSeconds, 1))s. Request was not retried."
        Write-Host $message
        if ($Method -ne 'Get' -and $Method -ne 'Head' -and $label -ne 'portal bootstrap telemetry') {
            Write-Host 'A write request failed; its outcome may be unknown. Check the relationship before submitting again.'
        }
        # Do not attach the original exception: it may include authentication or
        # response data that upstream helpers would interpolate into warnings.
        if ($timeout) { throw [TimeoutException]::new($message) }
        throw [InvalidOperationException]::new($message)
    }
}
