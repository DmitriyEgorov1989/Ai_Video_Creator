param(
    [int] $TimeoutSeconds = 3600,
    [string] $ReportPath = '.\data\real-telegram-acceptance-report.json',
    [ValidateSet('single-source', 'multi-source')]
    [string] $SourceMode = 'single-source',
    [switch] $DryRun
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

function Get-TreeMetrics([int] $RootId, [hashtable] $CpuByProcessId) {
    [long] $workingSet = 0
    [double] $cpuSeconds = 0
    foreach ($id in (Get-ProcessTreeIds $RootId)) {
        try {
            $process = Get-Process -Id $id -ErrorAction Stop
            $workingSet += [long]$process.WorkingSet64
            $currentCpu = [double]$process.CPU
            $CpuByProcessId[$id] = [Math]::Max([double]($CpuByProcessId[$id]), $currentCpu)
        }
        catch {
            # Процесс может завершиться между снимком дерева и Get-Process.
            # Потеря одного дочернего процесса не должна прерывать приёмку.
        }
    }
    foreach ($value in $CpuByProcessId.Values) { $cpuSeconds += [double]$value }
    return [pscustomobject]@{ WorkingSetBytes = $workingSet; CpuSeconds = $cpuSeconds }
}

function Get-DirectoryBytes([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return [long]0 }
    $sum = Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    return [long]($sum.Sum | ForEach-Object { if ($null -eq $_) { 0 } else { $_ } })
}

if ($TimeoutSeconds -le 0) { throw 'TimeoutSeconds must be positive.' }

$report = if ([IO.Path]::IsPathRooted($ReportPath)) { [IO.Path]::GetFullPath($ReportPath) } else { [IO.Path]::GetFullPath((Join-Path $root $ReportPath)) }
$reportParent = Split-Path -Parent $report
$null = New-Item -ItemType Directory -Force -Path $reportParent
$evidencePath = [IO.Path]::ChangeExtension($report, '.evidence.json')

if ($DryRun) {
    $dryRunResult = [ordered]@{
        schemaVersion = 2
        dryRun = $true
        sourceMode = $SourceMode
        timeoutSeconds = $TimeoutSeconds
        reportPathConfigured = $true
        note = 'No host, network, paid API or Telegram operation was started.'
    }
    [IO.File]::WriteAllText($report, ($dryRunResult | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Write-Host "Dry-run report written to $report"
    exit 0
}

function Stop-ProcessTree([int] $RootId) {
    $ids = @(Get-ProcessTreeIds $RootId)
    [array]::Reverse($ids)
    foreach ($id in $ids) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
}

$null = Require-ConfiguredValue 'Telegram__BotToken'
$null = Require-ConfiguredValue 'Telegram__AllowedUserId'
$null = Require-ConfiguredValue 'AiApi__Provider'
$null = Require-ConfiguredValue 'AiApi__ApiKey'
$null = Require-ConfiguredValue 'AiApi__Model'
$pythonPath = Require-EnvironmentVariable 'Whisper__PythonPath'
$modelPath = Require-EnvironmentVariable 'WHISPER_MODEL_PATH'

if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) { throw "Whisper Python executable was not found." }
if (-not (Test-Path -LiteralPath $modelPath -PathType Container)) { throw "Whisper model directory was not found." }

$stdoutPath = [IO.Path]::ChangeExtension($report, '.host.out.log')
$stderrPath = [IO.Path]::ChangeExtension($report, '.host.err.log')
Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
$dataRoot = Join-Path $root 'data'
$startedAt = [DateTimeOffset]::UtcNow
$start = [Diagnostics.Stopwatch]::StartNew()
$peakWorkingSet = [long]0
$cpuTimeSeconds = [double]0
$cpuByProcessId = @{}
$initialDiskBytes = [long](Get-DirectoryBytes $dataRoot)
$exitCode = 1
$resultReason = 'host-failed'
$jobId = $null
$process = $null

try {
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', 'src/VideoFactory.Bot', '--no-launch-profile') -WorkingDirectory $root -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
    $sourceInstruction = if ($SourceMode -eq 'single-source') { 'one video' } else { 'at least two videos' }
    Write-Host "Host started. Send $sourceInstruction and a non-empty instruction from the configured Telegram account, then use /done."
    Write-Host "The runner stops after $TimeoutSeconds seconds or when you press Ctrl+C."

    while (-not $process.HasExited -and $start.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        Start-Sleep -Seconds 1
        $metrics = Get-TreeMetrics $process.Id $cpuByProcessId
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $metrics.WorkingSetBytes)
        $cpuTimeSeconds = [Math]::Max($cpuTimeSeconds, $metrics.CpuSeconds)

        if ($null -eq $jobId) {
            $jobsRoot = Join-Path $dataRoot 'jobs'
            if (Test-Path -LiteralPath $jobsRoot -PathType Container) {
                $candidates = @(Get-ChildItem -LiteralPath $jobsRoot -Directory | Where-Object {
                    $parsed = [Guid]::Empty
                    [Guid]::TryParse($_.Name, [ref]$parsed) -and $_.CreationTimeUtc -ge $startedAt.UtcDateTime
                })
                if ($candidates.Count -gt 1) {
                    $resultReason = 'multiple-jobs-created-since-start'
                    break
                }
                if ($candidates.Count -eq 1) {
                    $jobId = $candidates[0].Name
                    Write-Host "Tracking Job $jobId."
                }
            }
        }

        if ($null -ne $jobId) {
            & dotnet run --project (Join-Path $root 'src/VideoFactory.Bot') --no-launch-profile --no-build -- `
                acceptance-evidence "--job=$jobId" "--output=$evidencePath" 1>$null 2>$null
            $evidenceExitCode = $LASTEXITCODE
            if ($evidenceExitCode -eq 0) {
                $evidence = Get-Content -Raw -LiteralPath $evidencePath | ConvertFrom-Json
                if ($evidence.sourceMode -ne $SourceMode) {
                    $exitCode = 1
                    $resultReason = 'source-mode-mismatch'
                }
                else {
                    $exitCode = 0
                    $resultReason = 'passed'
                }
                break
            }
            if ($evidenceExitCode -eq 1) {
                $exitCode = 1
                $resultReason = 'evidence-failed'
                break
            }
            if ($evidenceExitCode -ne 3) {
                $exitCode = 2
                $resultReason = 'evidence-command-error'
                break
            }
        }
    }

    if ($start.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        $exitCode = 1
        $resultReason = 'timeout'
    }
    elseif ($process.HasExited -and $resultReason -eq 'host-failed') {
        $process.WaitForExit()
        $process.Refresh()
        $exitCode = if ($process.ExitCode -eq 0) { 1 } else { $process.ExitCode }
    }
}
finally {
    $start.Stop()
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-ProcessTree $process.Id
    }
    $finishedAt = [DateTimeOffset]::UtcNow
    $result = [ordered]@{
        schemaVersion = 2
        startedAtUtc = $startedAt
        finishedAtUtc = $finishedAt
        elapsedSeconds = [Math]::Round($start.Elapsed.TotalSeconds, 3)
        exitCode = $exitCode
        result = $resultReason
        jobId = $jobId
        sourceMode = $SourceMode
        cpuTimeSeconds = [Math]::Round($cpuTimeSeconds, 3)
        peakRamBytes = $peakWorkingSet
        diskDeltaBytes = [long](Get-DirectoryBytes $dataRoot) - $initialDiskBytes
        evidenceReport = if (Test-Path -LiteralPath $evidencePath -PathType Leaf) { [IO.Path]::GetFileName($evidencePath) } else { $null }
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
    Write-Host "Acceptance metrics written to $report"
    Write-Host "Host logs written to $stdoutPath and $stderrPath"
}

exit $exitCode
