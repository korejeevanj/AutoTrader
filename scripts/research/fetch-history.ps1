# Fetches minute candles for the context instruments through the running Research API (read-only Kite requests; needs a valid token in
# src/Trading.Api/kite.credentials.Local.json). Resumable: files already saved are skipped. Start the Research API first (scripts\live\start-research.ps1).
param([string]$From = "2023-01-02", [string]$To = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd"), [string]$Symbols = "NIFTY BANK,NIFTY 50,INDIA VIX", [int]$Port = 5081)
$base = "http://localhost:$Port/api/research/archive"
$null = Invoke-RestMethod -Method Post -Uri ("$base/index-history?from=$From&to=$To&symbols=" + [uri]::EscapeDataString($Symbols))
do {
    Start-Sleep 10
    $s = Invoke-RestMethod "$base/status"
    Write-Host ("{0:HH:mm:ss} {1} done {2}/{3} skipped {4} failed {5} {6}" -f (Get-Date), $s.state, $s.done, $s.total, $s.skipped, $s.failed, $s.lastError)
} while ($s.state -eq "Running")
if ($s.state -ne "Completed") { Write-Host "Finished with state $($s.state): $($s.lastError)" -ForegroundColor Red; exit 1 }
Write-Host "Done. Analyse with: python -I scripts/research/hypotheses.py data/research/index-minute/NIFTY_BANK --vix data/research/index-minute/INDIA_VIX --out data/research/reports/hypotheses.md"
