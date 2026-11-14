<#
.SYNOPSIS
  Launches a program in the logged-on user's INTERACTIVE desktop session (needed for the Cloud Files provider
  and tray icons) from a non-interactive context such as an SSH session, via a one-shot scheduled task.
  Standard output and error go to -Log so they can be tailed remotely.
.EXAMPLE
  .\start-in-session.ps1 -Exe C:\Users\<PC_USER>\nodrive-spikes\cfapi-minimal\cfapi-minimal.exe -Arguments '--top 200' -Log C:\Users\<PC_USER>\nodrive-spikes\results\cfapi.log
  Get-Content -Wait C:\Users\<PC_USER>\nodrive-spikes\results\cfapi.log
.EXAMPLE
  .\start-in-session.ps1 -Stop            # ends the task and kills the process it started
#>
param(
    [string] $Exe,
    [string] $Arguments = '',
    [string] $Log = "$env:USERPROFILE\nodrive-spikes\results\session.log",
    [string] $TaskName = 'NoDriveSpike',
    [string] $EnvVars = '',   # e.g. 'NODRIVE_BATCH=250 NODRIVE_PAR=4' (set for the launched process only)
    [switch] $Stop
)
$ErrorActionPreference = 'Stop'
if ($Stop) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Get-Process cfapi-minimal, aot-baseline -ErrorAction SilentlyContinue | Stop-Process -Force
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "stopped"
    return
}
if (-not $Exe) { throw "-Exe is required" }
New-Item -ItemType Directory -Force (Split-Path $Log) | Out-Null
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

# cmd.exe does the redirection so the exe's own console output lands in the log.
$envPrefix = ($EnvVars -split '\s+' | Where-Object { $_ } | ForEach-Object { "set $_&& " }) -join ''
$cmdArgs = "/c `"$envPrefix`"$Exe`" $Arguments > `"$Log`" 2>&1`""
$action    = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument $cmdArgs -WorkingDirectory (Split-Path $Exe)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 12) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2
$state = (Get-ScheduledTask -TaskName $TaskName).State
Write-Host "task $TaskName state=$state  log=$Log"
Write-Host "tail with: Get-Content -Wait '$Log'"
