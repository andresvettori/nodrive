<#
.SYNOPSIS
  Opens and fully reads one placeholder, times it, and verifies the bytes against the fake server's deterministic pattern
  (byte at offset i of item n is (byte)(i + n)). Writes one result line to -Out. Used by responsiveness.ps1.
#>
param([string] $Path, [string] $Out)
$sw = [Diagnostics.Stopwatch]::StartNew()
$n = if ((Split-Path $Path -Leaf) -match ' - (\d+)\.[^.]+$') { [int]$Matches[1] } else { 0 }
try {
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    $opened = $sw.ElapsedMilliseconds
    $buf = New-Object byte[] 1048576; $total = 0L; $bad = 0
    while (($r = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
        foreach ($probe in 0, ($r - 1)) {
            if ($buf[$probe] -ne [byte](($total + $probe + $n) -band 0xFF)) { $bad++ }
        }
        $total += $r
    }
    $fs.Close()
    "OK bytes=$total opened=${opened}ms total=$($sw.ElapsedMilliseconds)ms badSamples=$bad" | Set-Content $Out
} catch {
    "ERROR after $($sw.ElapsedMilliseconds) ms: $($_.Exception.InnerException.Message)$($_.Exception.Message) (0x{0:X8})" -f $_.Exception.HResult | Set-Content $Out
}
