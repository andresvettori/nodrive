<#
.SYNOPSIS
  Cold-open benchmark of the big folder under different CfExecute(TRANSFER_PLACEHOLDERS) settings. For each (batch size, parallelism):
  restart the provider with a fresh registration (folder unpopulated), list the folder, read the provider's [populate] log line.
#>
param([string] $Exe = "$env:USERPROFILE\nodrive-dev\cfapi-index\cfapi-minimal.exe",
      [string] $Index = "$env:USERPROFILE\nodrive-dev\index500k.db",
      [string] $Launcher = 'C:\Projects\Personal\nodrive\spikes\remote\start-in-session.ps1',
      [string[]] $Configs = @('1000,1', '250,1', '4000,1', '1000,4', '500,8'))
$root = "$env:USERPROFILE\NoDriveSpike"
"{0,-14} {1,12} {2,14} {3,18}" -f 'batch,parallel', 'shell ms', 'provider ms', 'provider private MB'
foreach ($c in $Configs) {
    $b, $p = $c -split ','
    & $Launcher -Stop | Out-Null; Start-Sleep 2
    & $Exe --cleanup 2>&1 | Out-Null
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    $log = "$env:USERPROFILE\nodrive-dev\results\matrix-$b-$p.log"
    & $Launcher -Exe $Exe -Arguments "--index $Index" -Log $log -EnvVars "NODRIVE_BATCH=$b NODRIVE_PAR=$p" | Out-Null
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep 1; if ((Get-Content $log -EA SilentlyContinue) -match 'ready') { break } }
    Start-Sleep 2
    $big = Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '^Reuni.n 7$' } | Select-Object -First 1
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $n = @(Get-ChildItem -LiteralPath $big.FullName -Force).Count
    $shell = $sw.ElapsedMilliseconds
    Start-Sleep 1
    $line = Get-Content $log | Select-String 'folder=F61C08DC8B03ABFF!7 ' | Select-Object -Last 1
    $prov = if ($line -match 'total=(\d+) ms private=([\d.]+)') { "{0,14} {1,18}" -f $Matches[1], $Matches[2] } else { "{0,14} {1,18}" -f 'n/a (see log)', '' }
    "{0,-14} {1,12} {2}   entries={3}" -f "$b,$p", $shell, $prov, $n
}
& $Launcher -Stop | Out-Null
