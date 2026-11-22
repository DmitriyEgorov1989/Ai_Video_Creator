param(
    [int] $TimeoutSeconds = 3600,
    [string] $ReportPath = '.\data\real-telegram-acceptance-report.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-EnvironmentVariable([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required environment variable is missing: $Name"
    }
    return $value
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Test-DotEnvKey([string] $Name) {
    $path = Join-Path $root '.env'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    $normalizedName = $Name.Replace('__', ':')
    foreach ($line in Get-Content -LiteralPath $path) {
        $candidate = $line.Trim()
        if ($candidate.StartsWith('#') -or $candidate -notmatch '=') { continue }
        $key = $candidate.Substring(0, $candidate.IndexOf('=')).Trim().Replace('__', ':')
        if ($key -eq $normalizedName) {
            return -not [string]::IsNullOrWhiteSpace($candidate.Substring($candidate.IndexOf('=') + 1).Trim())
        }
    }
    return $false
}

function Require-ConfiguredValue([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    if (Test-DotEnvKey $Name) { return '<configured in .env>' }
    throw "Required configuration is missing: $Name"
}

function Get-ProcessTreeIds([int] $RootId) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
    $ids = [Collections.Generic.HashSet[int]]::new()
    $null = $ids.Add($RootId)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $all) {
            if ($ids.Contains([int]$process.ParentProcessId) -and $ids.Add([int]$process.ProcessId)) {
                $changed = $true
            }
        }
    }
    return @($ids)
}

function Get-TreeMetrics([int] $RootId) {
    [long] $workingSet = 0
    [double] $cpuSeconds = 0
    foreach ($id in (Get-ProcessTreeIds $RootId)) {
        try {
            $process = Get-Process -Id $id -ErrorAction Stop
            $workingSet += [long]$process.WorkingSet64
            $cpuSeconds += [double]$process.CPU
        }
        catch {
            # Процесс может завершиться между снимком дерева и Get-Process.
            # Потеря одного дочернего процесса не должна прерывать приёмку.
        }
    }
    return [pscustomobject]@{ WorkingSetBytes = $workingSet; CpuSeconds = $cpuSeconds }
}

function Get-DirectoryBytes([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return [long]0 }
    $sum = Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    return [long]($sum.Sum | ForEach-Object { if ($null -eq $_) { 0 } else { $_ } })
}

$null = Require-ConfiguredValue 'Telegram__BotToken'
$allowedUserId = Require-ConfiguredValue 'Telegram__AllowedUserId'
$null = Require-ConfiguredValue 'AiApi__Provider'
$null = Require-ConfiguredValue 'AiApi__ApiKey'
$null = Require-ConfiguredValue 'AiApi__Model'
$pythonPath = Require-EnvironmentVariable 'Whisper__PythonPath'
$modelPath = Require-EnvironmentVariable 'WHISPER_MODEL_PATH'

if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) { throw "Whisper Python executable was not found." }
if (-not (Test-Path -LiteralPath $modelPath -PathType Container)) { throw "Whisper model directory was not found." }
if ($TimeoutSeconds -le 0) { throw 'TimeoutSeconds must be positive.' }

$report = [IO.Path]::GetFullPath((Join-Path $root $ReportPath))
$reportParent = Split-Path -Parent $report
$null = New-Item -ItemType Directory -Force -Path $reportParent
$stdoutPath = Join-Path ([IO.Path]::GetTempPath()) "video-factory-real-telegram-$([Guid]::NewGuid().ToString('N')).out.log"
$stderrPath = Join-Path ([IO.Path]::GetTempPath()) "video-factory-real-telegram-$([Guid]::NewGuid().ToString('N')).err.log"
$dataRoot = Join-Path $root 'data'
$startedAt = [DateTimeOffset]::UtcNow
$start = [Diagnostics.Stopwatch]::StartNew()
$peakWorkingSet = [long]0
$peakCpuSeconds = [double]0
$peakDiskBytes = [long](Get-DirectoryBytes $dataRoot)
$exitCode = $null
$process = $null

try {
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', 'src/VideoFactory.Bot', '--no-launch-profile') -WorkingDirectory $root -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
    Write-Host "Host started. Send one video and a non-empty comment from Telegram user $allowedUserId."
    Write-Host "The runner stops after $TimeoutSeconds seconds or when you press Ctrl+C."

    while (-not $process.HasExited -and $start.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        Start-Sleep -Seconds 1
        $metrics = Get-TreeMetrics $process.Id
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $metrics.WorkingSetBytes)
        $peakCpuSeconds = [Math]::Max($peakCpuSeconds, $metrics.CpuSeconds)
        $peakDiskBytes = [Math]::Max($peakDiskBytes, [long](Get-DirectoryBytes $dataRoot))
    }

    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $exitCode = 'timeout'
    }
    else {
        $process.Refresh()
        $exitCode = $process.ExitCode
    }
}
finally {
    $start.Stop()
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    $finishedAt = [DateTimeOffset]::UtcNow
    $result = [ordered]@{
        schemaVersion = 1
        startedAtUtc = $startedAt
        finishedAtUtc = $finishedAt
        elapsedSeconds = [Math]::Round($start.Elapsed.TotalSeconds, 3)
        exitCode = $exitCode
        telegramUserId = $allowedUserId
        peakWorkingSetBytes = $peakWorkingSet
        peakCpuSeconds = [Math]::Round($peakCpuSeconds, 3)
        peakDataDirectoryBytes = $peakDiskBytes
        environment = [ordered]@{
            dotnet = (& dotnet --version)
            node = (& node --version)
            ffmpeg = ((& ffmpeg -version)[0])
            ffprobe = ((& ffprobe -version)[0])
            whisperPython = [IO.Path]::GetFileName($pythonPath)
            whisperModelDirectory = [IO.Path]::GetFileName($modelPath.TrimEnd('\', '/'))
        }
        note = 'This report contains no tokens, API keys, comments, media contents or Telegram file identifiers.'
    }
    [IO.File]::WriteAllText($report, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    Write-Host "Acceptance metrics written to $report"
}
