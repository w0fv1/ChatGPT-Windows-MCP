param([string]$Proxy = "")
$ErrorActionPreference = "Stop"
$version = "0.12.21"
$root = Split-Path -Parent $PSScriptRoot
$bundle = Join-Path $root "tools\bundled-uv\win-x64"
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
$manifestPath = Join-Path $bundle "manifest.json"
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $valid = $manifest.version -eq $version
    foreach ($name in @("uv.exe", "uvx.exe", "LICENSE-MIT", "LICENSE-APACHE")) {
        $file = Join-Path $bundle $name
        if (!(Test-Path -LiteralPath $file) -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $manifest.files.$name) { $valid = $false }
    }
    if ($valid) { Write-Host "Verified bundled uv $version is cached."; return }
}
$options = @{ TimeoutSec = 180 }
if ($Proxy) { $options.Proxy = $Proxy }
$url = "https://github.com/astral-sh/uv/releases/download/$version/uv-x86_64-pc-windows-msvc.zip"
$archive = Join-Path $bundle "uv.zip"
Invoke-WebRequest -Uri $url -OutFile $archive @options
$checksum = (Invoke-WebRequest -Uri "$url.sha256" @options).Content
if ($checksum -is [byte[]]) { $checksum = [Text.Encoding]::UTF8.GetString($checksum) }
$expected = ($checksum.Trim() -split '\s+')[0]
if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    throw "Official uv archive SHA-256 verification failed."
}
$expanded = Join-Path $bundle "archive"
Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
foreach ($name in @("uv.exe", "uvx.exe")) {
    $matches = @(Get-ChildItem -LiteralPath $expanded -Recurse -File | Where-Object Name -eq $name)
    if ($matches.Count -ne 1) { throw "Expected exactly one $name in official archive." }
    Copy-Item -LiteralPath $matches[0].FullName -Destination (Join-Path $bundle $name) -Force
}
foreach ($name in @("LICENSE-MIT", "LICENSE-APACHE")) {
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/astral-sh/uv/$version/$name" -OutFile (Join-Path $bundle $name) @options
}
$hashes = [ordered]@{}
foreach ($name in @("uv.exe", "uvx.exe", "LICENSE-MIT", "LICENSE-APACHE")) {
    $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $bundle $name) -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{ version = $version; architecture = "x64"; archiveSha256 = $expected; files = $hashes } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Bundled uv $version downloaded and verified."
