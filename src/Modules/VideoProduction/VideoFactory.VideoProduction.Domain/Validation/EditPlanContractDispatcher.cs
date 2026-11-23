using System.Text.Json;

namespace VideoFactory.VideoProduction.Domain.Validation;

/// <summary>Результат выбора и проверки версии монтажного контракта.</summary>
public sealed record EditPlanContractDispatch(
    int? Version,
    EditPlanContractStatus Status,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>Состояние монтажного контракта после dispatch.</summary>
public enum EditPlanContractStatus
{
    Accepted,
    Invalid,
    UnsupportedVersion
}

/// <summary>Выбирает реализацию контракта без исполнения произвольного JSON-кода.</summary>
public static class EditPlanContractDispatcher
{
    /// <summary>Проверяет JSON-документ V1 или V2.</summary>
    /// <param name="document">JSON-документ монтажного плана; не может быть <see langword="null"/>.</param>
    /// <returns>Результат dispatch с диагностикой ошибок.</returns>
    public static EditPlanContractDispatch Dispatch(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return Invalid(null, "plan.object_required", "Plan must be a JSON object.");
        }

        if (!document.TryGetProperty("schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int schemaVersion))
        {
            return Invalid(null, "plan.schema_version_required", "Plan schemaVersion must be an integer.");
        }

        return schemaVersion switch
        {
            1 => new(1, EditPlanContractStatus.Accepted, []),
            2 => ValidateV2(document),
            _ => new(schemaVersion, EditPlanContractStatus.UnsupportedVersion,
                [new ValidationIssue("plan.schema_version_unsupported", "schemaVersion", "Unsupported edit plan schema version.")])
        };
    }

    private static EditPlanContractDispatch ValidateV2(JsonElement document)
    {
        List<ValidationIssue> issues = [];
        RequiredObject(document, "output", issues);
        RequiredObject(document, "source", issues);
        RequiredObject(document, "video", issues);
        RequiredObject(document, "captions", issues);
        RequiredObject(document, "audio", issues);
        RequiredArray(document, "effects", issues);
        RequiredArray(document, "skills", issues);

        foreach (string property in new[] { "jobId", "variantId", "revisionId", "analysisRevisionId" })
        {
            if (!document.TryGetProperty(property, out JsonElement value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                issues.Add(new ValidationIssue("plan.required_string", property, "A non-empty string is required."));
            }
        }

        if (issues.Count == 0)
        {
            ValidateV2Semantics(document, issues);
        }

        return new(2, issues.Count == 0 ? EditPlanContractStatus.Accepted : EditPlanContractStatus.Invalid, issues);
    }

    private static void ValidateV2Semantics(JsonElement document, List<ValidationIssue> issues)
    {
        JsonElement output = document.GetProperty("output");
        int duration = PositiveInt(output, "durationFrames", "output.durationFrames", issues);
        JsonElement source = document.GetProperty("source");
        RequiredString(source, "originalAssetId", "source.originalAssetId", issues);
        RequiredString(source, "masterAssetId", "source.masterAssetId", issues);

        JsonElement video = document.GetProperty("video");
        if (!video.TryGetProperty("clips", out JsonElement clips) || clips.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new ValidationIssue("video.clips_required", "video.clips", "Video clips must be an array."));
        }
        else
        {
            int expectedStart = 0;
            foreach (JsonElement clip in clips.EnumerateArray())
            {
                if (clip.ValueKind != JsonValueKind.Object || !clip.TryGetProperty("output", out JsonElement range))
                {
                    issues.Add(new ValidationIssue("video.clip_invalid", "video.clips", "Each clip must contain an output range."));
                    continue;
                }

                int start = PositiveOrZeroInt(range, "startFrame", "video.clips.output.startFrame", issues);
                int end = PositiveInt(range, "endFrame", "video.clips.output.endFrame", issues);
                if (start != expectedStart)
                    issues.Add(new ValidationIssue("video.timeline_not_contiguous", "video.clips", "Video clips must be contiguous."));
                if (end > duration)
                    issues.Add(new ValidationIssue("timeline.output_out_of_bounds", "video.clips.output", "Output range exceeds plan duration."));
                expectedStart = end;
            }

            if (expectedStart != duration)
                issues.Add(new ValidationIssue("video.duration_mismatch", "video.clips", "Video clips must end at output duration."));
        }

        ValidateEffects(document.GetProperty("effects"), duration, issues);
        ValidateCaptions(document.GetProperty("captions"), duration, issues);
    }

    private static void ValidateEffects(JsonElement effects, int duration, List<ValidationIssue> issues)
    {
        foreach (JsonElement effect in effects.EnumerateArray())
        {
            if (effect.ValueKind != JsonValueKind.Object
                || !effect.TryGetProperty("type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String
                || !new[] { "speed", "transition", "blur" }.Contains(type.GetString(), StringComparer.Ordinal))
            {
                issues.Add(new ValidationIssue("effect.unsupported", "effects", "Effect type is not supported."));
            }

            if (effect.TryGetProperty("code", out _)
                || effect.TryGetProperty("path", out _)
                || effect.TryGetProperty("script", out _))
            {
                issues.Add(new ValidationIssue("effect.code_forbidden", "effects", "Effects cannot contain executable code or paths."));
            }
        }
    }

    private static void ValidateCaptions(JsonElement captions, int duration, List<ValidationIssue> issues)
    {
        if (!captions.TryGetProperty("words", out JsonElement words) || words.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new ValidationIssue("captions.words_required", "captions.words", "Caption words must be an array."));
            return;
        }

        foreach (JsonElement word in words.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ValidationIssue("captions.word_invalid", "captions.words", "Caption word must be an object."));
                continue;
            }

            if (word.TryGetProperty("output", out JsonElement range))
            {
                int end = PositiveInt(range, "endFrame", "captions.words.output.endFrame", issues);
                if (end > duration)
                    issues.Add(new ValidationIssue("timeline.output_out_of_bounds", "captions.words.output", "Caption range exceeds plan duration."));
            }
            else
            {
                issues.Add(new ValidationIssue("captions.word_output_required", "captions.words.output", "Caption word output range is required."));
            }
        }
    }

    private static int PositiveInt(JsonElement objectValue, string property, string path, List<ValidationIssue> issues)
    {
        if (objectValue.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number > 0)
            return number;
        issues.Add(new ValidationIssue("plan.positive_integer_required", path, "A positive integer is required."));
        return 0;
    }

    private static int PositiveOrZeroInt(JsonElement objectValue, string property, string path, List<ValidationIssue> issues)
    {
        if (objectValue.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number >= 0)
            return number;
        issues.Add(new ValidationIssue("plan.non_negative_integer_required", path, "A non-negative integer is required."));
        return 0;
    }

    private static void RequiredString(JsonElement objectValue, string property, string path, List<ValidationIssue> issues)
    {
        if (!objectValue.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            issues.Add(new ValidationIssue("plan.required_string", path, "A non-empty string is required."));
    }

    private static void RequiredObject(JsonElement objectValue, string property, List<ValidationIssue> issues)
    {
        if (!objectValue.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            issues.Add(new ValidationIssue("plan.object_required", property, "A JSON object is required."));
    }

    private static void RequiredArray(JsonElement objectValue, string property, List<ValidationIssue> issues)
    {
        if (!objectValue.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            issues.Add(new ValidationIssue("plan.array_required", property, "A JSON array is required."));
    }

    private static EditPlanContractDispatch Invalid(int? version, string code, string message) =>
        new(version, EditPlanContractStatus.Invalid, [new ValidationIssue(code, "plan", message)]);
}
