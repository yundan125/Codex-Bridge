param()

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$targets = @(
    (Join-Path $projectRoot "artifacts"),
    (Join-Path $projectRoot "services\bridge-daemon\bin"),
    (Join-Path $projectRoot "apps\desktop\CloudLight.CodexBridge\bin"),
    (Join-Path $projectRoot "apps\desktop\CloudLight.CodexBridge\obj")
)

foreach ($target in $targets) {
    $fullTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith($projectRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the project: $fullTarget"
    }
    if (Test-Path -LiteralPath $fullTarget) {
        Remove-Item -LiteralPath $fullTarget -Recurse -Force
        Write-Output "Cleaned: $fullTarget"
    }
}
