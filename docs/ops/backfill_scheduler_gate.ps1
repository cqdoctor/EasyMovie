$ErrorActionPreference = 'Continue'
# EasyMovie score-backfill scheduler.
# ASCII ONLY: non-ASCII comments/strings break Windows PowerShell 5.1 parsing
# (verified 2026-10-08: a Chinese comment caused a parse error at the Out-File line).
# Ensure the runner can locate the real library at %LocalAppData%/EasyMovie
$env:LOCALAPPDATA = 'C:\Users\10638\AppData\Local'

$dll    = 'D:\project\EasyMovie\EasyMovie.BackfillRunner\bin\Debug\net10.0\EasyMovie.BackfillRunner.dll'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$out    = 'D:\project\EasyMovie\backfill_console.txt'
$probe  = 'D:\project\EasyMovie\_dryrun_probe.txt'
$diag   = 'D:\project\EasyMovie\_diag_run_time.txt'

# Step 0: read-only probe to decide whether this run has any real work.
# Background: after terminal-marking landed (2026-10-08), most runs have an empty
# queue (all 9 remaining movies are terminal). Starting unconditionally wastes a
# process spin, repeatedly opens two DB files, and emits a noisy report.
# --dry-run is read-only: no writes, no network.
# Exit codes: 0 = work pending, 3 = nothing to do, 2 = environment unusable.
& $dotnet $dll --dry-run *> $probe
$probeExit = $LASTEXITCODE

$t0 = Get-Date
"PROBE_EXIT=$probeExit" | Out-File -FilePath $diag -Encoding utf8

if ($probeExit -eq 3) {
    "START=$($t0.ToString('yyyy-MM-ddTHH:mm:ss'))" | Out-File -FilePath $diag -Encoding utf8 -Append
    "END=$($t0.ToString('yyyy-MM-ddTHH:mm:ss'))" | Out-File -FilePath $diag -Encoding utf8 -Append
    "DURATION_S=0" | Out-File -FilePath $diag -Encoding utf8 -Append
    "SKIP_REASON=no_pending_work" | Out-File -FilePath $diag -Encoding utf8 -Append
    "SKIPPED: no pending movies (all terminal or already rated); no network call was made."
    return 0
}

if ($probeExit -ne 0) {
    "START=$($t0.ToString('yyyy-MM-ddTHH:mm:ss'))" | Out-File -FilePath $diag -Encoding utf8 -Append
    "END=$($t0.ToString('yyyy-MM-ddTHH:mm:ss'))" | Out-File -FilePath $diag -Encoding utf8 -Append
    "DURATION_S=0" | Out-File -FilePath $diag -Encoding utf8 -Append
    "SKIP_REASON=probe_error_exit_$probeExit" | Out-File -FilePath $diag -Encoding utf8 -Append
    "SKIPPED: probe did not pass (exit=$probeExit); backfill not executed."
    return 0
}

# Work pending: run the real backfill
& $dotnet $dll *> $out
$exit = $LASTEXITCODE
$t1 = Get-Date

"EXITCODE=$exit" | Out-File -FilePath $diag -Encoding utf8 -Append
"START=$($t0.ToString('yyyy-MM-ddTHH:mm:ss'))" | Out-File -FilePath $diag -Encoding utf8 -Append
"END=$($t1.ToString('yyyy-MM-ddTHH:mm:ss'))" | Out-File -FilePath $diag -Encoding utf8 -Append
"DURATION_S=$([int]($t1 - $t0).TotalSeconds)" | Out-File -FilePath $diag -Encoding utf8 -Append
"RAN=1" | Out-File -FilePath $diag -Encoding utf8 -Append
"Backfill executed (exit=$exit). Details in backfill_console.txt"