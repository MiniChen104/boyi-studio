param([string]$Version = '1.4.6')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo 'dist\windows-x64'
$archivePath = Join-Path $repo "dist\BoyiStudio-$Version-win-x64-no-references.zip"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be numeric major.minor.patch' }
Push-Location (Join-Path $repo 'glass-frontend')
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
} finally { Pop-Location }
& dotnet publish (Join-Path $repo 'glass-oj\GlassOJ.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' "-p:Version=$Version" -o $output
if ($LASTEXITCODE -ne 0) { throw 'Native publish failed' }
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $output -Force
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath $output
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.Extension -notin @('.pdb', '.lnk') } | ForEach-Object {
        $relative = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, "BoyiStudio-$Version/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose(); $stream.Dispose() }
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath (Join-Path $repo 'dist\SHA256SUMS.txt') -Encoding ascii
Write-Output "Package: $archivePath"

