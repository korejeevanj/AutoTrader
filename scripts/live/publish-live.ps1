# Builds a frozen copy of the API for the live session. Run once per code change, never during market hours.
# The live process then runs these binaries all day (a fixed CodeVersion), so research rebuilds in the repo cannot touch it.
param([string]$Output = "C:\AutoTrader-live\bin")
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$running = Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'dotnet*' -and $_.CommandLine -like "*$Output*" }
if ($running) { throw "A live process is running from $Output (PID $($running.ProcessId)). Stop it before publishing." }
Push-Location $repo
try {
    dotnet build AutoTrader.slnx --no-restore --configuration Release
    if ($LASTEXITCODE) { throw "build failed" }
    dotnet publish src/Trading.Api/Trading.Api.csproj --no-restore --no-build --configuration Release --output $Output
    if ($LASTEXITCODE) { throw "publish failed" }
    $commit = (git rev-parse --short HEAD); $dirty = (git status --porcelain)
    "commit $commit$(if ($dirty) { ' (uncommitted changes present)' })`npublished $(Get-Date -Format o)" | Set-Content "$Output\BUILD.txt"
    Write-Host "Published $commit to $Output"
} finally { Pop-Location }
