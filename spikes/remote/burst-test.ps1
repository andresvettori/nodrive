<#
.SYNOPSIS
  Pins the largest online-only file under the spike root (-> CfHydratePlaceholder -> our FETCH_DATA worker), samples the
  provider process every 2 s until hydration finishes, then 60 s after, and prints peak and settle memory.
#>
param([string] $ProcessName = 'cfapi-minimal', [string] $Root = "$env:USERPROFILE\NoDriveSpike", [int] $MaxSeconds = 300)
function Sample { $p = Get-Process $ProcessName; $p.Refresh(); [pscustomobject]@{ Private = $p.PrivateMemorySize64/1MB; WS = $p.WorkingSet64/1MB; Threads = $p.Threads.Count } }
$f = Get-ChildItem $Root -Recurse -File | Where-Object { $_.Attributes -band 0x400000 } | Sort-Object Length -Descending | Select-Object -First 1
$base = Sample
"target: {0} ({1:N0} MB)" -f $f.Name, ($f.Length/1MB)
"baseline: private {0:N1} MB, ws {1:N1} MB, threads {2}" -f $base.Private, $base.WS, $base.Threads
$sw = [Diagnostics.Stopwatch]::StartNew(); $peakP = 0; $peakW = 0; $n = 0
attrib +p $f.FullName
while ($sw.Elapsed.TotalSeconds -lt $MaxSeconds) {
    Start-Sleep 2; $s = Sample; $n++
    if ($s.Private -gt $peakP) { $peakP = $s.Private }; if ($s.WS -gt $peakW) { $peakW = $s.WS }
    if ($n % 5 -eq 0) { "  t={0,4:N0}s private {1,6:N1} MB  ws {2,6:N1} MB  threads {3}" -f $sw.Elapsed.TotalSeconds, $s.Private, $s.WS, $s.Threads }
    if (-not ((Get-Item $f.FullName).Attributes -band 0x400000)) { break }
}
$secs = $sw.Elapsed.TotalSeconds
"HYDRATED {0:N0} MB in {1:N1} s ({2:N1} MB/s). peak private {3:N1} MB (+{4:N1}), peak ws {5:N1} MB" -f ($f.Length/1MB), $secs, ($f.Length/1MB/$secs), $peakP, ($peakP-$base.Private), $peakW
foreach ($t in 5, 15, 30, 60) {
    Start-Sleep ($t - $(if ($t -eq 5) { 0 } else { @{15=5;30=15;60=30}[$t] }))
    $s = Sample; "  after-burst +{0,2}s private {1,6:N1} MB  ws {2,6:N1} MB  threads {3}" -f $t, $s.Private, $s.WS, $s.Threads
}
$end = Sample
"settled: private {0:N1} MB vs baseline {1:N1} MB (delta {2:+0.0;-0.0} MB); ws {3:N1} MB vs {4:N1}" -f $end.Private, $base.Private, ($end.Private-$base.Private), $end.WS, $base.WS
attrib -p +u $f.FullName; Start-Sleep 3
"unpinned: online-only again = " + [bool]((Get-Item $f.FullName).Attributes -band 0x400000)
