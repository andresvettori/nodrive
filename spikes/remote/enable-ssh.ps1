<#
.SYNOPSIS
  One-time, run as Administrator on the Windows PC: installs the built-in OpenSSH server, makes PowerShell the
  SSH shell, allows ONLY public-key authentication, installs the development Mac's public key, and opens the
  firewall for the private (home) network profile only.
.NOTES
  Reverse with: .\enable-ssh.ps1 -Disable
#>
param(
    [string] $PublicKey = '<SSH_PUBLIC_KEY>',
    [switch] $Disable
)
$ErrorActionPreference = 'Stop'

# The DISM cmdlets (Get/Add-WindowsCapability) fail in PowerShell 7 with "Class not registered":
# re-run this script in Windows PowerShell 5.1 when started from pwsh.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($Disable) { $argList += '-Disable' }
    if ($PSBoundParameters.ContainsKey('PublicKey')) { $argList += @('-PublicKey', $PublicKey) }
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @argList
    exit $LASTEXITCODE
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell."
}

if ($Disable) {
    Stop-Service sshd -ErrorAction SilentlyContinue
    Set-Service sshd -StartupType Disabled
    Get-NetFirewallRule -Name 'NoDrive-OpenSSH-Private' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "sshd stopped and disabled; firewall rule removed. Keys left in place."
    return
}

# 1. Install and start the server
$cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*'
if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name | Out-Null }
Start-Service sshd
Set-Service sshd -StartupType Automatic

# 2. PowerShell as the default shell (pwsh 7 if present, else Windows PowerShell)
$pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
# sshd cannot launch Microsoft Store (WindowsApps) executables, so only use pwsh from a normal install.
$shell = if ($pwsh -and $pwsh.Source -notlike '*\WindowsApps\*') { $pwsh.Source } else { "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" }
New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Value $shell -PropertyType String -Force | Out-Null

# 3. Key-only authentication
$cfg = "$env:ProgramData\ssh\sshd_config"
$lines = Get-Content $cfg
$lines = $lines | Where-Object { $_ -notmatch '^\s*#?\s*(PasswordAuthentication|PubkeyAuthentication|KbdInteractiveAuthentication)\s' }
$lines += 'PubkeyAuthentication yes', 'PasswordAuthentication no', 'KbdInteractiveAuthentication no'
Set-Content $cfg $lines -Encoding ascii

# 4. Install the Mac's public key. Administrators use the shared admin file (sshd_config default Match group administrators).
$isAdminUser = (whoami /groups) -match 'S-1-5-32-544'
if ($isAdminUser) {
    $auth = "$env:ProgramData\ssh\administrators_authorized_keys"
    $existing = if (Test-Path $auth) { Get-Content $auth } else { @() }
    if ($existing -notcontains $PublicKey) { Add-Content $auth $PublicKey -Encoding ascii }
    icacls $auth /inheritance:r /grant "Administrators:F" /grant "SYSTEM:F" | Out-Null
} else {
    $dir = "$env:USERPROFILE\.ssh"; New-Item -ItemType Directory -Force $dir | Out-Null
    $auth = "$dir\authorized_keys"
    $existing = if (Test-Path $auth) { Get-Content $auth } else { @() }
    if ($existing -notcontains $PublicKey) { Add-Content $auth $PublicKey -Encoding ascii }
}

# 5. Firewall: private profile only (home network), TCP 22
Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Disable-NetFirewallRule
if (-not (Get-NetFirewallRule -Name 'NoDrive-OpenSSH-Private' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name 'NoDrive-OpenSSH-Private' -DisplayName 'OpenSSH (NoDrive dev, private network only)' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -Profile Private | Out-Null
}
Restart-Service sshd

Write-Host "`nOpenSSH ready. Key-only auth, shell = $shell, admin keys file = $isAdminUser"
Write-Host "From the Mac: ssh $env:USERNAME@$((Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.*' } | Select-Object -First 1).IPAddress)"
Write-Host "Current network profile(s): $((Get-NetConnectionProfile).NetworkCategory -join ', ')  (must be Private for the rule to apply)"
