$shell = New-Object -ComObject Shell.Application
function Show($label, $path) {
    "=== $label : $path"
    $item = Get-Item -LiteralPath $path
    "   attributes: 0x{0:X} ({1})" -f [int]$item.Attributes, $item.Attributes
    $ns = $shell.NameSpace((Split-Path $path))
    $fi = $ns.ParseName((Split-Path $path -Leaf))
    foreach ($p in "System.FilePlaceholderStatus","System.StorageProviderState","System.StorageProviderFileFlags","System.StorageProviderStatus","System.StorageProviderFileIdentifier","System.StorageProviderFileSyncStatus","System.StorageProviderFileVersion","System.Availability","System.IsOnlineOnly","System.OfflineAvailability","System.OfflineStatus") {
        $v = $fi.ExtendedProperty($p)
        "   {0,-45} = {1}" -f $p, $(if ($null -eq $v) { "<null>" } else { $v })
    }
}
$spike = Get-ChildItem "$env:USERPROFILE\NoDriveSpike" -Recurse -File | Where-Object { $_.Attributes -band 0x400000 } | Select-Object -First 1
Show "NoDrive placeholder (online-only)" $spike.FullName
$od = Get-ChildItem "$env:USERPROFILE\OneDrive" -Recurse -File -EA SilentlyContinue | Where-Object { $_.Attributes -band 0x400000 } | Select-Object -First 1
if ($od) { Show "OneDrive online-only file" $od.FullName }
$od2 = Get-ChildItem "$env:USERPROFILE\OneDrive" -Recurse -File -EA SilentlyContinue | Where-Object { -not ($_.Attributes -band 0x400000) } | Select-Object -First 1
if ($od2) { Show "OneDrive local file" $od2.FullName }
"--- reparse data of our placeholder ---"
fsutil reparsepoint query $spike.FullName | Select-Object -First 8
