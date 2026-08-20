$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src\ChatGPTWindowsMcp\ChatGPTWindowsMcp.csproj"
$Dist = Join-Path $Root "dist"

if (Test-Path $Dist) {
    Remove-Item $Dist -Recurse -Force
}
New-Item -ItemType Directory -Path $Dist | Out-Null

dotnet publish $Project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o $Dist `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:DebugType=None `
  /p:DebugSymbols=false

Copy-Item (Join-Path $Root "config.example.json") (Join-Path $Dist "config.example.json")
Copy-Item (Join-Path $Root "README.md") (Join-Path $Dist "README.md")

Write-Host ""
Write-Host "Portable build created:" -ForegroundColor Green
Write-Host $Dist
Write-Host ""
Write-Host "Users can run ChatGPT-Windows-MCP.exe directly."
