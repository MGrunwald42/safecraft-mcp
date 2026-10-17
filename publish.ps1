<#
.SYNOPSIS
    Publishes safecraft-mcp.exe with NativeAOT and installs it with MXC's native files, or packs a
    release.

.DESCRIPTION
    Builds src/SafeCraftMcp.Server for win-x64 into artifacts/publish, then copies the exe and the
    files MXC loads next to it (mxc_ffi.dll and its helpers) to -Destination, leaving out the .pdb
    files. Prints the installed exe's SHA-256. The native link step needs the Visual Studio C++
    build tools; vswhere.exe is added to PATH for it.

    With -Release it installs nothing: it packs the same files into
    artifacts/release/safecraft-mcp-<version>-win-x64.zip and writes SHA256SUMS.txt next to it
    (the zip and every file in it).

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Destination C:\Tools\safecraft-mcp
    .\publish.ps1 -Release
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\safecraft-mcp'),
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $installer) { $env:PATH += ";$installer" }

$project = Join-Path $PSScriptRoot 'src\SafeCraftMcp.Server\SafeCraftMcp.Server.csproj'
$out = Join-Path $PSScriptRoot 'artifacts\publish'
# A fresh folder, so files from an earlier publish don't end up in the install.
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish $project -c Release -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
$files = Get-ChildItem $out -File | Where-Object Extension -ne '.pdb'

if ($Release) {
    $version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $releaseDir = Join-Path $PSScriptRoot 'artifacts\release'
    New-Item -ItemType Directory -Force $releaseDir | Out-Null
    $zip = Join-Path $releaseDir "safecraft-mcp-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $files.FullName -DestinationPath $zip
    $sums = @($zip) + $files.FullName | ForEach-Object {
        "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)"
    }
    $sums | Set-Content (Join-Path $releaseDir 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Packed $zip"
    $sums | ForEach-Object { Write-Host $_ }
    return
}

New-Item -ItemType Directory -Force $Destination | Out-Null
$files | Copy-Item -Destination $Destination -Force
$exe = Join-Path $Destination 'safecraft-mcp.exe'
Write-Host "Installed $exe"
Write-Host "SHA-256 $((Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant())"
