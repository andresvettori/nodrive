<#
.SYNOPSIS
  Samples memory, thread and handle counts of a process every N seconds and writes a CSV.
.EXAMPLE
  .\spikes\measure.ps1 -ProcessName aot-baseline -Minutes 10 -Out results\aot-tray.csv
#>
param(
    [Parameter(Mandatory = $true)] [string] $ProcessName,
    [int] $Minutes = 10,
    [int] $IntervalSeconds = 10,
    [string] $Out = "results\$ProcessName-$(Get-Date -Format yyyyMMdd-HHmmss).csv"
)

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

"timestamp,elapsed_s,private_bytes_mb,working_set_mb,paged_mem_mb,threads,handles" | Set-Content $Out
$start = Get-Date
$deadline = $start.AddMinutes($Minutes)
while ((Get-Date) -lt $deadline) {
    $p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $p) {
        Write-Host "process '$ProcessName' not running; waiting..."
    } else {
        $p.Refresh()
        $elapsed = [int]((Get-Date) - $start).TotalSeconds
        $line = "{0:o},{1},{2:N1},{3:N1},{4:N1},{5},{6}" -f (Get-Date), $elapsed,
            ($p.PrivateMemorySize64 / 1MB), ($p.WorkingSet64 / 1MB), ($p.PagedMemorySize64 / 1MB),
            $p.Threads.Count, $p.HandleCount
        Add-Content $Out $line
        Write-Host $line
    }
    Start-Sleep -Seconds $IntervalSeconds
}
Write-Host "done: $Out"
