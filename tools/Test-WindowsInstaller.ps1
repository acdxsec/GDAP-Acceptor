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
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'GDAP Acceptor/GDAP Acceptor.lnk'
foreach ($Path in @($Install,$State,$Handler,$Owner,$Shortcut)) { if (Test-Path $Path) { throw "Refusing to touch pre-existing state: $Path" } }
$Msi = (Resolve-Path $Msi).Path; $UpgradeMsi = (Resolve-Path $UpgradeMsi).Path
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Installer([string]$Operation, [string]$Package, [string]$Name, [string[]]$Properties = @(), [int]$Expected = 0) {
    $Log = Join-Path $env:RUNNER_TEMP "gdap-msi-$Name.log"
    $Process = Start-Process msiexec.exe -ArgumentList (@($Operation, "`"$Package`"", '/qn', '/norestart', '/L*v', "`"$Log`"") + $Properties) -Wait -PassThru
    Assert ($Process.ExitCode -eq $Expected) "Installer $Name returned $($Process.ExitCode), expected $Expected. See $Log"
}
function CheckInstalledLauncher {
    Assert (Test-Path $Shortcut) 'Start Menu shortcut missing'
    $Shell = New-Object -ComObject WScript.Shell
    $Link = $Shell.CreateShortcut($Shortcut)
    Assert ($Link.TargetPath -eq "$Install\gdap-acceptor.exe".Replace('/', '\')) 'Start Menu shortcut points to a different launcher'
    # MSI directory properties include a trailing separator; shell shortcut
    # paths may retain it. Compare directories, not that serialization detail.
    $ObservedDirectory = [string]$Link.WorkingDirectory
    Assert (-not [string]::IsNullOrWhiteSpace($ObservedDirectory)) 'Start Menu working directory is empty'
    $ExpectedDirectory = [IO.Path]::GetFullPath($Install).TrimEnd([char[]]'\/')
    $ActualDirectory = [IO.Path]::GetFullPath($ObservedDirectory).TrimEnd([char[]]'\/')
    Assert ($ActualDirectory -eq $ExpectedDirectory) "Start Menu working directory is incorrect. Expected: $ExpectedDirectory; observed: $ActualDirectory"
    & "$Install/gdap-acceptor.exe" self-test
    Assert ($LASTEXITCODE -eq 0) 'Installed launcher contracts failed'
    $Help = & "$Install/gdap-acceptor.exe" --help
    Assert ($LASTEXITCODE -eq 0 -and ($Help -join "`n").Contains('queue resolve')) 'Installed CLI is missing reviewed recovery'
    & "$PSScriptRoot/Test-Wrapper.ps1" -Wrapper "$Install/scripts/Invoke-Acceptance.ps1"
    Assert ($LASTEXITCODE -eq 0) 'Installed wrapper contracts failed'
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
CheckInstalledLauncher
$null = New-Item -ItemType Directory $State
$Enrollment = '{"11111111-1111-1111-1111-111111111111":{"BaseUrl":"https://cipp.example","PartnerTenantId":"22222222-2222-2222-2222-222222222222"}}'
[IO.File]::WriteAllText("$State/instances.json", $Enrollment)
$Reservation = '{"Token":"33333333-3333-3333-3333-333333333333","Invitation":{"InstanceId":"11111111-1111-1111-1111-111111111111","RelationshipId":"synthetic-interrupted"},"Instance":{"BaseUrl":"https://cipp.example","PartnerTenantId":"22222222-2222-2222-2222-222222222222"}}'
[IO.File]::WriteAllText("$State/active-v1.json", $Reservation)
function CheckRetainedState {
    Assert ([IO.File]::ReadAllText("$State/instances.json") -ceq $Enrollment) 'Installer changed local enrollment'
    Assert ([IO.File]::ReadAllText("$State/active-v1.json") -ceq $Reservation) 'Installer cleared interrupted acceptance protection'
}
# This executable was installed by this test on a disposable runner. Prove that
# MSI repair restores a missing payload without clearing the user's queue.
Remove-Item -LiteralPath "$Install/gdap-acceptor.exe"
Assert (-not (Test-Path "$Install/gdap-acceptor.exe")) 'Repair fixture did not remove the test executable'
Installer /fa $Msi repair
CheckInstalledLauncher
CheckRetainedState
Installer /i $UpgradeMsi upgrade
Installer /i $Msi downgrade -Expected 1603
CheckRetainedState
CheckInstalledLauncher
Installer /x $UpgradeMsi uninstall
Assert (-not (Test-Path "$Install/gdap-acceptor.exe")) 'Uninstall left owned executable'
Assert (-not (Test-Path $Shortcut)) 'Uninstall left the Start Menu shortcut'
Assert (-not (Test-Path "$Handler/shell/open/command") -or -not (Get-Item "$Handler/shell/open/command").GetValue('')) 'Uninstall left active protocol handler'
CheckRetainedState
Write-Output 'PASS: per-user install, shortcut, CLI/wrapper, collision/context guards, repair, upgrade, downgrade rejection, uninstall and enrollment/interrupted-state retention'
