[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root 'artifacts/downloads'
$runtime = Join-Path $root '.runtime/tools'
[void][IO.Directory]::CreateDirectory($cache)
Add-Type -AssemblyName System.IO.Compression.FileSystem

$archives = @(
    @{
        Name = 'platform-tools_r37.0.1-win.zip'
        Url = 'https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip'
        Sha256 = '45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99'
        Source = 'platform-tools'; Folder = 'platform-tools'
        Files = @('adb.exe','fastboot.exe','AdbWinApi.dll','AdbWinUsbApi.dll','libwinpthread-1.dll','NOTICE.txt','source.properties')
    },
    @{
        Name = 'scrcpy-win64-v5.0.1.zip'
        Url = 'https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip'
        Sha256 = 'b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5'
        Source = 'scrcpy-win64-v5.0.1'; Folder = 'scrcpy'
        Files = @('scrcpy.exe','scrcpy-server','avcodec-63.dll','avformat-63.dll','avutil-61.dll',
                  'swresample-7.dll','SDL3.dll','libusb-1.0.dll','scrcpy.png','disconnected.png','LICENSE.txt')
    }
)

$allowed = foreach ($spec in $archives) {
    foreach ($name in $spec.Files) { [IO.Path]::GetFullPath((Join-Path (Join-Path $runtime $spec.Folder) $name)) }
}
if (Test-Path -LiteralPath $runtime) {
    foreach ($existing in Get-ChildItem -LiteralPath $runtime -Recurse -File -Force) {
        if ($existing.FullName -notin $allowed) { throw "Unexpected file in the tools staging directory: $($existing.Name)" }
    }
}
foreach ($spec in $archives) {
    $zipPath = Join-Path $cache $spec.Name
    if (!(Test-Path -LiteralPath $zipPath) -or (Get-FileHash -LiteralPath $zipPath).Hash -ne $spec.Sha256) {
        $part = "$zipPath.$([Guid]::NewGuid().ToString('N')).part"
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $spec.Url -OutFile $part
            if ((Get-FileHash -LiteralPath $part).Hash -ne $spec.Sha256) { throw "SHA-256 mismatch for $($spec.Name)" }
            Move-Item -LiteralPath $part -Destination $zipPath -Force
        } finally { if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part } }
    }
    $folder = Join-Path $runtime $spec.Folder
    [void][IO.Directory]::CreateDirectory($folder)
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($file in $spec.Files) {
            $entry = $archive.GetEntry("$($spec.Source)/$file")
            if (!$entry) { throw "Missing archive entry: $file" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $folder $file), $true)
        }
    } finally { $archive.Dispose() }
    Write-Host "$($spec.Folder) ready ($($spec.Files.Count) verified archive entries)"
}
