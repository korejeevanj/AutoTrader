# Pre-market checklist from GET /api/live/readiness. Exit code 0 = ready, 1 = not ready. -Watch repeats every 15 s until ready.
param([string]$Url = "http://localhost:5080", [switch]$Watch)
do {
    try { $r = Invoke-RestMethod "$Url/api/live/readiness" -TimeoutSec 5 }
    catch { Write-Host "NOT REACHABLE: live API at $Url is not running." -ForegroundColor Red; if (-not $Watch) { exit 1 }; Start-Sleep 15; continue }
    Write-Host ("{0:HH:mm:ss}  phase {1}  severity {2}" -f (Get-Date), $r.phase, $r.severity)
    foreach ($c in $r.checks) { Write-Host ("  {0,-4} {1,-28} {2}" -f $(if ($c.ok) { 'ok' } else { 'FAIL' }), $c.code, $c.detail) -ForegroundColor $(if ($c.ok) { 'Green' } else { 'Red' }) }
    if ($r.ready) { Write-Host "READY" -ForegroundColor Green; exit 0 }
    if ($Watch) { Start-Sleep 15 }
} while ($Watch)
exit 1
