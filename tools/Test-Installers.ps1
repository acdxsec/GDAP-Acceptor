[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [string]$Version = '0.4.0',
    [string]$MsiToolsImage
)
$ErrorActionPreference = 'Stop'
$Directory = (Resolve-Path $Directory).Path
$Msi = "$Directory/gdap-acceptor-$Version-x64.msi"
$Deb = "$Directory/gdap-acceptor_${Version}_amd64.deb"
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Read-MsiMetadata([string[]]$Arguments) {
    $Lines = if ($MsiToolsImage) {
        & podman run --rm --network none --entrypoint msiinfo -v "${Directory}:${Directory}:ro" $MsiToolsImage @Arguments
    } else { & msiinfo @Arguments }
    Assert ($LASTEXITCODE -eq 0) 'MSI metadata extraction failed'
    return $Lines
}
function Table([string]$Name) {
    $Lines = @(Read-MsiMetadata @('export', $Msi, $Name))
    # MSI's IDT export is tab-delimited, not CSV: quotes are literal data.
    $Columns = $Lines[0].Split("`t")
    foreach ($Line in $Lines | Select-Object -Skip 3) {
        $Values = $Line.Split("`t"); $Row = [ordered]@{}
        for ($Index = 0; $Index -lt $Columns.Count; $Index++) { $Row[$Columns[$Index]] = $Values[$Index] }
        [pscustomobject]$Row
    }
}
$Properties = @(Table Property)
Assert (-not ($Properties | Where-Object Property -eq ALLUSERS)) 'MSI changed to per-machine context'
Assert (($Properties | Where-Object Property -eq ProductVersion).Value -eq $Version) 'Wrong MSI version'
$Summary = (Read-MsiMetadata @('suminfo', $Msi)) -join "`n"
Assert ($Summary -match 'Template: x64;1033' -and $Summary -match 'Source: 10') 'MSI must be x64, compressed and limited-privilege'
$Registry = @(Table Registry)
Assert ($Registry.Count -gt 4 -and -not ($Registry | Where-Object Root -ne 1)) 'MSI must write only HKCU'
$Command = $Registry | Where-Object Key -eq 'Software\Classes\gdap-acceptor\shell\open\command'
Assert ($Command.Value -ceq '"[INSTALLDIR]gdap-acceptor.exe" "%1"') 'Protocol argument quoting changed'
$Search = (Table RegLocator) | Where-Object Signature_ -eq ExistingHandler
Assert ($Search.Root -eq 1 -and $Search.Name -eq '' -and $Search.Type -eq 18) 'Existing handler search must inspect the HKCU default value'
$Sequence = @(Table InstallExecuteSequence)
$Remove = [int]($Sequence | Where-Object Action -eq RemoveExistingProducts).Sequence
$Initialize = [int]($Sequence | Where-Object Action -eq InstallInitialize).Sequence
$Process = [int]($Sequence | Where-Object Action -eq ProcessComponents).Sequence
Assert ($Remove -gt $Initialize -and $Remove -lt $Process) 'Upgrade removal is outside the early rollback transaction'
$Conditions = @(Table LaunchCondition)
Assert ($Conditions.Condition -contains 'NOT ALLUSERS') 'Missing per-user guard'
Assert ($Conditions.Condition -contains 'NOT WIX_DOWNGRADE_DETECTED') 'Missing downgrade guard'
$Components = @(Table Component)
Assert (-not ($Components | Where-Object { ([int]$_.Attributes -band 260) -ne 260 })) 'Every component needs a 64-bit registry key path'
$Shortcut = @(Table Shortcut) | Where-Object Shortcut -eq LaunchAcceptor
Assert ($Shortcut.Target -ceq '[INSTALLDIR]gdap-acceptor.exe' -and $Shortcut.WkDir -eq 'INSTALLDIR' -and $Shortcut.Directory_ -eq 'AcceptorMenu') 'Missing standalone Start Menu launcher'
$Contents = & dpkg-deb --contents $Deb
Assert ($LASTEXITCODE -eq 0) 'Debian metadata extraction failed'
Assert (-not ($Contents | Where-Object { $_ -notmatch '^[-d][rwx-]{9}\s+root/root\s' })) 'Debian package has unexpected ownership, modes or links'
$Temp = Join-Path ([IO.Path]::GetTempPath()) ('gdap-package-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $Temp
& dpkg-deb --extract $Deb "$Temp/payload"
Assert ($LASTEXITCODE -eq 0) 'Debian extraction failed'
& dpkg-deb --control $Deb "$Temp/control"
Assert ($LASTEXITCODE -eq 0) 'Debian control extraction failed'
Assert (-not (Get-ChildItem "$Temp/control" | Where-Object Name -in @('preinst','postinst','prerm','postrm'))) 'Package must not modify desktop sessions from maintainer scripts'
& "$Temp/payload/opt/gdap-acceptor/gdap-acceptor" self-test
Assert ($LASTEXITCODE -eq 0) 'Packaged Linux native self-test failed'
Assert (Test-Path "$Temp/payload/opt/gdap-acceptor/M365Internals-LICENSE.txt") 'Bundled attribution missing'
$Desktop = Get-Content "$Temp/payload/usr/share/applications/gdap-acceptor.desktop" -Raw
Assert ($Desktop -match '(?m)^NoDisplay=false$' -and $Desktop -match '(?m)^Terminal=true$' -and $Desktop -match '(?m)^Exec=/opt/gdap-acceptor/gdap-acceptor %u$') 'Linux launcher must be visible and open a terminal'
Assert (Test-Path "$Temp/payload/opt/gdap-acceptor/scripts/GdapInvitationBrowser.ps1") 'Bundled invitation browser helper missing'
Assert (Test-Path "$Temp/payload/opt/gdap-acceptor/scripts/GdapHttpDiagnostics.ps1") 'Bundled HTTP diagnostics helper missing'
Write-Output "PASS: unsigned MSI metadata and extracted Debian payload; no system installation. Inspection files: $Temp"
