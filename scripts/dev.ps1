param()

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$daemonRoot = Join-Path $projectRoot "services\bridge-daemon"
$desktopProject = Join-Path $projectRoot "apps\desktop\CloudLight.CodexBridge\CloudLight.CodexBridge.csproj"
$daemonOutput = Join-Path $daemonRoot "bin\bridge-daemon.exe"
$desktopOutput = Join-Path $projectRoot "apps\desktop\CloudLight.CodexBridge\bin\Debug\net8.0-windows"

$goCommand = Get-Command go -ErrorAction SilentlyContinue
if ($null -ne $goCommand) {
    $goExecutable = $goCommand.Source
} else {
    $goExecutable = Join-Path $projectRoot ".tools\go\bin\go.exe"
}
if (-not (Test-Path -LiteralPath $goExecutable)) {
    throw "Go was not found. Install Go 1.26+ or place a portable toolchain in .tools\go."
}

New-Item -ItemType Directory -Path (Split-Path $daemonOutput) -Force | Out-Null
$env:GOOS = "windows"
$env:GOARCH = "amd64"
$env:CGO_ENABLED = "0"
& $goExecutable -C $daemonRoot build -trimpath -o $daemonOutput .\cmd\bridge-daemon
if ($LASTEXITCODE -ne 0) { throw "Go daemon build failed." }

dotnet build $desktopProject -c Debug --nologo
if ($LASTEXITCODE -ne 0) { throw "WPF Debug build failed." }
New-Item -ItemType Directory -Path $desktopOutput -Force | Out-Null
Copy-Item -LiteralPath $daemonOutput -Destination (Join-Path $desktopOutput "bridge-daemon.exe") -Force

dotnet run --project $desktopProject -c Debug --no-build
