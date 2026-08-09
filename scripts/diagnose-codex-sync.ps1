[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidatePattern('^[0-9a-fA-F-]+$')]
    [string]$ThreadId = "019fc0e9-6055-7991-8e02-450e864fbb86"
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Name) {
    Write-Output ""
    Write-Output "== $Name =="
}

function Protect-CommandLine([AllowNull()][string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return '<unavailable>' }

    # Never print positional arguments: they can be an unlabeled prompt. Keep
    # only the executable/role shape needed for process comparison.
    if ($CommandLine -match '(?i)(^|\s)app-server(\s|$)') { return 'codex.exe app-server [arguments omitted]' }
    return 'Codex.exe [arguments omitted]'
}

function Get-ProcessSnapshot {
    try {
        return @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop |
            Where-Object { $_.Name -ieq 'Codex.exe' -or ($_.Name -ieq 'codex.exe' -and $_.CommandLine -match '(?i)(^|\s)app-server(\s|$)') } |
            ForEach-Object {
                $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner -ErrorAction SilentlyContinue
                $version = if ($_.ExecutablePath) { try { [Diagnostics.FileVersionInfo]::GetVersionInfo($_.ExecutablePath).FileVersion } catch { '<unavailable>' } } else { '<unavailable>' }
                [pscustomobject]@{
                    ProcessId = $_.ProcessId
                    Name = $_.Name
                    Owner = if ($owner.User) { "$($owner.Domain)\$($owner.User)" } else { '<unavailable>' }
                    FileVersion = $version
                    ExecutablePath = $_.ExecutablePath
                    CommandLine = $_.CommandLine
                }
            })
    } catch {
        Write-Warning "Cannot inspect other process details without sufficient access: $($_.Exception.Message)"
        return @()
    }
}

Write-Section 'Codex CLI'
Write-Output 'where.exe codex:'
try {
    & where.exe codex 2>&1 | ForEach-Object { Write-Output "  $_" }
} catch {
    Write-Output "  unavailable: $($_.Exception.Message)"
}
Write-Output 'codex --version:'
try {
    & codex --version 2>&1 | ForEach-Object { Write-Output "  $_" }
} catch {
    Write-Output "  unavailable: $($_.Exception.Message)"
}

Write-Section 'Current process environment'
Write-Output "User: $([Environment]::UserName)"
Write-Output "HOME: $($env:HOME)"
Write-Output "USERPROFILE: $($env:USERPROFILE)"
$hasCodexHome = -not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)
Write-Output "CODEX_HOME explicitly set: $hasCodexHome"
if ($hasCodexHome) {
    $codexRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($env:CODEX_HOME)
    Write-Output "Resolved Codex data root: $codexRoot (from CODEX_HOME)"
} elseif (-not [string]::IsNullOrWhiteSpace($env:HOME)) {
    $codexRoot = Join-Path $env:HOME '.codex'
    Write-Output "Resolved Codex data root: $codexRoot (default under HOME)"
} elseif (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
    $codexRoot = Join-Path $env:USERPROFILE '.codex'
    Write-Output "Resolved Codex data root: $codexRoot (default under USERPROFILE)"
} else {
    $codexRoot = $null
    Write-Output 'Resolved Codex data root: unavailable (USERPROFILE is not set and CODEX_HOME is not explicit)'
}

$sessionsRoot = if ($null -ne $codexRoot) { Join-Path $codexRoot 'sessions' } else { $null }
Write-Output "Resolved .codex\\sessions root: $sessionsRoot"

Write-Section 'Codex processes'
$processes = Get-ProcessSnapshot
if ($processes.Count -eq 0) {
    Write-Output 'No readable Codex.exe/codex.exe processes were found.'
} else {
    foreach ($process in $processes) {
        $isAppServer = $process.CommandLine -match '(?i)(^|\s)app-server(\s|$)'
        Write-Output "PID: $($process.ProcessId)"
        Write-Output "  Name: $($process.Name)"
        Write-Output "  Owner: $($process.Owner)"
        Write-Output "  FileVersion: $($process.FileVersion)"
        Write-Output "  Role: $(if ($isAppServer) { 'codex app-server' } else { 'Codex/Desktop or other Codex process' })"
        Write-Output "  Path: $(if ($process.ExecutablePath) { $process.ExecutablePath } else { '<unavailable>' })"
        Write-Output "  CommandLine: $(Protect-CommandLine $process.CommandLine)"
    }
}
Write-Output 'Other-process HOME, USERPROFILE, and CODEX_HOME cannot be read reliably without explicit permission/debug access; this script does not infer them.'

Write-Section 'Thread rollout files'
Write-Output "ThreadId: $ThreadId"
if ([string]::IsNullOrWhiteSpace($sessionsRoot) -or -not (Test-Path -LiteralPath $sessionsRoot -PathType Container)) {
    Write-Output 'Sessions root does not exist or cannot be read.'
    exit 0
}

# Match the standard rollout filename first. For nonstandard filenames, inspect
# only the first session_meta JSON line and compare its ID field. Never scan or
# print message lines, which could merely mention another Thread ID in a prompt.
$matches = New-Object System.Collections.Generic.List[System.IO.FileInfo]
try {
    $files = Get-ChildItem -LiteralPath $sessionsRoot -File -Recurse -ErrorAction SilentlyContinue
    foreach ($file in $files) {
        $nameMatches = $file.Name -like "*$ThreadId*"
        $metadataMatches = $false
        if (-not $nameMatches) {
            try {
                $firstLine = [System.IO.File]::ReadLines($file.FullName) | Select-Object -First 1
                $metadata = $firstLine | ConvertFrom-Json -ErrorAction Stop
                $metadataMatches = $metadata.type -eq 'session_meta' -and
                    ($metadata.payload.id -eq $ThreadId -or $metadata.payload.session_id -eq $ThreadId)
            } catch { $metadataMatches = $false }
        }
        if ($nameMatches -or $metadataMatches) { [void]$matches.Add($file) }
    }
} catch {
    Write-Output "Unable to inspect session files: $($_.Exception.Message)"
}

if ($matches.Count -eq 0) {
    Write-Output 'No rollout file whose session metadata matches the specified Thread ID was found.'
} else {
    foreach ($file in $matches | Sort-Object FullName) {
        Write-Output "Path: $($file.FullName)"
        Write-Output "  Length: $($file.Length) bytes"
        Write-Output "  LastWriteTime: $($file.LastWriteTime.ToString('o'))"
    }
    if ($matches.Count -gt 1) {
        Write-Output "WARNING: $($matches.Count) rollout files have the same Thread ID in filename/session metadata. Compare their paths and timestamps before drawing a sync conclusion."
    }
}

Write-Output ''
Write-Output 'This script is diagnostic and read-only. It never prints rollout contents, prompts, replies, headers, tokens, API keys, or environment-variable inventories.'
