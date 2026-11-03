[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $project = [xml](Get-Content -LiteralPath 'src/MasronDroid/MasronDroid.csproj' -Raw)
    $version = $project.Project.PropertyGroup.Version
    $staging = Join-Path $root "artifacts/release-input-v$version-$([Guid]::NewGuid().ToString('N'))"
    & (Join-Path $PSScriptRoot 'Build-Portable.ps1') -OutputDirectory $staging
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'Could not restore Velopack.' }
    dotnet vpk pack --runtime win-x64 --channel win --packId MasronDroid --packVersion $version --packAuthors f22r --packDir $staging --mainExe MasronDroid.exe --icon src/MasronDroid/Assets/masrondroid.ico --outputDir "artifacts/github/v$version"
    if ($LASTEXITCODE) { throw 'Could not build update packages.' }
} finally { Pop-Location }
