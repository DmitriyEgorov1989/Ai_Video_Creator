using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Подготавливает проверенный Master и связь Original → Master до перехода к анализу.</summary>
public sealed class PreparationStageExecutor : IJobStageExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IArtifactStore artifactStore;
    private readonly IMediaProbe mediaProbe;
    private readonly IMezzanineBuilder mezzanineBuilder;
    private readonly IMediaDecodeVerifier decodeVerifier;
    private readonly string workspaceRoot;
    private readonly MediaInputProfile profile;
    private readonly TimeSpan probeTimeout;
    private readonly TimeSpan normalizeTimeout;
    private readonly TimeSpan decodeTimeout;
    private readonly int maxOutputBytes;
    private readonly IStorageBudget storageBudget;

    /// <summary>Создаёт исполнитель из существующих адаптеров медиа и хранилища.</summary>
    /// <param name="artifactStore">Хранилище неизменяемых описаний артефактов.</param>
    /// <param name="mediaProbe">Адаптер чтения фактических характеристик медиа.</param>
    /// <param name="mezzanineBuilder">Адаптер нормализации Original при необходимости.</param>
    /// <param name="decodeVerifier">Адаптер полного декодирования Master без преобразования.</param>
    /// <param name="workspaceRoot">Корневой каталог с исходниками и результатами.</param>
    /// <param name="profile">Ограничения входного профиля MVP.</param>
    /// <param name="probeTimeout">Предельная длительность одного probe.</param>
    /// <param name="normalizeTimeout">Предельная длительность нормализации.</param>
    /// <param name="decodeTimeout">Предельная длительность полного декодирования.</param>
    /// <param name="maxOutputBytes">Лимит сохраняемой диагностики каждого потока процесса в байтах.</param>
    /// <param name="storageBudget">Проверка места перед копированием; <see langword="null"/> использует локальную политику по умолчанию.</param>
    public PreparationStageExecutor(
        IArtifactStore artifactStore,
        IMediaProbe mediaProbe,
        IMezzanineBuilder mezzanineBuilder,
        IMediaDecodeVerifier decodeVerifier,
        string workspaceRoot,
        MediaInputProfile profile,
        TimeSpan probeTimeout,
        TimeSpan normalizeTimeout,
        TimeSpan decodeTimeout,
        int maxOutputBytes,
        IStorageBudget? storageBudget = null)
    {
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.mediaProbe = mediaProbe ?? throw new ArgumentNullException(nameof(mediaProbe));
        this.mezzanineBuilder = mezzanineBuilder ?? throw new ArgumentNullException(nameof(mezzanineBuilder));
        this.decodeVerifier = decodeVerifier ?? throw new ArgumentNullException(nameof(decodeVerifier));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (probeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        if (normalizeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(normalizeTimeout));
        if (decodeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(decodeTimeout));
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        this.probeTimeout = probeTimeout;
        this.normalizeTimeout = normalizeTimeout;
        this.decodeTimeout = decodeTimeout;
        this.maxOutputBytes = maxOutputBytes;
        this.storageBudget = storageBudget ?? StorageBudgetRunner.Default;
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Progress.Stage != ProductionStage.Preparation)
            return StageExecutionResult.Permanent("preparation.invalid_stage", $"Preparation executor received stage '{job.Progress.Stage}'.");

        ArtifactRecord? source = await artifactStore.GetAsync(job.Request.SourceAssetId, cancellationToken);
        if (source is null)
            return StageExecutionResult.Permanent("preparation.source_missing", "The Original artifact was not found.");

        string sourcePath = SafeFileTree.Resolve(workspaceRoot, source.RelativePath);
        if (!File.Exists(sourcePath))
            return StageExecutionResult.Permanent("media.file_missing", "The Original file does not exist.");

        string sourceHash = await HashAsync(sourcePath, cancellationToken);
        if ((source.Sha256 is not null && !string.Equals(source.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase))
            || (source.SizeBytes is not null && source.SizeBytes != new FileInfo(sourcePath).Length))
            return StageExecutionResult.Permanent("preparation.source_changed", "The Original file no longer matches its registered hash or size.");
        ArtifactRecord? existing = await artifactStore.FindLatestAsync(job.JobId, "master-video", cancellationToken);
        if (existing?.ParentArtifactId == source.ArtifactId && existing.SizeBytes is > 0 && HasCompletedMapping(existing, sourceHash)
            && File.Exists(SafeFileTree.Resolve(workspaceRoot, existing.RelativePath)))
        {
            string existingPath = SafeFileTree.Resolve(workspaceRoot, existing.RelativePath);
            if (new FileInfo(existingPath).Length == existing.SizeBytes
                && await HashAsync(existingPath, cancellationToken) == existing.Sha256)
                return StageExecutionResult.Success();
        }

        MediaProbeResult probe = await mediaProbe.ProbeAsync(new ProbeRequest(sourcePath, probeTimeout, maxOutputBytes), cancellationToken);
        if (!probe.IsSuccess) return Failure(probe.ErrorCode!, probe.ErrorMessage!);
        MediaInputProfileResult validation = new MediaInputProfileValidator().Validate(sourcePath, probe.Metadata!, profile);
        if (!validation.IsSupported) return StageExecutionResult.Permanent(validation.ErrorCode!, validation.ErrorMessage!);

        ArtifactId masterId = ArtifactId.New();
        string masterPath = SafeFileTree.Resolve(workspaceRoot, $"data/jobs/{job.JobId}/normalized/{masterId}.mp4");
        MezzanineResult result = await mezzanineBuilder.BuildAsync(new MezzanineRequest(
            sourcePath, masterPath, source.ArtifactId.ToString(), masterId.ToString(), probe.Metadata!,
            normalizeTimeout, probeTimeout, decodeTimeout, maxOutputBytes), cancellationToken);
        if (!result.IsSuccess) return Failure(result.ErrorCode!, result.ErrorMessage!);

        // Builder проверяет decode созданного mezzanine; исходник без преобразования проверяется здесь.
        if (!result.WasNormalized)
        {
            MediaDecodeResult decoded = await decodeVerifier.VerifyAsync(new MediaDecodeRequest(
                result.PublishedPath, decodeTimeout, maxOutputBytes), cancellationToken);
            if (!decoded.IsSuccess) return Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            StageExecutionResult copied = await StorageBudgetRunner.RunAsync(storageBudget,
                StorageBudgetRunner.JobDirectory(masterPath), new FileInfo(sourcePath).Length + 1_048_576,
                async token =>
                {
                    string temporaryPath = masterPath + ".tmp";
                    try
                    {
                        await using (FileStream input = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        await using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            await input.CopyToAsync(output, token);
                            await output.FlushAsync(token);
                            output.Flush(flushToDisk: true);
                        }
                        token.ThrowIfCancellationRequested();
                        File.Move(temporaryPath, masterPath, overwrite: false);
                        return StageExecutionResult.Success();
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                }, failure => Failure(failure.Code, failure.Message), cancellationToken);
            if (copied.Kind != StageExecutionKind.Completed) return copied;
        }

        MediaProbeResult masterProbe = result.WasNormalized
            ? await mediaProbe.ProbeAsync(new ProbeRequest(result.PublishedPath, probeTimeout, maxOutputBytes), cancellationToken)
            : probe;
        if (!masterProbe.IsSuccess) return Failure(masterProbe.ErrorCode!, masterProbe.ErrorMessage!);
        if (masterProbe.Metadata!.VideoStreams.Any(video => video.IsVariableFrameRate == true || video.RotationDegrees is not null and not 0))
            return StageExecutionResult.Retryable("preparation.master_not_normalized", "The Master still requires frame rate or rotation normalization.");
        await using FileStream stream = new(masterPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        ArtifactRecord master = new(masterId, "master-video",
            Path.GetRelativePath(workspaceRoot, masterPath).Replace(Path.DirectorySeparatorChar, '/'),
            hash, stream.Length,
            JsonSerializer.Serialize(new { schemaVersion = 1, wasNormalized = result.WasNormalized, originalSha256 = sourceHash,
                originalMetadata = probe.Metadata, mediaMetadata = masterProbe.Metadata, mapping = result.Mapping }, JsonOptions),
            source.ArtifactId, "preparation/1", job.JobId);
        ArtifactRegistrationResult registered = await artifactStore.RegisterAsync(master, cancellationToken);
        return registered is ArtifactRegistrationResult.Rejected rejected
            ? StageExecutionResult.Permanent("preparation.artifact_rejected", rejected.Message)
            : StageExecutionResult.Success();
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool HasCompletedMapping(ArtifactRecord master, string sourceHash)
    {
        if (master.MetadataJson is null) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(master.MetadataJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1
                || !root.TryGetProperty("originalSha256", out JsonElement hash) || hash.ValueKind != JsonValueKind.String || hash.GetString() != sourceHash
                || !root.TryGetProperty("mapping", out JsonElement value)) return false;
            TimelineMapping? mapping = value.Deserialize<TimelineMapping>(JsonOptions);
            return mapping is not null && mapping.OriginalArtifactId == master.ParentArtifactId?.ToString()
                && mapping.MasterArtifactId == master.ArtifactId.ToString()
                && mapping.OriginalStartMicroseconds == 0 && mapping.MasterStartMicroseconds == 0
                && TryReadDuration(root, "originalMetadata", out long originalDuration)
                && TryReadDuration(root, "mediaMetadata", out long masterDuration)
                && mapping.OriginalDurationMicroseconds == originalDuration && mapping.MasterDurationMicroseconds == masterDuration;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDuration(JsonElement root, string property, out long duration)
    {
        duration = 0;
        return root.TryGetProperty(property, out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("durationMicroseconds", out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out duration) && duration > 0;
    }

    private static StageExecutionResult Failure(string code, string message) => code switch
    {
        "process.cancelled" => StageExecutionResult.Cancelled(code, message),
        "storage.job_quota_exceeded" => StageExecutionResult.Permanent(code, message),
        _ => StageExecutionResult.Retryable(code, message)
    };
}
