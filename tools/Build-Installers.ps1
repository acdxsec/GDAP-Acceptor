[CmdletBinding()]
param(
    [string]$WindowsPayload = "$PSScriptRoot/../dist/win-x64",
    [string]$LinuxPayload = "$PSScriptRoot/../dist/linux-x64",
    [string]$OutputDirectory = "$PSScriptRoot/../dist/installers",
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.6',
    [switch]$SkipMsiCompilation
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
foreach ($Payload in @($WindowsPayload, $LinuxPayload)) {
    if (-not (Test-Path -LiteralPath "$Payload/scripts/Invoke-Acceptance.ps1") -or
        -not (Test-Path -LiteralPath "$Payload/scripts/GdapInvitationBrowser.ps1") -or
        -not (Test-Path -LiteralPath "$Payload/scripts/GdapHttpDiagnostics.ps1") -or
        -not (Test-Path -LiteralPath "$Payload/M365Internals/M365Internals.psd1") -or
        -not (Test-Path -LiteralPath "$Payload/M365Internals-LICENSE.txt")) { throw 'A complete pinned-source payload is required.' }
    if (Get-ChildItem -LiteralPath $Payload -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Payload links are not permitted.' }
}
if (-not (Test-Path "$WindowsPayload/gdap-acceptor.exe") -or -not (Test-Path "$LinuxPayload/gdap-acceptor")) { throw 'Native launchers are missing.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$Stage = Join-Path $OutputDirectory ('stage-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $Stage

# Debian package: no root-time desktop/session modification or maintainer script.
$DebRoot = Join-Path $Stage 'deb'
foreach ($Path in @('DEBIAN','opt/gdap-acceptor','usr/share/applications','usr/share/doc/gdap-acceptor')) {
    $null = New-Item -ItemType Directory -Path (Join-Path $DebRoot $Path) -Force
}
Copy-Item -Path "$LinuxPayload/*" -Destination "$DebRoot/opt/gdap-acceptor" -Recurse
$Control = [IO.File]::ReadAllText("$Root/packaging/linux/control").Replace('Version: 0.1.0', "Version: $Version")
[IO.File]::WriteAllText("$DebRoot/DEBIAN/control", $Control, [Text.UTF8Encoding]::new($false))
Copy-Item "$Root/packaging/linux/gdap-acceptor.desktop" "$DebRoot/usr/share/applications/"
Copy-Item "$Root/LICENSE" "$DebRoot/usr/share/doc/gdap-acceptor/copyright"
Copy-Item "$LinuxPayload/M365Internals-LICENSE.txt" "$DebRoot/usr/share/doc/gdap-acceptor/"
if ($IsLinux) {
    $DirMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor [IO.UnixFileMode]::OtherExecute
    $FileMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::OtherRead
    [IO.File]::SetUnixFileMode($DebRoot, $DirMode)
    foreach ($Item in Get-ChildItem $DebRoot -Recurse -Force) { [IO.File]::SetUnixFileMode($Item.FullName, $(if ($Item.PSIsContainer) { $DirMode } else { $FileMode })) }
    [IO.File]::SetUnixFileMode("$DebRoot/opt/gdap-acceptor/gdap-acceptor", $DirMode)
} else { throw 'Build installers on Linux so Debian file modes can be set explicitly.' }
$Deb = "$OutputDirectory/gdap-acceptor_${Version}_amd64.deb"
if (Test-Path $Deb) { throw 'Refusing to overwrite an existing package; use a fresh output directory or version.' }
& dpkg-deb --root-owner-group --build $DebRoot $Deb
if ($LASTEXITCODE -ne 0) { throw 'Debian package build failed.' }

# MSI uses GNOME wixl (open-source msitools), not the commercial-maintenance WiX
# distribution. One file per stable component with an HKCU registry key path.
function StableId([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).Substring(0, 32) }
function StableGuid([string]$Text) { ([guid]::ParseExact((StableId $Text), 'N')).ToString('B').ToUpperInvariant() }
$Xml = [xml]@'
<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
  <Product Id="*" Name="GDAP Acceptor (Development)" Language="1033" Version="0.1.0" Manufacturer="acdxsec" UpgradeCode="{906197FD-8198-49B0-8E30-22036EE64023}">
    <Package InstallerVersion="500" Compressed="yes" InstallScope="perUser" />
    <MajorUpgrade DowngradeErrorMessage="A newer GDAP Acceptor version is installed." />
    <InstallExecuteSequence><RemoveExistingProducts After="InstallInitialize" /></InstallExecuteSequence>
    <Media Id="1" Cabinet="payload.cab" EmbedCab="yes" />
    <Property Id="GDAPEXISTINGHANDLER"><RegistrySearch Id="ExistingHandler" Root="HKCU" Key="Software\Classes\gdap-acceptor\shell\open\command" Name="" Type="raw" Win64="yes" /></Property>
    <Property Id="GDAPOWNEDHANDLER"><RegistrySearch Id="OwnedHandler" Root="HKCU" Key="Software\acdxsec\GDAP Acceptor" Name="InstallerOwned" Type="raw" Win64="yes" /></Property>
    <Condition Message="GDAP Acceptor is per-user only; ALLUSERS must not be set.">NOT ALLUSERS</Condition>
    <Condition Message="Another gdap-acceptor handler exists. Inspect and remove it before installation.">Installed OR NOT GDAPEXISTINGHANDLER OR GDAPOWNEDHANDLER</Condition>
    <Directory Id="TARGETDIR" Name="SourceDir"><Directory Id="LocalAppDataFolder"><Directory Id="UserPrograms" Name="Programs"><Directory Id="INSTALLDIR" Name="GDAP Acceptor" /></Directory></Directory></Directory>
    <Feature Id="Complete" Title="GDAP Acceptor" Level="1" />
  </Product>
</Wix>
'@
$Ns = $Xml.DocumentElement.NamespaceURI
function Element($Parent, [string]$Name, [hashtable]$Attributes) {
    $Node = $Xml.CreateElement($Name, $Ns)
    foreach ($Key in $Attributes.Keys) { $Node.SetAttribute($Key, [string]$Attributes[$Key]) }
    $null = $Parent.AppendChild($Node)
    return $Node
}
$Product = $Xml.DocumentElement.FirstChild
$Product.SetAttribute('Version', $Version)
$Product.SetAttribute('Id', (StableGuid "product-x64-per-user-$Version"))
$Install = $Product.SelectSingleNode(".//*[local-name()='Directory' and @Id='INSTALLDIR']")
$Feature = $Product.SelectSingleNode("./*[local-name()='Feature']")
$Directories = @{ ''=$Install }
$Names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$WindowsPayload = (Resolve-Path -LiteralPath $WindowsPayload).Path
foreach ($File in Get-ChildItem -LiteralPath $WindowsPayload -Recurse -File | Sort-Object FullName) {
    $Relative = [IO.Path]::GetRelativePath($WindowsPayload, $File.FullName).Replace('\','/')
    if (-not $Names.Add($Relative) -or $Relative -match '[<>:"|?*]') { throw 'Windows payload has an ambiguous or unsupported path.' }
    $ParentPath = [IO.Path]::GetDirectoryName($Relative).Replace('\','/')
    $CurrentPath = ''; $Parent = $Install
    foreach ($Part in $ParentPath.Split('/', [StringSplitOptions]::RemoveEmptyEntries)) {
        $CurrentPath = if ($CurrentPath) { "$CurrentPath/$Part" } else { $Part }
        if (-not $Directories.ContainsKey($CurrentPath)) { $Directories[$CurrentPath] = Element $Parent 'Directory' @{ Id=('d' + (StableId $CurrentPath)); Name=$Part } }
        $Parent = $Directories[$CurrentPath]
    }
    $Hash = StableId $Relative
    $Component = Element $Parent 'Component' @{ Id="c$Hash"; Guid=(StableGuid "component-x64-per-user-$Relative"); Win64='yes' }
    $null = Element $Component 'File' @{ Id="f$Hash"; Source=$File.FullName; Name=$File.Name; DiskId='1' }
    $null = Element $Component 'RegistryValue' @{ Root='HKCU'; Key='Software\acdxsec\GDAP Acceptor\Files'; Name=$Hash; Type='string'; Value="[#f$Hash]"; KeyPath='yes' }
    $null = Element $Component 'RemoveFolder' @{ Id="r$Hash"; On='uninstall' }
    $null = Element $Feature 'ComponentRef' @{ Id="c$Hash" }
}
$Protocol = Element $Install 'Component' @{ Id='Protocol'; Guid='{7D304515-009B-4F6C-B581-57CDD4E10EAE}'; Win64='yes' }
$null = Element $Protocol 'RegistryValue' @{ Root='HKCU'; Key='Software\acdxsec\GDAP Acceptor'; Name='InstallerOwned'; Type='integer'; Value='1'; KeyPath='yes' }
$null = Element $Protocol 'RegistryValue' @{ Root='HKCU'; Key='Software\Classes\gdap-acceptor'; Type='string'; Value='URL:GDAP Acceptor' }
$null = Element $Protocol 'RegistryValue' @{ Root='HKCU'; Key='Software\Classes\gdap-acceptor'; Name='URL Protocol'; Type='string'; Value='' }
$null = Element $Protocol 'RegistryValue' @{ Root='HKCU'; Key='Software\Classes\gdap-acceptor\shell\open\command'; Type='string'; Value='"[INSTALLDIR]gdap-acceptor.exe" "%1"' }
$null = Element $Feature 'ComponentRef' @{ Id='Protocol' }
$MenuRoot = Element ($Product.SelectSingleNode("./*[local-name()='Directory']")) 'Directory' @{ Id='ProgramMenuFolder' }
$Menu = Element $MenuRoot 'Directory' @{ Id='AcceptorMenu'; Name='GDAP Acceptor' }
$Shortcut = Element $Menu 'Component' @{ Id='StartMenuShortcut'; Guid='{C0E97BB2-8209-4D7C-B7C6-09A6B044AFAB}'; Win64='yes' }
$null = Element $Shortcut 'Shortcut' @{ Id='LaunchAcceptor'; Name='GDAP Acceptor'; Target='[INSTALLDIR]gdap-acceptor.exe'; WorkingDirectory='INSTALLDIR' }
$null = Element $Shortcut 'RegistryValue' @{ Root='HKCU'; Key='Software\acdxsec\GDAP Acceptor'; Name='StartMenuShortcut'; Type='integer'; Value='1'; KeyPath='yes' }
$null = Element $Shortcut 'RemoveFolder' @{ Id='RemoveAcceptorMenu'; On='uninstall' }
$null = Element $Feature 'ComponentRef' @{ Id='StartMenuShortcut' }
$Source = "$OutputDirectory/gdap-acceptor-$Version.wxs"
if (Test-Path $Source) { throw 'Refusing to overwrite existing MSI authoring.' }
$Xml.Save($Source)
if (-not $SkipMsiCompilation) {
    $Msi = "$OutputDirectory/gdap-acceptor-$Version-x64.msi"
    if (Test-Path $Msi) { throw 'Refusing to overwrite an existing MSI.' }
    $CompilerOutput = & wixl -a x64 -o $Msi $Source 2>&1
    if ($LASTEXITCODE -ne 0 -or ($CompilerOutput -join "`n") -match '(?i)warning|critical|error') { throw "MSI compilation failed or emitted diagnostics: $CompilerOutput" }
}
Write-Output "Unsigned development installers: $OutputDirectory. No signing or APT publication was performed."
