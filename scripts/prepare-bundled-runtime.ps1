param([string]$Proxy = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$bundle = Join-Path $root "tools\bundled-runtime"
$payload = Join-Path $bundle "payload"
$uv = Join-Path $root "tools\bundled-uv\win-x64\uv.exe"
$pythonVersion = "3.13.14"
$mcpVersion = "0.8.5"
$requirementsHash = (Get-FileHash (Join-Path $PSScriptRoot "bundled-runtime-requirements.txt")).Hash.ToLowerInvariant()
$tunnelVersion = "v0.0.15"
New-Item $bundle -ItemType Directory -Force | Out-Null
$manifestPath = Join-Path $bundle "manifest.json"
$zip = Join-Path $bundle "runtime.zip"
if ((Test-Path $manifestPath) -and (Test-Path $zip)) {
    $cached = Get-Content $manifestPath -Raw | ConvertFrom-Json
    if ($cached.pythonVersion -eq $pythonVersion -and $cached.mcpVersion -eq $mcpVersion -and
        $cached.tunnelVersion -eq $tunnelVersion -and $cached.requirementsSha256 -eq $requirementsHash -and (Get-FileHash $zip).Hash -eq $cached.archiveSha256) {
        Write-Host "Verified full runtime bundle is cached."; return
    }
}
$oldProxy = $env:HTTPS_PROXY
try {
    if ($Proxy) { $env:HTTPS_PROXY = $Proxy }
    & $uv python install $pythonVersion --install-dir (Join-Path $bundle "managed-python")
    if ($LASTEXITCODE -ne 0) { throw "Python preparation failed." }
    $python = Join-Path $bundle "managed-python\cpython-$pythonVersion-windows-x86_64-none\python.exe"
    if (!(Test-Path $python)) { throw "Bundled Python not found." }
    New-Item $payload -ItemType Directory -Force | Out-Null
    $pythonRoot = Join-Path $payload "python"
    New-Item $pythonRoot -ItemType Directory -Force | Out-Null
    Get-ChildItem -LiteralPath (Split-Path $python) | Copy-Item -Destination $pythonRoot -Recurse -Force
    & $uv pip install --python (Join-Path $pythonRoot "python.exe") --target (Join-Path $pythonRoot "Lib\site-packages") -r (Join-Path $PSScriptRoot "bundled-runtime-requirements.txt")
    if ($LASTEXITCODE -ne 0) { throw "Windows-MCP dependencies preparation failed." }
    $options = @{ TimeoutSec = 240 }
    if ($Proxy) { $options.Proxy = $Proxy }
    $url = "https://github.com/openai/tunnel-client/releases/download/$tunnelVersion"
    $sumsPath = Join-Path $bundle "SHA256SUMS.txt"
    Invoke-WebRequest "$url/SHA256SUMS.txt" -OutFile $sumsPath @options
    $sums = Get-Content $sumsPath
    $tunnelDir = Join-Path $payload "tunnel"
    New-Item $tunnelDir -ItemType Directory -Force | Out-Null
    foreach ($name in @("tunnel-client-$tunnelVersion-windows-amd64.zip", "tunnel-client-$tunnelVersion-windows-amd64-licenses.txt", "tunnel-client-$tunnelVersion-windows-amd64.spdx.json")) {
        $download = Join-Path $bundle $name
        Invoke-WebRequest "$url/$name" -OutFile $download @options
        $line = @($sums | Where-Object { ($_ -split '\s+')[-1] -eq $name })
        if ($line.Count -ne 1 -or (Get-FileHash $download).Hash -ne ($line[0] -split '\s+')[0]) { throw "Official checksum failed: $name" }
        if ($name.EndsWith('.zip')) { Expand-Archive $download -DestinationPath $tunnelDir -Force }
        else { Copy-Item $download $tunnelDir -Force }
    }
    if (!(Test-Path (Join-Path $tunnelDir "tunnel-client.exe"))) { throw "Missing Tunnel executable." }
    # Entry points are loaded from package metadata; no absolute build-machine script paths.
    'from importlib.metadata import distribution; next(e for e in distribution("windows-mcp").entry_points if e.group == "console_scripts" and e.name == "windows-mcp").load()()' |
        Set-Content (Join-Path $payload "launch-mcp.py") -Encoding utf8
    $files = [ordered]@{}
    Get-ChildItem $payload -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' } | ForEach-Object {
        $name = $_.FullName.Substring($payload.Length + 1).Replace('\','/')
        $files[$name] = (Get-FileHash $_.FullName).Hash.ToLowerInvariant()
    }
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try { foreach ($name in $files.Keys) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $payload $name), $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null } }
    finally { $archive.Dispose() }
    @{ pythonVersion=$pythonVersion; mcpVersion=$mcpVersion; tunnelVersion=$tunnelVersion;
        archiveSha256=(Get-FileHash $zip).Hash.ToLowerInvariant(); requirementsSha256=$requirementsHash; files=$files } |
        ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding utf8
    Write-Host "Full runtime bundle prepared and verified."
} finally { $env:HTTPS_PROXY = $oldProxy }
