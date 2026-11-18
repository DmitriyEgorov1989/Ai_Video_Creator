using System.Text.Json;

namespace VideoFactory.VideoProduction.Infrastructure.Rendering;

/// <summary>
/// Проверяет JSON render request на границе между .NET и render-host.
/// </summary>
public static class RenderRequestJsonValidator
{
    private static readonly HashSet<string> KnownCrops = ["fit", "centerCrop"];
    private static readonly HashSet<string> KnownCaptions = ["none", "minimal", "emphasis"];
    private static readonly HashSet<string> KnownThemes = ["light", "dark"];

    /// <summary>
    /// Возвращает структурированные ошибки контракта; для некорректного JSON исключение не выбрасывается.
    /// </summary>
    /// <param name="json">Проверяемый JSON-документ; значение <see langword="null"/> считается некорректным.</param>
    /// <returns>Список ошибок. Пустой список означает, что документ принят проверками границы.</returns>
    public static IReadOnlyList<RenderRequestJsonValidationIssue> Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [new("request_null", "$", "Render request must be a JSON object.")];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return Validate(document.RootElement);
        }
        catch (JsonException exception)
        {
            return [new("json_invalid", "$", exception.Message)];
        }
    }

    private static List<RenderRequestJsonValidationIssue> Validate(JsonElement root)
    {
        List<RenderRequestJsonValidationIssue> issues = [];
        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("request_null", "$", "Render request must be a JSON object."));
            return issues;
        }

        RequiredInt(root, "schemaVersion", "schema_version", issues, expected: 1);
        string? jobId = RequiredString(root, "jobId", "jobId", issues);
        string? variantId = RequiredString(root, "variantId", "variantId", issues);
        RequiredString(root, "outputPath", "outputPath", issues);

        if (!root.TryGetProperty("plan", out JsonElement plan) || plan.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("plan_null", "plan", "Plan must be an object."));
        }
        else
        {
            string? planJobId = RequiredString(plan, "jobId", "plan.jobId", issues);
            string? planVariantId = RequiredString(plan, "variantId", "plan.variantId", issues);
            if (jobId is not null && planJobId is not null && jobId != planJobId)
                issues.Add(new("plan_job_id_mismatch", "plan.jobId", "Plan jobId must match the render request jobId."));
            if (variantId is not null && planVariantId is not null && variantId != planVariantId)
                issues.Add(new("plan_variant_id_mismatch", "plan.variantId", "Plan variantId must match the render request variantId."));

            ValidatePlan(plan, issues);
        }

        HashSet<string> referencedSegmentIds = new(StringComparer.Ordinal);
        if (!root.TryGetProperty("plan", out JsonElement planForReferences) || planForReferences.ValueKind != JsonValueKind.Object)
        {
            // Структурная ошибка плана уже добавлена выше.
        }
        else
        {
            CollectReferencedSegmentIds(planForReferences, referencedSegmentIds);
        }

        HashSet<string> assetIds = new(StringComparer.Ordinal);
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() == 0)
        {
            issues.Add(new("assets_empty", "assets", "At least one local asset is required."));
        }
        else
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? id = RequiredString(asset, "id", "assets[].id", issues);
                if (id is not null && !assetIds.Add(id))
                    issues.Add(new("asset_duplicate", "assets[].id", "Asset IDs must be unique."));
                string? path = RequiredString(asset, "path", "assets[].path", issues);
                if (path is not null && !Path.IsPathFullyQualified(path))
                    issues.Add(new("asset_path_not_absolute", "assets[].path", "Asset paths must be absolute."));
            }
        }

        foreach (string segmentId in referencedSegmentIds)
        {
            if (!assetIds.Contains(segmentId))
                issues.Add(new("asset_unknown", "plan", $"No asset is registered for segment '{segmentId}'."));
        }

        return issues;
    }

    private static void CollectReferencedSegmentIds(JsonElement plan, HashSet<string> segmentIds)
    {
        if (plan.ValueKind == JsonValueKind.Object
            && plan.TryGetProperty("video", out JsonElement video)
            && video.ValueKind == JsonValueKind.Object
            && video.TryGetProperty("clips", out JsonElement clips)
            && clips.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement clip in clips.EnumerateArray())
            {
                if (clip.TryGetProperty("segmentId", out JsonElement segmentId) && segmentId.ValueKind == JsonValueKind.String)
                    segmentIds.Add(segmentId.GetString()!);
            }
        }

        if (plan.ValueKind == JsonValueKind.Object
            && plan.TryGetProperty("audio", out JsonElement audio)
            && audio.ValueKind == JsonValueKind.Object
            && audio.TryGetProperty("sourceClips", out JsonElement sourceClips)
            && sourceClips.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement clip in sourceClips.EnumerateArray())
            {
                if (clip.TryGetProperty("segmentId", out JsonElement segmentId) && segmentId.ValueKind == JsonValueKind.String)
                    segmentIds.Add(segmentId.GetString()!);
            }
        }
    }

    private static void ValidatePlan(JsonElement plan, List<RenderRequestJsonValidationIssue> issues)
    {
        RequiredInt(plan, "schemaVersion", "plan.schemaVersion", issues, expected: 1);
        RequiredString(plan, "revisionId", "plan.revisionId", issues);
        RequiredString(plan, "analysisRevisionId", "plan.analysisRevisionId", issues);
        RequiredInt(plan, "seed", "plan.seed", issues);

        if (!plan.TryGetProperty("template", out JsonElement template) || template.ValueKind != JsonValueKind.String || template.GetString() != "dynamic")
            issues.Add(new("template_unknown", "plan.template", "Only the dynamic template is supported."));

        if (!plan.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("output_null", "plan.output", "Output profile is required."));
            return;
        }

        int duration = RequiredPositiveInt(output, "durationFrames", "plan.output.durationFrames", issues);
        RequiredPositiveInt(output, "width", "plan.output.width", issues);
        RequiredPositiveInt(output, "height", "plan.output.height", issues);
        if (!output.TryGetProperty("frameRate", out JsonElement frameRate) || frameRate.ValueKind != JsonValueKind.Object)
            issues.Add(new("frame_rate_invalid", "plan.output.frameRate", "A positive rational frame rate is required."));

        if (!plan.TryGetProperty("templateProps", out JsonElement props) || props.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("template_props_null", "plan.templateProps", "Template properties are required."));
        }
        else
        {
            CheckEnum(props, "captions", KnownCaptions, "plan.templateProps.captions", issues);
            CheckEnum(props, "theme", KnownThemes, "plan.templateProps.theme", issues);
        }

        HashSet<string> segmentIds = [];
        if (!plan.TryGetProperty("video", out JsonElement video) || video.ValueKind != JsonValueKind.Object
            || !video.TryGetProperty("clips", out JsonElement clips) || clips.ValueKind != JsonValueKind.Array || clips.GetArrayLength() == 0)
        {
            issues.Add(new("video_empty", "plan.video.clips", "At least one video clip is required."));
        }
        else
        {
            int index = 0;
            foreach (JsonElement clip in clips.EnumerateArray())
            {
                string path = $"plan.video.clips[{index++}]";
                string? segmentId = RequiredString(clip, "segmentId", $"{path}.segmentId", issues);
                if (segmentId is not null) segmentIds.Add(segmentId);
                CheckEnum(clip, "crop", KnownCrops, $"{path}.crop", issues);
                ValidateRange(clip, "output", $"{path}.output", duration, issues);
            }
        }

        if (!plan.TryGetProperty("audio", out JsonElement audio) || audio.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("audio_invalid", "plan.audio", "Audio tracks must be initialized arrays."));
        }
        else
        {
            if (!audio.TryGetProperty("sourceClips", out JsonElement sourceClips) || sourceClips.ValueKind != JsonValueKind.Array)
                issues.Add(new("audio_invalid", "plan.audio.sourceClips", "Audio tracks must be initialized arrays."));
            else
            {
                int index = 0;
                foreach (JsonElement clip in sourceClips.EnumerateArray())
                {
                    string path = $"plan.audio.sourceClips[{index++}]";
                    string? segmentId = RequiredString(clip, "segmentId", $"{path}.segmentId", issues);
                    if (segmentId is not null) segmentIds.Add(segmentId);
                    ValidateRange(clip, "output", $"{path}.output", duration, issues);
                }
            }

            if (!audio.TryGetProperty("musicClips", out JsonElement music) || music.ValueKind != JsonValueKind.Array)
                issues.Add(new("audio_invalid", "plan.audio.musicClips", "Audio tracks must be initialized arrays."));
            else if (music.GetArrayLength() != 0)
                issues.Add(new("music_unsupported", "plan.audio.musicClips", "Music is not supported by the MVP."));
        }

        ValidateCollection(plan, "captions", "items", "captions_invalid", issues);
        ValidateCollection(plan, "overlays", "items", "overlays_invalid", issues);

        if (plan.TryGetProperty("captions", out JsonElement captions) && captions.TryGetProperty("items", out JsonElement captionItems) && captionItems.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement caption in captionItems.EnumerateArray())
            {
                string path = $"plan.captions.items[{index++}]";
                RequiredString(caption, "text", $"{path}.text", issues);
                ValidateRange(caption, "output", $"{path}.output", duration, issues);
            }
        }

        if (plan.TryGetProperty("overlays", out JsonElement overlays) && overlays.TryGetProperty("items", out JsonElement overlayItems) && overlayItems.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement overlay in overlayItems.EnumerateArray())
            {
                string path = $"plan.overlays.items[{index++}]";
                CheckEnum(overlay, "type", ["text"], $"{path}.type", issues);
                RequiredString(overlay, "text", $"{path}.text", issues);
                ValidateRange(overlay, "output", $"{path}.output", duration, issues);
            }
        }
    }

    private static void ValidateCollection(JsonElement parent, string property, string child, string code, List<RenderRequestJsonValidationIssue> issues)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(child, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            issues.Add(new(code, $"plan.{property}.{child}", "Collection must be initialized as an array."));
    }

    private static void ValidateRange(JsonElement parent, string property, string path, int duration, List<RenderRequestJsonValidationIssue> issues)
    {
        if (!parent.TryGetProperty(property, out JsonElement range) || range.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("range_invalid", path, "A range must contain integer startFrame and endFrame."));
            return;
        }

        int start = RequiredInt(range, "startFrame", $"{path}.startFrame", issues);
        int end = RequiredInt(range, "endFrame", $"{path}.endFrame", issues);
        if (start < 0 || end <= start || end > duration)
            issues.Add(new("range_out_of_bounds", path, "The range must be inside the output timeline."));
    }

    private static void CheckEnum(JsonElement parent, string property, HashSet<string> known, string path, List<RenderRequestJsonValidationIssue> issues)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String || !known.Contains(value.GetString()!))
            issues.Add(new("enum_unknown", path, "Unknown enum value."));
    }

    private static string? RequiredString(JsonElement parent, string property, string path, List<RenderRequestJsonValidationIssue> issues)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
        {
            issues.Add(new("required_string", path, "A non-empty string is required."));
            return null;
        }

        return value.GetString();
    }

    private static int RequiredPositiveInt(JsonElement parent, string property, string path, List<RenderRequestJsonValidationIssue> issues)
    {
        int value = RequiredInt(parent, property, path, issues);
        if (value <= 0) issues.Add(new("positive_integer", path, "A positive integer is required."));
        return value;
    }

    private static int RequiredInt(JsonElement parent, string property, string path, List<RenderRequestJsonValidationIssue> issues, int? expected = null)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out JsonElement value) || !value.TryGetInt32(out int result))
        {
            issues.Add(new("integer_invalid", path, "An integer is required."));
            return 0;
        }

        if (expected is int expectedValue && result != expectedValue)
            issues.Add(new("schema_version", path, $"Only value {expectedValue} is supported."));
        return result;
    }
}

/// <summary>Описывает одну ошибку JSON render request.</summary>
/// <param name="Code">Стабильный код ошибки.</param>
/// <param name="Path">Путь к ошибочному полю.</param>
/// <param name="Message">Диагностическое сообщение.</param>
public sealed record RenderRequestJsonValidationIssue(string Code, string Path, string Message);
