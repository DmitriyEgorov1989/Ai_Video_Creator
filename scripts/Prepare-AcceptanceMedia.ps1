param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\data\acceptance-media')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Speech
$null = Get-Command ffmpeg, ffprobe -ErrorAction Stop
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Output directory already exists; choose a new directory to preserve the previous revision: $output"
}
$null = [IO.Directory]::CreateDirectory($output)
$utf8 = New-Object Text.UTF8Encoding($false)
$recipePath = Join-Path $PSScriptRoot 'acceptance-speech.json'
$recipe = [IO.File]::ReadAllText($recipePath, $utf8) | ConvertFrom-Json
$intervals = @()
$phrasePaths = @()
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $synth.SelectVoice($recipe.voice)
    $synth.Rate = $recipe.rate
    $voice = $synth.Voice
    for ($i = 0; $i -lt $recipe.phrases.Count; $i++) {
        $phrase = $recipe.phrases[$i]
        $path = Join-Path $output "phrase-$i.wav"
        $phrasePaths += $path
        $synth.SetOutputToWaveFile($path)
        $synth.Speak($phrase.text)
        $synth.SetOutputToNull()
        $durationText = & ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 $path
        if ($LASTEXITCODE -ne 0) { throw "FFprobe failed for $path" }
        $duration = [double]::Parse($durationText, [Globalization.CultureInfo]::InvariantCulture)
        # Preserve at least one second of silence before the next utterance/end.
        $limit = if ($i -eq 0) { 12 } else { 24 }
        if ($phrase.startSeconds + $duration -gt $limit) { throw 'Synthesized phrase exceeds its time slot.' }
        $intervals += [ordered]@{
            startSeconds = $phrase.startSeconds
            endSeconds = [Math]::Round($phrase.startSeconds + $duration, 6)
            text = $phrase.text
        }
    }
} finally {
    $synth.Dispose()
}

$speech = Join-Path $output 'speech.mp4'
$silent = Join-Path $output 'no-speech.mp4'
$unsupported = Join-Path $output 'unsupported.mp4'
$corrupt = Join-Path $output 'corrupt.mp4'
$visual = 'color=c=0x16324f:s=360x640:r=30:d=25,drawgrid=w=60:h=80:t=1:c=white@0.2,drawbox=x=130:y=260:w=100:h=100:c=orange:t=fill,rotate=0.15*sin(2*PI*t/5):fillcolor=0x16324f'
$mix = '[1:a]adelay=2000:all=1[a1];[2:a]adelay=13000:all=1[a2];[a1][a2]amix=inputs=2:normalize=0,apad,atrim=duration=25[a]'
& ffmpeg -hide_banner -loglevel error -nostdin -n -f lavfi -i $visual -i $phrasePaths[0] -i $phrasePaths[1] -filter_complex $mix -map 0:v:0 -map '[a]' -t 25 -c:v libx264 -preset fast -crf 23 -threads 2 -pix_fmt yuv420p -r 30 -fps_mode cfr -color_primaries bt709 -color_trc bt709 -colorspace bt709 -x264-params 'colorprim=bt709:transfer=bt709:colormatrix=bt709' -c:a aac -ar 48000 -ac 1 -b:a 128k -movflags +faststart $speech
if ($LASTEXITCODE -ne 0) { throw 'Speech fixture encoding failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -n -i $speech -f lavfi -i 'anullsrc=r=48000:cl=mono' -map 0:v:0 -map 1:a:0 -t 25 -c:v copy -c:a aac -b:a 128k -movflags +faststart $silent
if ($LASTEXITCODE -ne 0) { throw 'Silent fixture encoding failed.' }

foreach ($path in @($speech, $silent)) {
    & ffmpeg -hide_banner -loglevel error -xerror -nostdin -i $path -map 0:v:0 -map 0:a:0 -f null -
    if ($LASTEXITCODE -ne 0) { throw "Full decode failed: $path" }
}

# Change only the video codec; the container and audio remain valid.
& ffmpeg -hide_banner -loglevel error -nostdin -n -i $speech -map 0:v:0 -map 0:a:0 -c:v mpeg4 -q:v 5 -threads 2 -c:a copy -movflags +faststart $unsupported
if ($LASTEXITCODE -ne 0) { throw 'Unsupported fixture encoding failed.' }
# Truncate inside the first MP4 box: deterministic failure before metadata exists.
$bytes = [IO.File]::ReadAllBytes($speech)
[IO.File]::WriteAllBytes($corrupt, [byte[]]$bytes[0..15])

$pauses = @(
    @{ startSeconds = 0; endSeconds = 2 },
    @{ startSeconds = $intervals[0].endSeconds; endSeconds = 13 },
    @{ startSeconds = $intervals[1].endSeconds; endSeconds = 25 }
)
$records = @()
foreach ($name in @('speech.mp4', 'no-speech.mp4', 'unsupported.mp4', 'corrupt.mp4')) {
    $path = Join-Path $output $name
    $metadata = $null
    if ($name -ne 'corrupt.mp4') {
        $json = & ffprobe -v error -show_format -show_streams -of json $path
        if ($LASTEXITCODE -ne 0) { throw "FFprobe failed: $path" }
        $metadata = ($json -join "`n") | ConvertFrom-Json
        $metadata.format.PSObject.Properties.Remove('filename')
    }
    $expected = switch ($name) {
        'corrupt.mp4' { 'process.exit_code' }
        'unsupported.mp4' { 'media.video_codec_unsupported' }
        default { 'supported' }
    }
    $records += [ordered]@{
        file = $name
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = (Get-Item -LiteralPath $path).Length
        expectedInputResult = $expected
        metadata = $metadata
    }
}
$ffmpegVersion = (& ffmpeg -version)[0]
$ffprobeVersion = (& ffprobe -version)[0]
$manifest = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTime]::UtcNow.ToString('o')
    provenance = 'Project-authored neutral text; local Windows speech synthesis and FFmpeg-generated graphics. No user media.'
    recipeSha256 = (Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash.ToLowerInvariant()
    generatorSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    tools = @{ ffmpeg = $ffmpegVersion; ffprobe = $ffprobeVersion; voice = $voice.Name; voiceVersion = $voice.AdditionalInfo['Version']; speechAssembly = [System.Speech.Synthesis.SpeechSynthesizer].Assembly.FullName; windowsVersion = [Environment]::OSVersion.VersionString }
    durationSeconds = 25
    speechWindows = $intervals
    pauseWindows = $pauses
    intervalNote = 'Utterance WAV windows, not word-level alignment; allow 0.3 seconds at acoustic boundaries. No-speech fixture has silence over [0,25].'
    files = $records
    speechSources = @($phrasePaths | ForEach-Object {
        @{ file = [IO.Path]::GetFileName($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(); sizeBytes = (Get-Item -LiteralPath $_).Length }
    })
}
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 30), $utf8)
"Prepared acceptance media: $output"
