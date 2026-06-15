param(
    [string]$Source = (Join-Path $PSScriptRoot "..\..\pose_estimation_app\models\centerpose\chair.onnx"),
    [string]$Dest = (Join-Path $PSScriptRoot "..\Assets\ObjectronDetection\CenterPose\Models\chair.onnx")
)

$Source = [System.IO.Path]::GetFullPath($Source)
$Dest = [System.IO.Path]::GetFullPath($Dest)

if (-not (Test-Path $Source)) {
    Write-Error "Source model not found: $Source"
    exit 1
}

$destDir = Split-Path $Dest -Parent
if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

Copy-Item -Path $Source -Destination $Dest -Force
Write-Host "Copied chair.onnx to $Dest"
Write-Host "In Unity: QuestObjectron > CenterPose > Convert Chair ONNX To Sentis"
