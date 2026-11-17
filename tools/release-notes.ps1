<#
  Extracts the notes of one version from CHANGELOG.md (the section "## [<version>]"); falls back to "[Unreleased]".
  Usage: pwsh tools/release-notes.ps1 -Version 0.9.0-beta.142 > notes.md
#>
param([Parameter(Mandatory)][string] $Version, [string] $Path = "$PSScriptRoot/../CHANGELOG.md")
$text = Get-Content $Path -Raw
$short = if ($Version -match '^(.+-[A-Za-z]+)\.\d+$') { $Matches[1] } else { $Version }
foreach ($name in @($Version, $short, 'Unreleased')) {
  $m = [regex]::Match($text, "(?ms)^## \[$([regex]::Escape($name))\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)")
  if ($m.Success -and $m.Groups[1].Value.Trim().Length -gt 0) { "# NoDrive $Version`n`n" + $m.Groups[1].Value.Trim(); return }
}
"# NoDrive $Version`n`nSee CHANGELOG.md."
