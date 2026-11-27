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
$landscape = Join-Path $output 'landscape.mp4'
$landscapeShort = Join-Path $output 'landscape-short.mp4'
$fps24 = Join-Path $output 'portrait-24fps.mp4'
$fps60 = Join-Path $output 'portrait-60fps.mp4'
$rotated = Join-Path $output 'portrait-rotated.mp4'
$similarA = Join-Path $output 'similar-take-a.mp4'
$similarB = Join-Path $output 'similar-take-b.mp4'
$tones = Join-Path $output 'tones.mp4'
$montage = Join-Path $output 'montage-source.mp4'
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

# Additional deterministic project-authored variants for media-quality assertions.
& ffmpeg -hide_banner -loglevel error -nostdin -n -i $speech -vf 'scale=640:360:flags=neighbor' -c:v libx264 -preset fast -crf 23 -threads 2 -pix_fmt yuv420p -r 30 -fps_mode cfr -c:a copy -movflags +faststart $landscape
if ($LASTEXITCODE -ne 0) { throw 'Landscape fixture encoding failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -n -i $landscape -t 18 -map 0 -c copy -movflags +faststart $landscapeShort
if ($LASTEXITCODE -ne 0) { throw 'Short landscape fixture creation failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -n -i $speech -vf 'fps=24' -c:v libx264 -preset fast -crf 23 -threads 2 -pix_fmt yuv420p -r 24 -fps_mode cfr -c:a copy -movflags +faststart $fps24
if ($LASTEXITCODE -ne 0) { throw '24 FPS fixture encoding failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -n -i $speech -vf 'fps=60' -c:v libx264 -preset fast -crf 23 -threads 2 -pix_fmt yuv420p -r 60 -fps_mode cfr -c:a copy -movflags +faststart $fps60
if ($LASTEXITCODE -ne 0) { throw '60 FPS fixture encoding failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -display_rotation:v:0 90 -i $speech -map 0 -c copy -movflags +faststart $rotated
if ($LASTEXITCODE -ne 0) { throw 'Rotation metadata fixture creation failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -n -i $speech -vf 'drawbox=x=130:y=260:w=100:h=100:c=yellow:t=fill' -c:v libx264 -preset fast -crf 23 -threads 2 -pix_fmt yuv420p -r 30 -fps_mode cfr -c:a copy -movflags +faststart $similarA
if ($LASTEXITCODE -ne 0) { throw 'Similar take A encoding failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -n -i $speech -vf 'drawbox=x=135:y=260:w=100:h=100:c=yellow:t=fill' -c:v libx264 -preset fast -crf 23 -threads 2 -pix_fmt yuv420p -r 30 -fps_mode cfr -c:a copy -movflags +faststart $similarB
if ($LASTEXITCODE -ne 0) { throw 'Similar take B encoding failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -n -f lavfi -i 'color=c=0x203040:s=360x640:r=30:d=10' -f lavfi -i 'sine=frequency=440:duration=5:sample_rate=48000' -f lavfi -i 'sine=frequency=880:duration=5:sample_rate=48000' -filter_complex '[1:a][2:a]concat=n=2:v=0:a=1[a]' -map 0:v:0 -map '[a]' -c:v libx264 -preset fast -crf 23 -threads 2 -pix_fmt yuv420p -r 30 -fps_mode cfr -c:a aac -ar 48000 -ac 1 -b:a 128k -movflags +faststart $tones
if ($LASTEXITCODE -ne 0) { throw 'Tone fixture encoding failed.' }
& ffmpeg -hide_banner -loglevel error -nostdin -n -f lavfi -i 'color=c=red:s=320x568:r=24:d=1,drawbox=x=140:y=254:w=40:h=60:c=lime:t=fill' -f lavfi -i 'color=c=yellow:s=320x568:r=24:d=1' -f lavfi -i 'color=c=blue:s=320x568:r=24:d=1,drawbox=x=140:y=254:w=40:h=60:c=lime:t=fill' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=1' -f lavfi -i 'sine=frequency=660:sample_rate=48000:duration=1' -f lavfi -i 'sine=frequency=880:sample_rate=48000:duration=1' -filter_complex '[0:v][1:v][2:v]concat=n=3:v=1:a=0[v];[3:a][4:a][5:a]concat=n=3:v=0:a=1[a]' -map '[v]' -map '[a]' -c:v libx264 -pix_fmt yuv420p -r 24 -c:a aac -movflags +faststart $montage
if ($LASTEXITCODE -ne 0) { throw 'Montage source fixture encoding failed.' }

foreach ($path in @($landscape, $landscapeShort, $fps24, $fps60, $rotated, $similarA, $similarB, $tones, $montage)) {
    & ffmpeg -hide_banner -loglevel error -xerror -nostdin -i $path -map 0:v:0 -map 0:a:0 -f null -
    if ($LASTEXITCODE -ne 0) { throw "Full decode failed: $path" }
}

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

$oracleFiles = @($speech, $silent, $landscape, $landscapeShort, $fps24, $fps60, $rotated, $similarA, $similarB, $tones, $montage) | ForEach-Object {
    $name = [IO.Path]::GetFileName($_)
    $probeJson = & ffprobe -v error -show_streams -show_format -of json $_
    if ($LASTEXITCODE -ne 0) { throw "FFprobe failed for oracle fixture: $_" }
    $probe = ($probeJson -join "`n") | ConvertFrom-Json
    $video = @($probe.streams | Where-Object { $_.codec_type -eq 'video' })[0]
    $audio = @($probe.streams | Where-Object { $_.codec_type -eq 'audio' })[0]
    $fps = if ($video.r_frame_rate -match '^([0-9]+)/([0-9]+)$') {
        [Math]::Round(([double]$Matches[1] / [double]$Matches[2]), 3)
    } else { throw "Unable to read FPS for oracle fixture: $_" }
    [ordered]@{
        file = $name
        sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = (Get-Item -LiteralPath $_).Length
        orientation = if ([int]$video.width -ge [int]$video.height) { 'landscape' } else { 'portrait' }
        fps = [int][Math]::Round($fps)
        width = [int]$video.width
        height = [int]$video.height
        container = [string]$probe.format.format_name
        durationMicroseconds = [long][Math]::Round([double]::Parse([string]$probe.format.duration, [Globalization.CultureInfo]::InvariantCulture) * 1000000)
        videoCodec = [string]$video.codec_name
        audioCodec = [string]$audio.codec_name
        pixelFormat = [string]$video.pix_fmt
        sampleRate = [int]$audio.sample_rate
        channels = [int]$audio.channels
    }
}
$oracle = [ordered]@{
    schemaVersion = 1
    provenance = 'Project-authored synthetic media; no user media or complete user transcripts.'
    tolerances = [ordered]@{ timeMicroseconds = 300000; frames = 1; decibels = 2.0; frequencyHz = 5.0; colorChannel = 25.0 }
    fixtures = $oracleFiles
    markers = @(
        [ordered]@{ id = 'similar-take-a'; fixture = 'similar-take-a.mp4'; sourceTimeMicroseconds = 5000000; expectedOutputFrame = 0; kind = 'visual'; region = @{ x = 130; y = 260; width = 100; height = 100 }; expectedRgb = @{ red = 253; green = 253; blue = 0 } },
        [ordered]@{ id = 'similar-take-b'; fixture = 'similar-take-b.mp4'; sourceTimeMicroseconds = 5000000; expectedOutputFrame = 0; kind = 'visual'; region = @{ x = 135; y = 260; width = 100; height = 100 }; expectedRgb = @{ red = 253; green = 253; blue = 0 } }
    )
    audioWindows = @(
        [ordered]@{ id = 'tone-window-one'; fixture = 'tones.mp4'; startMicroseconds = 0; endMicroseconds = 5000000; expectedFrequencyHz = 440; },
        [ordered]@{ id = 'tone-window-two'; fixture = 'tones.mp4'; startMicroseconds = 5000000; endMicroseconds = 10000000; expectedFrequencyHz = 880; }
    )
    pauseWindows = @(
        [ordered]@{ id = 'initial-speech-pause'; fixture = 'speech.mp4'; startMicroseconds = 0; endMicroseconds = 2000000 }
    )
    speechWindows = @($intervals | ForEach-Object {
        [ordered]@{ id = "speech-$($_.startSeconds)"; fixture = 'speech.mp4'; startMicroseconds = [long]($_.startSeconds * 1000000); endMicroseconds = [long]($_.endSeconds * 1000000) }
    })
    safeRegions = @(
        [ordered]@{ id = 'orange-subject'; fixture = 'speech.mp4'; x = 130; y = 260; width = 100; height = 100 }
    )
    repeatGroups = @(
        [ordered]@{ id = 'similar-takes'; fixtureIds = @('similar-take-a.mp4', 'similar-take-b.mp4'); startMicroseconds = 0; endMicroseconds = 25000000 }
    )
    montageCases = @(
        [ordered]@{
            id = 'remove-middle-interval'
            fixture = 'montage-source.mp4'
            clips = @(
                @{ sourceStartMicroseconds = 0; sourceEndMicroseconds = 1000000; outputStartFrame = 0; outputEndFrame = 30; expectedColor = 'red'; expectedFrequencyHz = 440 },
                @{ sourceStartMicroseconds = 2000000; sourceEndMicroseconds = 3000000; outputStartFrame = 30; outputEndFrame = 60; expectedColor = 'blue'; expectedFrequencyHz = 880 }
            )
            removed = @{ sourceStartMicroseconds = 1000000; sourceEndMicroseconds = 2000000; color = 'yellow'; frequencyHz = 660 }
            caption = @{ startFrame = 10; endFrame = 20; text = 'MARKER' }
            cropSafeRegion = @{ x = 460; y = 850; width = 160; height = 220; expectedColor = 'green' }
        }
    )
}
[IO.File]::WriteAllText((Join-Path $output 'media-oracle.v1.json'), ($oracle | ConvertTo-Json -Depth 30), $utf8)
"Prepared acceptance media: $output"
