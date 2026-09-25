param([string] $AppDirectory)
# This does not invoke a Bluetooth or output-changing command.
. "$PSScriptRoot/common.ps1"
$app = if ($AppDirectory) { (Resolve-Path -LiteralPath $AppDirectory -ErrorAction Stop).Path }
       else { Join-Path $RepoRoot 'artifacts/app' }
$exe = Join-Path $app 'BtSwitcherExtension.exe'
if (-not (Test-Path $exe)) { throw 'Run scripts/build.ps1 first.' }
$resultFile = Join-Path $RepoRoot 'artifacts/extension-smoke.json'
# Remove only the previous result so stale success cannot be reported.
if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile }
$process = Start-Process -FilePath $exe -ArgumentList '--smoke-test',('"' + $resultFile + '"') -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(30000)) {
    throw "Read-only smoke process is still running (PID $($process.Id)). Inspect that process before retrying."
}
if (-not (Test-Path $resultFile)) { throw "Smoke test produced no result (exit $($process.ExitCode))." }
$result = Get-Content -Raw -LiteralPath $resultFile | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $result.Passed) { throw $result.Error }
$result | ConvertTo-Json -Depth 5
