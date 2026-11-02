[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Prepare-Tools.ps1')
$output = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $root 'artifacts/MasronDroid-win-x64' }
$project = Join-Path $root 'src/MasronDroid/MasronDroid.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $output -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Portable build failed.' }
foreach ($file in @('MasronDroid.exe','tools/platform-tools/adb.exe','tools/platform-tools/fastboot.exe','tools/scrcpy/scrcpy.exe','tools/scrcpy/scrcpy-server')) {
    if (!(Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) { throw "Missing portable file: $file" }
}
Write-Host "Portable folder: $output"
