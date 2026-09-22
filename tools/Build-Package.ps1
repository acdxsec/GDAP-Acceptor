[CmdletBinding()]
param([ValidateSet('linux-x64','win-x64')][string]$Runtime = 'linux-x64', [Parameter(Mandatory)][string]$UpstreamSource, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$Pin = '21e8728b9491eda1c13e1e05ce03678ca75d64cc'
$Head = & git -C $UpstreamSource rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $Head -ne $Pin) { throw 'M365Internals checkout does not match the reviewed source pin.' }
$Changes = & git -C $UpstreamSource status --porcelain -- M365Internals
if ($LASTEXITCODE -ne 0 -or $Changes) { throw 'The vendored module must be an unmodified reviewed snapshot.' }
$Destination = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $Root "dist/$Runtime" }
if (Test-Path -LiteralPath $Destination) { throw 'Output directory already exists. Choose a fresh OutputDirectory to avoid mixing old and new payload files.' }
& dotnet publish "$Root/src/GdapAcceptor.csproj" -c Release -r $Runtime --self-contained true -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'Native launcher build failed.' }
Copy-Item "$Root/scripts" $Destination -Recurse -Force
Copy-Item "$UpstreamSource/M365Internals" $Destination -Recurse -Force
$License = Get-ChildItem $UpstreamSource -File | Where-Object Name -Match '^LICENSE(\.md|\.txt)?$' | Select-Object -First 1
if (-not $License) { throw 'Required upstream MIT notice was not found.' }
Copy-Item $License.FullName "$Destination/M365Internals-LICENSE.txt" -Force
Copy-Item "$Root/README.md" $Destination -Force
Copy-Item "$Root/docs" $Destination -Recurse -Force
Copy-Item "$Root/LICENSE" "$Destination/LICENSE" -Force
if (-not (Test-Path "$Destination/scripts/GdapInvitationBrowser.ps1")) { throw 'The invitation-browser helper was not bundled.' }
if (-not (Test-Path "$Destination/scripts/GdapHttpDiagnostics.ps1")) { throw 'The bounded HTTP diagnostics helper was not bundled.' }
Write-Output "Assembled development package: $Destination. Signing and release gates have not been satisfied."
