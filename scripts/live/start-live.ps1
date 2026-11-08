# Starts the live-only API from the frozen publish. Configuration, credentials and data paths come from src/Trading.Api (the content root).
param([string]$Bin = "C:\AutoTrader-live\bin", [int]$Port = 5080)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
if (-not (Test-Path "$Bin\Trading.Api.dll")) { throw "No published build at $Bin. Run scripts\live\publish-live.ps1 first." }
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is already in use." }
Get-Content "$Bin\BUILD.txt" | ForEach-Object { Write-Host "[live] $_" }
$env:AutoTrader__Mode = "Live"
$env:LivePaper__ConnectOnStartup = "true"
$env:ASPNETCORE_ENVIRONMENT = "Production"
dotnet "$Bin\Trading.Api.dll" --contentRoot "$repo\src\Trading.Api" --urls "http://localhost:$Port"
