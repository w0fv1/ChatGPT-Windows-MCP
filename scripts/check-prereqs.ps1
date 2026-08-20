Write-Host "=== ChatGPT-Windows-MCP prerequisite check ==="

Write-Host "`nOS:"
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, OSArchitecture | Format-List

Write-Host "dotnet:"
dotnet --version

Write-Host "`nuv:"
try { uv --version } catch { Write-Warning "uv not found" }

Write-Host "`nwinget:"
try { winget --version } catch { Write-Warning "winget not found" }

Write-Host "`nPort 8000:"
try { Get-NetTCPConnection -LocalPort 8000 -ErrorAction Stop | Format-Table -AutoSize }
catch { Write-Host "Port 8000 is free." }
