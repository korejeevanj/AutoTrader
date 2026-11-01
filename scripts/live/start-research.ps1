# Starts the research API (cockpit, historical replay, read-only recordings, quote replays). It never controls the live run.
param([int]$Port = 5081)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:AutoTrader__Mode = "Research"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project "$repo\src\Trading.Api" --no-launch-profile --urls "http://localhost:$Port"
