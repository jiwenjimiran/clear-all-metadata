param([string]$CoveSourceRoot = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $root 'src/ClearAllMetadata/extension.json') -Raw | ConvertFrom-Json
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts ([Guid]::NewGuid().ToString('N'))
$arguments = @('publish', (Join-Path $root 'src/ClearAllMetadata/ClearAllMetadata.csproj'), '-c', 'Release', '-o', $stage, '-p:CoveCiVersion=true')
if ($CoveSourceRoot) { $arguments += "-p:CoveSourceRoot=$CoveSourceRoot" }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Extension publish failed.' }
$allowed = @('Cove.ClearAllMetadata.dll', 'Cove.ClearAllMetadata.deps.json', 'Cove.ClearAllMetadata.runtimeconfig.json', 'extension.json', 'assets')
foreach ($name in $allowed) {
    if (!(Test-Path -LiteralPath (Join-Path $stage $name))) { throw "Missing package file: $name" }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $artifacts "clear-all-metadata-$($manifest.version).zip"
$stream = [IO.MemoryStream]::new()
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
try {
    foreach ($name in $allowed) {
        $target = Join-Path $stage $name
        $files = if (Test-Path -LiteralPath $target -PathType Container) { Get-ChildItem -LiteralPath $target -File -Recurse } else { Get-Item -LiteralPath $target }
        foreach ($file in $files) {
            $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $archive.Dispose() }
try { [IO.File]::WriteAllBytes($zip, $stream.ToArray()) } finally { $stream.Dispose() }
$check = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($required in @('extension.json', 'Cove.ClearAllMetadata.dll', 'Cove.ClearAllMetadata.deps.json', 'Cove.ClearAllMetadata.runtimeconfig.json', 'assets/index.mjs', 'assets/options.mjs', 'assets/styles.css')) {
        if ($check.Entries.FullName -notcontains $required) { throw "ZIP is missing $required" }
    }
    if ($check.Entries.FullName -contains 'Cove.Data.dll' -or $check.Entries.FullName -contains 'Cove.Sdk.dll') { throw 'ZIP must not bundle host assemblies.' }
} finally { $check.Dispose() }
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $([IO.Path]::GetFileName($zip))`n")
Write-Output "SHA256: $hash"
Write-Output "Package: $zip"

