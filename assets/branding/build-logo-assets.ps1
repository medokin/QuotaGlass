param(
    [string]$Source = (Join-Path $PSScriptRoot "quotaglass-logo.svg"),
    [string]$OutputDirectory = $PSScriptRoot
)

$edgeCandidates = @(
    "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\Google\Chrome\Application\chrome.exe",
    "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
)

$browserPath = $edgeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $browserPath) {
    throw "Microsoft Edge or Google Chrome is required to rasterize the SVG."
}

if (-not (Test-Path -LiteralPath $Source)) {
    throw "SVG source not found: $Source"
}

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python with Pillow is required to resize PNG files and create the ICO."
}

python -c "import PIL" 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "Python package Pillow is required to resize PNG files and create the ICO."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = (Resolve-Path -LiteralPath $OutputDirectory).Path
$sourceUri = [Uri]::new($sourcePath).AbsoluteUri
$masterPng = Join-Path $outputPath "quotaglass-logo-1024.png"
$temporaryProfile = Join-Path ([System.IO.Path]::GetTempPath()) ("quotaglass-logo-" + [Guid]::NewGuid().ToString("N"))
$temporaryMasterPng = Join-Path $temporaryProfile "quotaglass-logo-1024.png"

New-Item -ItemType Directory -Path $temporaryProfile | Out-Null

try {
    & $browserPath `
        --headless=new `
        --disable-gpu `
        --hide-scrollbars `
        --force-device-scale-factor=1 `
        --default-background-color=00000000 `
        --user-data-dir=$temporaryProfile `
        --window-size=1024,1024 `
        --screenshot=$temporaryMasterPng `
        $sourceUri

    $screenshotDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $temporaryMasterPng) -and [DateTime]::UtcNow -lt $screenshotDeadline) {
        Start-Sleep -Milliseconds 50
    }

    if (-not (Test-Path -LiteralPath $temporaryMasterPng)) {
        throw "The browser did not produce the 1024 px master PNG."
    }

    Copy-Item -LiteralPath $temporaryMasterPng -Destination $masterPng -Force

    $resizeScript = @'
from pathlib import Path
import sys
from PIL import Image

output_directory = Path(sys.argv[1])
master_path = output_directory / "quotaglass-logo-1024.png"
sizes = (16, 24, 32, 48, 64, 128, 256, 512)

with Image.open(master_path) as master:
    source = master.convert("RGBA")
    for size in sizes:
        output = source.resize((size, size), Image.Resampling.LANCZOS)
        output.save(output_directory / f"quotaglass-logo-{size}.png", optimize=True)

    source.save(
        output_directory / "quotaglass.ico",
        format="ICO",
        sizes=[(size, size) for size in (16, 24, 32, 48, 64, 128, 256)],
    )
'@

    $resizeScript | python - $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "PNG resizing or ICO creation failed."
    }
}
finally {
    $resolvedTemporaryProfile = [System.IO.Path]::GetFullPath($temporaryProfile)
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $temporaryName = [System.IO.Path]::GetFileName($resolvedTemporaryProfile)

    if ($resolvedTemporaryProfile.StartsWith($resolvedTemporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        $temporaryName.StartsWith("quotaglass-logo-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryProfile -Recurse -Force -ErrorAction SilentlyContinue
    }
}
