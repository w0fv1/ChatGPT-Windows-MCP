$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the .NET 10 SDK before running this verification script.'
    }
    if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        throw 'Run this full WPF verification on Windows. The regression console alone is cross-platform.'
    }
    & dotnet build .\src\ChatGPTWindowsMcp\ChatGPTWindowsMcp.csproj -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Release build failed: $LASTEXITCODE" }
    & dotnet run --project .\tests\Launcher.RegressionTests\Launcher.RegressionTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Regression tests failed: $LASTEXITCODE" }
    Write-Host 'Build and regression console passed. ChatGPT/Tunnel end-to-end validation is still required.'
} finally {
    Pop-Location
}
