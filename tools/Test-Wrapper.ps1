[CmdletBinding()]
param([string]$Wrapper = "$PSScriptRoot/../scripts/Invoke-Acceptance.ps1")
$ErrorActionPreference = 'Stop'
$stage = [IO.Directory]::CreateTempSubdirectory('gdap-wrapper-contract-').FullName
Copy-Item -LiteralPath $Wrapper -Destination "$stage/Invoke-Acceptance.ps1"
Copy-Item -LiteralPath "$PSScriptRoot/../tests/fixtures/ApprovalStub.ps1" -Destination "$stage/Approve-GdapRelationship.ps1"
$pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$count = 0
foreach ($mode in @('Discovery', 'Explicit')) {
    foreach ($scenario in @('active', 'pending', 'approved', 'activating', 'empty', 'malformed', 'error')) {
        $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', "$stage/Invoke-Acceptance.ps1", '-RelationshipId', 'synthetic-invitation', '-ExpectedPartnerTenantId', '22222222-2222-2222-2222-222222222222')) { $start.ArgumentList.Add($argument) }
        if ($mode -eq 'Discovery') { $start.ArgumentList.Add('-ConfirmAuthenticatedTenant') }
        else {
            $start.ArgumentList.Add('-ExpectedTenantId')
            $start.ArgumentList.Add('11111111-1111-1111-1111-111111111111')
        }
        $start.Environment['GDAP_TEST_RESULT'] = $scenario
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        try {
            $null = $process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Isolated wrapper check timed out' }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            $expected = if ($scenario -eq 'active') { 0 } elseif ($scenario -eq 'error') { 1 } else { 2 }
            if ($process.ExitCode -ne $expected -or $output -notmatch 'STUB: validated') { throw "Wrapper contract failed: $mode/$scenario exit=$($process.ExitCode). $output" }
            if ($output.Contains('SYNTHETIC_SECRET_MUST_NOT_APPEAR')) { throw 'Raw approval exception leaked' }
            $count++
            Write-Host "PASS: wrapper $mode/$scenario"
        } finally {
            if ($process.Id -and -not $process.HasExited) { $process.Kill($true) }
            $process.Dispose()
        }
    }
}
Write-Output "PASS: $count real wrapper subprocess checks with a stub approval script; no authentication or network."
Write-Output "Isolated fixtures: $stage"
