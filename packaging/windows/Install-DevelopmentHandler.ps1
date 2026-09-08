# Development registration only. Production installation must use a signed,
# per-user MSI; this helper does not change PowerShell execution policy.
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows only.' }
$Path = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetFileName($Path) -ne 'gdap-acceptor.exe' -or $Path.Contains('"')) { throw 'Expected a native gdap-acceptor.exe launcher.' }
$Key = 'HKCU:\Software\Classes\gdap-acceptor'
if (Test-Path $Key) { throw 'A handler is already registered. Inspect it before replacing the registration.' }
if ($PSCmdlet.ShouldProcess($Path, 'Register per-user development URI handler')) {
    $null = New-Item -Path "$Key\shell\open\command" -Force
    Set-Item -Path $Key -Value 'URL:GDAP Acceptor'
    $null = New-ItemProperty -Path $Key -Name 'URL Protocol' -Value '' -PropertyType String
    Set-Item -Path "$Key\shell\open\command" -Value ('"' + $Path + '" "%1"')
}
