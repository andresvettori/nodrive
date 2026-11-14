<#
.SYNOPSIS
  Proves hydrated placeholders really contain the cloud bytes: for every file under the spike root that is NOT online-only,
  compares size and SHA-256 with what OneDrive reports (Microsoft Graph, by path) and shows the space actually allocated
  on disk versus an online-only file. Uses the spike's saved refresh token (rotates it and saves the new one). Read-only on files.
#>
param([string] $Root = "$env:USERPROFILE\NoDriveSpike",
      [string] $TokenPath = "$env:LOCALAPPDATA\NoDrive\spike\token.json",
      [string] $ClientId = '6711e7c6-ec09-42df-a5bb-bfb449119fdc')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class DiskSize {
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern uint GetCompressedFileSizeW(string f, out uint high);
  public static long Allocated(string f) { uint hi; uint lo = GetCompressedFileSizeW(f, out hi); return ((long)hi << 32) | lo; }
}
"@
$tok = Get-Content $TokenPath -Raw | ConvertFrom-Json
$r = Invoke-RestMethod -Method Post -Uri 'https://login.microsoftonline.com/consumers/oauth2/v2.0/token' -Body @{ client_id=$ClientId; grant_type='refresh_token'; refresh_token=$tok.refresh_token; scope='Files.ReadWrite offline_access' }
($r | ConvertTo-Json) | Set-Content $TokenPath
$h = @{ Authorization = "Bearer $($r.access_token)" }

$all = Get-ChildItem $Root -Recurse -File
$local = $all | Where-Object { -not ($_.Attributes -band 0x400000) }
$online = $all | Where-Object { $_.Attributes -band 0x400000 }
"files: {0} total, {1} local (hydrated), {2} online-only" -f $all.Count, $local.Count, $online.Count
$o = $online | Where-Object Length -gt 100000 | Select-Object -First 1
if ($o) { "online-only example : {0,12:N0} bytes logical, {1,12:N0} bytes allocated on disk  ({2})" -f $o.Length, [DiskSize]::Allocated($o.FullName), $o.Name }
foreach ($f in $local) {
    $rel = $f.FullName.Substring($Root.Length).TrimStart('\') -replace '\\','/'
    $enc = ($rel -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $mine = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    try {
        $item = Invoke-RestMethod -Headers $h -Uri "https://graph.microsoft.com/v1.0/me/drive/root:/$enc`?select=size,file"
        $theirs = $item.file.hashes.sha256Hash
        $ok = ($mine -eq $theirs) -and ($item.size -eq $f.Length)
        "{0}  {1,12:N0} B logical, {2,12:N0} B on disk  sha256 {3}  size {4}  [{5}]" -f $(if ($ok) { 'MATCH   ' } else { 'MISMATCH' }), $f.Length, [DiskSize]::Allocated($f.FullName), $(if ($mine -eq $theirs) { 'equal' } else { 'DIFFERENT' }), $(if ($item.size -eq $f.Length) { 'equal' } else { 'DIFFERENT' }), $rel
    } catch { "NOT FOUND in cloud by path: $rel ($($_.Exception.Message))" }
}
