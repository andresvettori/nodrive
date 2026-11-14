<#
.SYNOPSIS
  Reports the OneDrive state on this PC: linked accounts and their folders, whether OneDrive.exe is
  running, registered Cloud Files sync roots, whether Desktop/Documents/Pictures are redirected
  (OneDrive "Back up folders" / Known Folder Move), and policies that hide or block that feature.
  Read-only. Run as the normal user (not elevated).
.EXAMPLE
  .\spikes\onedrive-state.ps1
#>
$ErrorActionPreference = 'SilentlyContinue'
function Section($t) { Write-Host "`n== $t ==" -ForegroundColor Cyan }

Section "Windows"
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
"Build        : $($cv.CurrentBuild).$($cv.UBR)  ($($cv.DisplayVersion))"
"cldflt.sys   : $((Get-Item "$env:SystemRoot\System32\drivers\cldflt.sys").VersionInfo.FileVersion)"
$join = (dsregcmd /status) 2>$null
"Domain joined: $(($join | Select-String 'DomainJoined\s*:\s*(\w+)').Matches.Groups[1].Value)   Entra joined: $(($join | Select-String 'AzureAdJoined\s*:\s*(\w+)').Matches.Groups[1].Value)   Entra registered: $(($join | Select-String 'WorkplaceJoined\s*:\s*(\w+)').Matches.Groups[1].Value)"
"MDM enrolled : $(($join | Select-String 'MdmUrl\s*:\s*(\S+)').Matches.Count -gt 0)"

Section "OneDrive accounts (HKCU\Software\Microsoft\OneDrive\Accounts)"
$accounts = Get-ChildItem 'HKCU:\Software\Microsoft\OneDrive\Accounts'
if (-not $accounts) { "none linked" }
foreach ($a in $accounts) {
    $p = Get-ItemProperty $a.PSPath
    "{0,-10} email={1,-35} folder={2}" -f $a.PSChildName, $p.UserEmail, $p.UserFolder
    if ($null -ne $p.KfmFoldersProtectedNow) {
        $k = [int]$p.KfmFoldersProtectedNow
        "           KfmFoldersProtectedNow={0}  (Desktop={1} Documents={2} Pictures={3}; community-derived bit map)" -f $k, (($k -band 0x200) -ne 0), (($k -band 0x400) -ne 0), (($k -band 0x800) -ne 0)
    } else { "           KfmFoldersProtectedNow: not present (folder backup never enabled for this account, or value renamed)" }
}
"env OneDrive=$env:OneDrive  OneDriveConsumer=$env:OneDriveConsumer  OneDriveCommercial=$env:OneDriveCommercial"

Section "OneDrive.exe"
$procs = Get-Process OneDrive
if ($procs) { $procs | ForEach-Object { "running pid=$($_.Id) path=$($_.Path)" } } else { "not running" }
"Run entry    : $((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').OneDrive)"
"Per-machine  : $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\OneDrive').Version)  (HKLM\SOFTWARE\Microsoft\OneDrive\Version; empty = per-user install)"

Section "Known folders (SHGetKnownFolderPath via .NET) vs defaults"
$pairs = @(
    @{ Name='Desktop';   Current=[Environment]::GetFolderPath('Desktop');     Default="$env:USERPROFILE\Desktop" },
    @{ Name='Documents'; Current=[Environment]::GetFolderPath('MyDocuments'); Default="$env:USERPROFILE\Documents" },
    @{ Name='Pictures';  Current=[Environment]::GetFolderPath('MyPictures');  Default="$env:USERPROFILE\Pictures" }
)
$redirected = $false
foreach ($f in $pairs) {
    $state = if ($f.Current -ieq $f.Default) { 'default' } else { 'REDIRECTED'; $redirected = $true }
    "{0,-10} {1,-11} {2}" -f $f.Name, $state, $f.Current
}
"User Shell Folders (registry, diagnostic only):"
$usf = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders'
"  Desktop={0}`n  Personal={1}`n  My Pictures={2}" -f $usf.Desktop, $usf.Personal, $usf.'My Pictures'
if ($redirected) { Write-Host "Folder backup (Known Folder Move) IS active for at least one folder: follow README 'Migrating from OneDrive when Back up folders is on'." -ForegroundColor Yellow }
else { Write-Host "No known folder is redirected: folder backup is OFF; the migration steps for it do not apply." -ForegroundColor Green }

Section "Policies that hide or block folder backup / OneDrive"
$od = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\OneDrive'
"KFMBlockOptIn={0}  KFMBlockOptOut={1}  KFMSilentOptIn={2}  KFMOptInWithWizard={3}" -f $od.KFMBlockOptIn, $od.KFMBlockOptOut, $od.KFMSilentOptIn, $od.KFMOptInWithWizard
"DisableFileSyncNGSC (HKLM\...\Policies\Microsoft\Windows\OneDrive)={0}" -f (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive').DisableFileSyncNGSC
"DisablePersonalDirChange (HKCU\...\Policies\Explorer)={0}" -f (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer').DisablePersonalDirChange
"Note: Microsoft blocks folder backup to PERSONAL accounts on domain-joined PCs; on such machines the option is simply not shown."

Section "Cloud Files sync roots (HKLM\...\Explorer\SyncRootManager)"
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager' | ForEach-Object {
    $path = (Get-ItemProperty "$($_.PSPath)\UserSyncRoots").$sid
    "{0,-60} {1}" -f $_.PSChildName, $path
}
