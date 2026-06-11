# Downloads and installs homuler MediaPipeUnityPlugin v0.11.0 if not present.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pkgDir = Join-Path $root "Packages\com.github.homuler.mediapipe"
$tgz = Join-Path $root "Packages/com.github.homuler.mediapipe-0.11.0.tgz"
$uri = "https://github.com/homuler/MediaPipeUnityPlugin/releases/download/v0.11.0/com.github.homuler.mediapipe-0.11.0.tgz"

if (-not (Test-Path $pkgDir)) {
    if (-not (Test-Path $tgz)) {
        Write-Host "Downloading MediaPipe package (~500MB)..."
        Invoke-WebRequest -Uri $uri -OutFile $tgz -UseBasicParsing
    }
    tar -xzf $tgz -C (Join-Path $root "Packages")
    if (Test-Path (Join-Path $root "Packages/package")) {
        Move-Item (Join-Path $root "Packages/package") $pkgDir
    }
}

Write-Host "MediaPipe package ready at $pkgDir"
