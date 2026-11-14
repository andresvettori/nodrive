<#
.SYNOPSIS
  P0 step 7 on the Windows PC. Sections: faults (stall / 429 / reset / ignorerange / slow), kill (provider killed mid-hydration then restarted),
  load (4 concurrent hydrations). While each scenario runs, a probe lists a populated folder and stats a file every 100 ms and the script
  reports its latency, because the requirement is that ordinary Explorer-style operations never wait on a hydration problem.
#>
param([ValidateSet('faults', 'kill', 'load', 'all')] [string] $Section = 'all',
      [string] $Dev = "$env:USERPROFILE\nodrive-dev",
      [int] $Port = 5799)
$ErrorActionPreference = 'Continue'
$root = "$env:USERPROFILE\NoDriveSpike"
$launcher = 'C:\Projects\Personal\nodrive\spikes\remote\start-in-session.ps1'
$providerExe = "$Dev\cfapi-index\cfapi-minimal.exe"
$index = "$Dev\index500k.db"
$reader = 'C:\Projects\Personal\nodrive\spikes\remote\hydrate-reader.ps1'
$base = "http://127.0.0.1:$Port"
New-Item -ItemType Directory -Force "$Dev\results" | Out-Null

function Stop-Provider { & $launcher -Stop | Out-Null; Start-Sleep 2 }
function Start-Provider([string] $fault, [string] $tag) {
    Stop-Provider
    $log = "$Dev\results\resp-$tag.log"
    & $launcher -Exe $providerExe -Arguments "--index $index" -Log $log -EnvVars "NODRIVE_DOWNLOAD_BASE=$base NODRIVE_FAULT=$fault" | Out-Null
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep 1; if ((Get-Content $log -EA SilentlyContinue) -match 'ready') { break } }
    return $log
}
function Get-Targets([int] $count, [int] $minMB, [int] $maxMB, [int] $skip) {
    $dir = Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '^Reuni.n 7$' } | Select-Object -First 1
    Get-ChildItem -LiteralPath $dir.FullName -File -Force | Where-Object { ($_.Attributes -band 0x400000) -and $_.Length -ge ($minMB * 1MB) -and $_.Length -le ($maxMB * 1MB) } | Select-Object -Skip $skip -First $count
}
function Start-Reader([string] $path, [string] $out) {
    Remove-Item $out -EA SilentlyContinue
    Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $reader, '-Path', "`"$path`"", '-Out', $out -WindowStyle Hidden -PassThru
}
function Probe-Until([scriptblock] $done, [int] $maxSeconds) {
    $lat = New-Object System.Collections.Generic.List[double]
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $small = Get-ChildItem -LiteralPath $root -Directory | Select-Object -First 1
    while ($sw.Elapsed.TotalSeconds -lt $maxSeconds -and -not (& $done)) {
        $t = [Diagnostics.Stopwatch]::StartNew()
        $null = @(Get-ChildItem -LiteralPath $root -Force).Count
        $null = Get-Item -LiteralPath $small.FullName
        $lat.Add($t.Elapsed.TotalMilliseconds)
        Start-Sleep -Milliseconds 100
    }
    $s = $lat | Sort-Object
    if ($s.Count -eq 0) { return 'probe: none' }
    "probe {0} ops: median {1:N1} ms, p95 {2:N1} ms, max {3:N1} ms" -f $s.Count, $s[[int]($s.Count / 2)], $s[[int]($s.Count * 0.95)], $s[-1]
}

# fake Graph/download server as a separate process
$serverExe = "$Dev\scale\scale-500k.exe"
Get-Process scale-500k -EA SilentlyContinue | Stop-Process -Force
$server = Start-Process $serverExe -ArgumentList 'serve', '--items', '500000', '--page-size', '1000', '--port', $Port -WindowStyle Hidden -PassThru -RedirectStandardOutput "$Dev\results\resp-server.log"
Start-Sleep 3

if ($Section -in 'faults', 'all') {
    "=== FAULT MATRIX: one 3-40 MB placeholder per scenario, read fully by a separate process ==="
    $skip = 0
    foreach ($fault in 'none', 'ignorerange', '429', 'reset', 'stall', 'slow') {
        $log = Start-Provider $fault "fault-$fault"
        $target = Get-Targets 1 3 40 $skip; $skip += 3
        $out = "$Dev\results\reader-$fault.txt"
        $proc = Start-Reader $target.FullName $out
        $wait = if ($fault -in 'stall', 'slow') { 100 } else { 60 }
        $probe = Probe-Until { $proc.HasExited } $wait
        if (-not $proc.HasExited) { $proc | Stop-Process -Force; $res = "reader still blocked after $wait s (killed by the test)" } else { $res = (Get-Content $out) -join ' ' }
        $alive = [bool](Get-Process cfapi-minimal -EA SilentlyContinue)
        "[{0,-11}] {1}`n               {2}`n               provider alive: {3}" -f $fault, $res, $probe, $alive
        Get-Content $log | Select-String '\[hydrate\] (FAILED|throttled|completed)' | Select-Object -Last 2 | % { "               log: " + $_.Line }
    }
}

if ($Section -in 'kill', 'all') {
    "=== KILL / RESTART: provider killed 5 s into a slow hydration (256 KiB/s), then restarted ==="
    $log = Start-Provider 'slow' 'kill'
    $target = Get-Targets 1 10 40 20
    $out = "$Dev\results\reader-kill.txt"
    $proc = Start-Reader $target.FullName $out
    Start-Sleep 5
    $killAt = Get-Date
    Get-Process cfapi-minimal | Stop-Process -Force
    $probe = Probe-Until { $proc.HasExited } 40
    $took = ((Get-Date) - $killAt).TotalSeconds
    if (-not $proc.HasExited) { $proc | Stop-Process -Force; "reader still blocked 40 s after the kill" } else { "reader result {0:N1} s after the kill: {1}" -f $took, ((Get-Content $out) -join " ") }
    $probe
    $listing = [Diagnostics.Stopwatch]::StartNew(); $null = @(Get-ChildItem -LiteralPath $root).Count; "listing with the provider dead: {0} ms (populated folders need no provider)" -f $listing.ElapsedMilliseconds
    $log = Start-Provider 'none' 'kill2'
    $proc = Start-Reader $target.FullName "$Dev\results\reader-kill2.txt"
    Probe-Until { $proc.HasExited } 60 | Out-Null
    "after restart, same file: " + $(if ($proc.HasExited) { (Get-Content "$Dev\results\reader-kill2.txt") -join ' ' } else { 'STILL BLOCKED' })
}

if ($Section -in 'load', 'all') {
    "=== LOAD: 4 concurrent hydrations of 8-18 MB placeholders ==="
    $log = Start-Provider 'none' 'load'
    $targets = Get-Targets 4 8 18 40
    $procs = @(); $i = 0
    foreach ($tg in $targets) { $procs += Start-Reader $tg.FullName "$Dev\results\reader-load$i.txt"; $i++ }
    $peak = 0; $sampler = [Diagnostics.Stopwatch]::StartNew()
    $probe = Probe-Until { $p = Get-Process cfapi-minimal -EA SilentlyContinue; if ($p -and ($p.PrivateMemorySize64 / 1MB) -gt $script:peak) { $script:peak = $p.PrivateMemorySize64 / 1MB }; -not ($procs | Where-Object { -not $_.HasExited }) } 120
    $probe
    0..($i - 1) | % { "reader $_ : " + ((Get-Content "$Dev\results\reader-load$_.txt") -join " ") }
    "provider peak private during load: {0:N1} MB" -f $peak
    Get-Content $log | Select-String '\[latency\]' | Select-Object -Last 1 | % { $_.Line }
}

Stop-Provider
Get-Process scale-500k -EA SilentlyContinue | Stop-Process -Force
