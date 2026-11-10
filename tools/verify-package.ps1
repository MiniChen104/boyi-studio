param([Parameter(Mandatory=$true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PackagePath).Path
foreach ($required in @('博宜练习室.exe','ui\index.html','ui\app.js','ui\liquid-ui.js','ui\drawers.css','seed\catalog.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $root $required))) { throw "Missing package file: $required" }
}
$forbidden = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    $relative -match '(^|/)(references|reference-variants|accounts|drafts|staged|submissions|webview)/' -or
    $_.Name -in @('reference-solutions.json','reference-status.json','state.json','activity.log') -or
    $_.Extension -eq '.cpp'
})
if ($forbidden.Count) { throw "Forbidden reference or user-data files: $($forbidden.Count)" }
Write-Output 'Verified: no bundled reference answers or user data.'

