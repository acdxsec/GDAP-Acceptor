[CmdletBinding()]
param(
    [string]$ApprovalScript = "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1",
    [string]$ModulePath = "$PSScriptRoot/../../M365Internals/M365Internals.psd1",
    [switch]$Child
)
$ErrorActionPreference = 'Stop'
if (-not $Child) {
    # A warmed-up test host can conceal a missing assembly dependency.
    $Pwsh = [IO.Path]::Combine($PSHOME, $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }))
    & $Pwsh -NoLogo -NoProfile -File $PSCommandPath -ApprovalScript $ApprovalScript -ModulePath $ModulePath -Child
    if ($LASTEXITCODE -ne 0) { throw 'Fresh-process approval startup failed.' }
    exit 0
}
# Do not load Utility, call its cmdlets, or resolve WebRequestSession here.
# Import the real dependency, replacing only the external browser/network edge.
$Module = Import-Module $ModulePath -PassThru
& $Module {
    function script:Resolve-M365BrowserPath {
        $null = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        throw 'GDAP_STARTUP_REACHED_BROWSER_BOUNDARY'
    }
}
try {
    & $ApprovalScript -RelationshipId 'startup-test' -ExpectedTenantId '11111111-1111-1111-1111-111111111111' -ExpectedPartnerTenantId '22222222-2222-2222-2222-222222222222' -WhatIf
} catch {
    if ($_.Exception.Message -eq 'GDAP_STARTUP_REACHED_BROWSER_BOUNDARY') {
        [Console]::WriteLine('PASS: fresh-process approval startup resolves real portal parameter types before reaching the stopped browser boundary')
        exit 0
    }
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
throw 'Startup did not reach the stopped browser boundary.'
