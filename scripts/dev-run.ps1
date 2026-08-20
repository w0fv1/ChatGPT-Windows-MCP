$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $Root "src\ChatGPTWindowsMcp\ChatGPTWindowsMcp.csproj")
