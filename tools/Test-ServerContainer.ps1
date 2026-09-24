[CmdletBinding()]
param([ValidateSet('docker','podman')][string]$Engine = 'docker', [string]$Image = 'gdap-status:0.4.0')
$ErrorActionPreference = 'Stop'
$Fixture = Join-Path ([IO.Path]::GetTempPath()) ('gdap-server-container-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $Fixture
$Name = 'gdap-status-test-' + [guid]::NewGuid().ToString('N')
$Settings = Join-Path $Fixture 'settings.json'
$Secret = Join-Path $Fixture 'synthetic-secret'
$Config = @{
    IdentityTenantId = '11111111-1111-1111-1111-111111111111'
    Audience = '22222222-2222-2222-2222-222222222222'
    DesktopClientId = '33333333-3333-3333-3333-333333333333'
    CippOrigin = 'https://cipp.example'
    PartnerTenantId = '44444444-4444-4444-4444-444444444444'
    CippApiOrigin = 'https://api.cipp.example'
    CippAuthenticationTenantId = '55555555-5555-5555-5555-555555555555'
    CippClientId = '66666666-6666-6666-6666-666666666666'
    CippScope = 'api://66666666-6666-6666-6666-666666666666/.default'
}
[IO.File]::WriteAllText($Settings, ($Config | ConvertTo-Json))
[IO.File]::WriteAllText($Secret, 'synthetic-not-a-real-secret')
# Synthetic fixture permissions only; container UID 1654 must read the binds.
if (-not $IsWindows) { & chmod 755 $Fixture; & chmod 644 $Settings $Secret }
$Started = $false
$Client = [Net.Http.HttpClient]::new()
$Client.Timeout = [TimeSpan]::FromSeconds(3)
try {
    $null = & $Engine run --detach --name $Name --read-only --cap-drop ALL --security-opt no-new-privileges --tmpfs /tmp:size=32m -p 127.0.0.1::8080 --mount "type=bind,source=$Settings,target=/config/settings.json,readonly" --mount "type=bind,source=$Secret,target=/run/secrets/cipp-client-secret,readonly" $Image
    if ($LASTEXITCODE -ne 0) { throw 'Container did not start.' }
    $Started = $true
    $Address = (& $Engine port $Name 8080/tcp | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $Address -notmatch '^127\.0\.0\.1:\d+$') { throw 'Unexpected test listener.' }
    $Ready = $false
    for ($Attempt = 0; $Attempt -lt 20; $Attempt++) {
        try {
            $Response = $Client.GetAsync("http://$Address/healthz").GetAwaiter().GetResult()
            $Ready = [int]$Response.StatusCode -eq 200
            $Response.Dispose()
            if ($Ready) { break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $Ready) { throw 'Container did not become healthy.' }
    foreach ($Route in @('/v1/connection', '/v1/onboarding/relationship-1')) {
        $Request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "http://$Address$Route")
        $Request.Headers.Add('X-MS-CLIENT-PRINCIPAL', 'spoofed-admin')
        $Response = $Client.SendAsync($Request).GetAwaiter().GetResult()
        try { if ([int]$Response.StatusCode -ne 401) { throw 'Anonymous status access was not rejected.' } }
        finally { $Response.Dispose(); $Request.Dispose() }
    }
    $User = (& $Engine inspect --format '{{.Config.User}}' $Name).Trim()
    if ($User -ne '1654') { throw 'Server image did not run as the expected non-root user.' }
    Write-Output 'PASS: non-root read-only container starts, liveness works, protected routes reject anonymous/spoofed identities. No real credentials or approvals.'
} finally {
    $Client.Dispose()
    if ($Started) { $null = & $Engine rm --force $Name }
    [IO.File]::Delete($Secret)
    [IO.File]::Delete($Settings)
    [IO.Directory]::Delete($Fixture)
}
