using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Создаёт AAC audio stem через FFmpeg.</summary>
public sealed class FfmpegAudioStemBuilder : IAudioStemBuilder
{
    private readonly IProcessRunner processRunner;
    private readonly string executablePath;

    /// <summary>Создаёт audio stem builder.</summary>
    public FfmpegAudioStemBuilder(IProcessRunner processRunner, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.processRunner = processRunner;
        this.executablePath = executablePath;
    }

    /// <inheritdoc />
    public async Task<AudioStemResult> BuildAsync(
        AudioStemRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceMetadata.AudioStreams.Length == 0)
        {
            return AudioStemResult.Skipped();
        }

        string directory = Path.GetDirectoryName(request.OutputPath)
            ?? throw new ArgumentException("Output path must include a directory.", nameof(request));
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(request.OutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            ProcessExecutionResult result = await processRunner.RunAsync(
                new ProcessStartRequest(
                    executablePath,
                    [
                        "-y", "-i", request.SourcePath,
                        "-map", "0:a:0",
                        "-vn",
                        "-c:a", "aac",
                        temporaryPath
                    ],
                    request.Timeout,
                    request.MaxOutputBytes),
                cancellationToken);

            if (!result.IsSuccess || !File.Exists(temporaryPath))
            {
                return AudioStemResult.Failed(
                    result.ErrorCode ?? "media.audio_stem_failed",
                    result.ErrorMessage ?? "FFmpeg did not create the audio stem.");
            }

            File.Move(temporaryPath, request.OutputPath, overwrite: false);
            return AudioStemResult.Created(request.OutputPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
