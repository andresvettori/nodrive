Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class Raw {
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  public static extern SafeFileHandle CreateFileW(string f, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newPtr, uint method);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint n, out uint read, IntPtr ov);
}
"@
$r = "$env:USERPROFILE\NoDriveSpike"
$big = Get-ChildItem $r -Recurse -File | Where-Object { $_.Attributes -band 0x400000 } | Sort-Object Length -Descending | Select-Object -First 1
"target: {0} ({1:N1} MB)" -f $big.Name, ($big.Length/1MB)
$h = [Raw]::CreateFileW($big.FullName, [uint32]2147483648, 7, [IntPtr]::Zero, 3, 0x80, [IntPtr]::Zero)   # GENERIC_READ, share all, OPEN_EXISTING, NORMAL
$np = 0L; [Raw]::SetFilePointerEx($h, [long]($big.Length / 2), [ref]$np, 0) | Out-Null
$buf = New-Object byte[] 4096; $got = [uint32]0
$sw = [Diagnostics.Stopwatch]::StartNew()
$ok = [Raw]::ReadFile($h, $buf, 4096, [ref]$got, [IntPtr]::Zero)
"raw ReadFile at middle: ok=$ok bytes=$got after {0:N1} s" -f ($sw.ElapsedMilliseconds/1000)
$h.Close()
