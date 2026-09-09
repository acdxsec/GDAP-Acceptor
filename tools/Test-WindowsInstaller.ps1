[CmdletBinding()]
param([Parameter(Mandatory)][string]$Msi, [Parameter(Mandatory)][string]$UpgradeMsi)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This mutating lifecycle check is restricted to disposable GitHub-hosted Windows runners.'
}
$Install = Join-Path $env:LOCALAPPDATA 'Programs/GDAP Acceptor'
$State = Join-Path $env:LOCALAPPDATA 'gdap-acceptor'
$Handler = 'HKCU:\Software\Classes\gdap-acceptor'
$Owner = 'HKCU:\Software\acdxsec\GDAP Acceptor'
foreach ($Path in @($Install,$State,$Handler,$Owner)) { if (Test-Path $Path) { throw "Refusing to touch pre-existing state: $Path" } }
$Msi = (Resolve-Path $Msi).Path; $UpgradeMsi = (Resolve-Path $UpgradeMsi).Path
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Installer([string]$Operation, [string]$Package, [string]$Name, [string[]]$Properties = @(), [int]$Expected = 0) {
    $Log = Join-Path $env:RUNNER_TEMP "gdap-msi-$Name.log"
    $Process = Start-Process msiexec.exe -ArgumentList (@($Operation, "`"$Package`"", '/qn', '/norestart', '/L*v', "`"$Log`"") + $Properties) -Wait -PassThru
    Assert ($Process.ExitCode -eq $Expected) "Installer $Name returned $($Process.ExitCode), expected $Expected. See $Log"
}
# Never overwrite a handler installed by another program.
$null = New-Item "$Handler/shell/open/command" -Force
Set-Item "$Handler/shell/open/command" -Value '"C:\existing-handler.exe" "%1"'
Installer /i $Msi collision -Expected 1603
Assert ((Get-Item "$Handler/shell/open/command").GetValue('') -eq '"C:\existing-handler.exe" "%1"') 'Foreign handler was changed'
Remove-Item -LiteralPath $Handler -Recurse # Only the synthetic key created above.
Installer /i $Msi machine-context -Properties @('ALLUSERS=1') -Expected 1603
Installer /i $Msi install
Assert (Test-Path "$Install/gdap-acceptor.exe") 'Per-user launcher missing'
Assert ((Get-Item "$Handler/shell/open/command").GetValue('') -eq "`"$Install\gdap-acceptor.exe`" `"%1`"".Replace('/', '\')) 'Wrong handler command'
& "$Install/gdap-acceptor.exe" self-test
Assert ($LASTEXITCODE -eq 0) 'Installed launcher contracts failed'
$null = New-Item -ItemType Directory $State
[IO.File]::WriteAllText("$State/instances.json", '{}')
Installer /i $UpgradeMsi upgrade
Installer /i $Msi downgrade -Expected 1603
Assert ([IO.File]::ReadAllText("$State/instances.json") -eq '{}') 'Upgrade changed local enrollment'
& "$Install/gdap-acceptor.exe" self-test
Assert ($LASTEXITCODE -eq 0) 'Upgraded launcher contracts failed'
Installer /x $UpgradeMsi uninstall
Assert (-not (Test-Path "$Install/gdap-acceptor.exe")) 'Uninstall left owned executable'
Assert (-not (Test-Path "$Handler/shell/open/command") -or -not (Get-Item "$Handler/shell/open/command").GetValue('')) 'Uninstall left active protocol handler'
Assert ([IO.File]::ReadAllText("$State/instances.json") -eq '{}') 'Uninstall removed user enrollment'
Write-Output 'PASS: per-user install, handler collision, context guard, upgrade, downgrade rejection, uninstall and enrollment retention'
