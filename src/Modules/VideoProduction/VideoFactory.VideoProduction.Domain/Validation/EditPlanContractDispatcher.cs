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
    private static readonly HashSet<string> V4SkillIds = new(StringComparer.Ordinal)
    {
        "multi-clip-director", "semantic-story", "hook-builder", "smart-cut",
        "best-shot", "pacing-director", "speech-tightening", "caption-director",
        "visual-director", "smart-reframe", "audio-director"
    };

    /// <summary>Проверяет JSON-документ V1, V2, V3 или V4.</summary>
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
            3 => ValidateV3(document),
            4 => ValidateV4(document),
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

        ValidateEffects(document.GetProperty("effects"), document.GetProperty("skills"), duration, issues);
        ValidateCaptions(document.GetProperty("captions"), duration, issues);
        ValidateAudio(document.GetProperty("audio"), issues);
    }

    private static void ValidateEffects(JsonElement effects, JsonElement skills, int duration, List<ValidationIssue> issues)
    {
        HashSet<string> appliedSkills = skills.EnumerateArray()
            .Where(skill => skill.ValueKind == JsonValueKind.Object
                && skill.TryGetProperty("status", out JsonElement status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() == "applied"
                && skill.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String)
            .Select(skill => skill.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> ids = new(StringComparer.Ordinal);
        List<(int Start, int End)> transitions = [];
        foreach (JsonElement effect in effects.EnumerateArray())
        {
            if (effect.ValueKind != JsonValueKind.Object
                || !effect.TryGetProperty("type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String
                || !new[] { "speed", "transition", "blur", "dynamicZoom", "textHighlight", "visualTransition" }.Contains(type.GetString(), StringComparer.Ordinal))
            {
                issues.Add(new ValidationIssue("effect.unsupported", "effects", "Effect type is not supported."));
                continue;
            }

            if (effect.TryGetProperty("code", out _)
                || effect.TryGetProperty("path", out _)
                || effect.TryGetProperty("script", out _))
            {
                issues.Add(new ValidationIssue("effect.code_forbidden", "effects", "Effects cannot contain executable code or paths."));
            }

            if (type.GetString() is not ("dynamicZoom" or "textHighlight" or "visualTransition"))
                continue;
            if (!effect.TryGetProperty("id", out JsonElement id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())
                || !ids.Add(id.GetString()!))
                issues.Add(new ValidationIssue("effect.identity", "effects.id", "Enriched effect IDs must be present and unique."));
            if (!effect.TryGetProperty("sourceSkillId", out JsonElement sourceSkill)
                || sourceSkill.ValueKind != JsonValueKind.String
                || !appliedSkills.Contains(sourceSkill.GetString() ?? string.Empty))
                issues.Add(new ValidationIssue("effect.provenance", "effects.sourceSkillId", "Effect must reference an applied skill."));
            if (!effect.TryGetProperty("output", out JsonElement output))
            {
                issues.Add(new ValidationIssue("effect.output_required", "effects.output", "Enriched effect output is required."));
                continue;
            }

            int start = PositiveOrZeroInt(output, "startFrame", "effects.output.startFrame", issues);
            int end = PositiveInt(output, "endFrame", "effects.output.endFrame", issues);
            if (end <= start || end > duration)
                issues.Add(new ValidationIssue("effect.output_invalid", "effects.output", "Effect output must be inside the plan duration."));
            if (type.GetString() == "visualTransition" && end > start)
                transitions.Add((start, end));
        }

        transitions.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (int index = 1; index < transitions.Count; index++)
        {
            if (transitions[index].Start < transitions[index - 1].End)
                issues.Add(new ValidationIssue("effect.conflict", "effects", "Visual transitions cannot overlap."));
        }
    }

    private static EditPlanContractDispatch ValidateV3(JsonElement document)
    {
        List<ValidationIssue> issues = [];
        RequiredObject(document, "output", issues);
        RequiredArray(document, "sources", issues);
        RequiredObject(document, "video", issues);
        RequiredObject(document, "captions", issues);
        RequiredObject(document, "audio", issues);
        RequiredArray(document, "effects", issues);
        RequiredArray(document, "skills", issues);

        foreach (string property in new[] { "jobId", "variantId", "revisionId", "analysisRevisionId" })
        {
            RequiredString(document, property, property, issues);
        }

        if (issues.Count == 0)
        {
            ValidateV3Semantics(document, issues);
        }

        return new(3, issues.Count == 0 ? EditPlanContractStatus.Accepted : EditPlanContractStatus.Invalid, issues);
    }

    private static void ValidateV3Semantics(JsonElement document, List<ValidationIssue> issues)
    {
        int duration = PositiveInt(document.GetProperty("output"), "durationFrames", "output.durationFrames", issues);
        Dictionary<string, long> masterDurations = new(StringComparer.Ordinal);
        HashSet<string> originalIds = new(StringComparer.Ordinal);
        JsonElement sources = document.GetProperty("sources");
        if (sources.GetArrayLength() == 0)
        {
            issues.Add(new ValidationIssue("source.empty", "sources", "At least one source is required."));
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ValidationIssue("source.invalid", "sources", "Each source must be an object."));
                continue;
            }

            string? originalId = ReadRequiredString(source, "originalAssetId", "sources.originalAssetId", issues);
            string? masterId = ReadRequiredString(source, "masterAssetId", "sources.masterAssetId", issues);
            long masterDuration = PositiveLong(source, "durationMicroseconds", "sources.durationMicroseconds", issues);
            if (originalId is not null && !originalIds.Add(originalId))
                issues.Add(new ValidationIssue("source.duplicate", "sources.originalAssetId", "Original source IDs must be unique."));
            if (masterId is not null && !masterDurations.TryAdd(masterId, masterDuration))
                issues.Add(new ValidationIssue("source.duplicate", "sources.masterAssetId", "Master source IDs must be unique."));
        }

        JsonElement video = document.GetProperty("video");
        if (!video.TryGetProperty("clips", out JsonElement clips)
            || clips.ValueKind != JsonValueKind.Array
            || clips.GetArrayLength() == 0)
        {
            issues.Add(new ValidationIssue("video.clips_required", "video.clips", "At least one video clip is required."));
        }
        else
        {
            int expectedStart = 0;
            foreach (JsonElement clip in clips.EnumerateArray())
            {
                if (clip.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new ValidationIssue("video.clip_invalid", "video.clips", "Each clip must be an object."));
                    continue;
                }

                string? masterId = ReadRequiredString(clip, "sourceAssetId", "video.clips.sourceAssetId", issues);
                long masterDuration = 0;
                if (masterId is not null && !masterDurations.TryGetValue(masterId, out masterDuration))
                    issues.Add(new ValidationIssue("source.unknown", "video.clips.sourceAssetId", "Clip must reference a known Master source."));
                if (clip.TryGetProperty("source", out JsonElement sourceRange))
                    ValidateSourceRange(sourceRange, long.MaxValue, "video.clips.source", issues);
                else
                    issues.Add(new ValidationIssue("timeline.source_out_of_bounds", "video.clips.source", "Source range is required."));
                if (masterId is not null && clip.TryGetProperty("master", out JsonElement masterRange))
                    ValidateSourceRange(masterRange, masterDuration, "video.clips.master", issues);
                else if (masterId is not null)
                    issues.Add(new ValidationIssue("timeline.source_out_of_bounds", "video.clips.master", "Master range is required."));

                if (!clip.TryGetProperty("output", out JsonElement output))
                {
                    issues.Add(new ValidationIssue("video.clip_invalid", "video.clips.output", "Each clip must contain an output range."));
                    continue;
                }

                int start = PositiveOrZeroInt(output, "startFrame", "video.clips.output.startFrame", issues);
                int end = PositiveInt(output, "endFrame", "video.clips.output.endFrame", issues);
                if (start != expectedStart)
                    issues.Add(new ValidationIssue("video.timeline_not_contiguous", "video.clips", "Video clips must be contiguous."));
                if (end > duration)
                    issues.Add(new ValidationIssue("timeline.output_out_of_bounds", "video.clips.output", "Output range exceeds plan duration."));
                expectedStart = end;
            }

            if (expectedStart != duration)
                issues.Add(new ValidationIssue("video.duration_mismatch", "video.clips", "Video clips must end at output duration."));
        }

        ValidateEffects(document.GetProperty("effects"), document.GetProperty("skills"), duration, issues);
        ValidateCaptions(document.GetProperty("captions"), duration, issues);
        ValidateAudio(document.GetProperty("audio"), issues);
    }

    private static EditPlanContractDispatch ValidateV4(JsonElement document)
    {
        List<ValidationIssue> issues = [.. ValidateV3(document).Issues];
        if (document.TryGetProperty("skills", out JsonElement skills)
            && skills.ValueKind == JsonValueKind.Array)
        {
            ValidateV4Skills(skills, issues);
        }

        return new(4, issues.Count == 0 ? EditPlanContractStatus.Accepted : EditPlanContractStatus.Invalid, issues);
    }

    private static void ValidateV4Skills(JsonElement skills, List<ValidationIssue> issues)
    {
        HashSet<string> found = new(StringComparer.Ordinal);
        foreach (JsonElement skill in skills.EnumerateArray())
        {
            if (skill.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new ValidationIssue("skill.invalid", "skills", "Each skill audit entry must be an object."));
                continue;
            }

            string? id = ReadRequiredString(skill, "id", "skills.id", issues);
            RequiredString(skill, "version", "skills.version", issues);
            if (id is not null && !V4SkillIds.Contains(id))
                issues.Add(new ValidationIssue("skill.unknown", "skills.id", "Skill audit contains an unknown skill."));
            else if (id is not null && !found.Add(id))
                issues.Add(new ValidationIssue("skill.duplicate", "skills.id", "Skill audit IDs must be unique."));

            if (!skill.TryGetProperty("status", out JsonElement status)
                || status.ValueKind != JsonValueKind.String
                || status.GetString() is not ("applied" or "skipped" or "unavailable"))
            {
                issues.Add(new ValidationIssue("skill.status_invalid", "skills.status", "Skill status must be applied, skipped or unavailable."));
            }

            if (!skill.TryGetProperty("reasons", out JsonElement reasons)
                || reasons.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new ValidationIssue("skill.reasons_required", "skills.reasons", "Skill reasons must be an array."));
                continue;
            }

            foreach (JsonElement reason in reasons.EnumerateArray())
            {
                if (reason.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new ValidationIssue("skill.reason_invalid", "skills.reasons", "Each skill reason must be an object."));
                    continue;
                }

                RequiredString(reason, "code", "skills.reasons.code", issues);
                RequiredString(reason, "message", "skills.reasons.message", issues);
            }
        }

        foreach (string missing in V4SkillIds.Except(found, StringComparer.Ordinal))
            issues.Add(new ValidationIssue("skill.missing", "skills", $"Skill audit is missing '{missing}'."));
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

        if (!captions.TryGetProperty("groups", out JsonElement groups))
            return;
        if (groups.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new ValidationIssue("captions.group_invalid", "captions.groups", "Caption groups must be an array."));
            return;
        }

        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object
                || !group.TryGetProperty("text", out JsonElement text)
                || text.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text.GetString())
                || !group.TryGetProperty("maxLines", out JsonElement maxLines)
                || !maxLines.TryGetInt32(out int lineCount)
                || lineCount is < 1 or > 2
                || !HasKnownString(group, "highlight", "none", "activeWord")
                || !HasKnownString(group, "animation", "none", "fade")
                || !HasKnownString(group, "placement", "bottomSafe")
                || !group.TryGetProperty("words", out JsonElement groupWords)
                || groupWords.ValueKind != JsonValueKind.Array
                || groupWords.GetArrayLength() == 0
                || !group.TryGetProperty("output", out JsonElement groupOutput))
            {
                issues.Add(new ValidationIssue("captions.group_invalid", "captions.groups", "Caption group must use bounded lines, words and safe presentation values."));
                continue;
            }

            int groupStart = PositiveOrZeroInt(groupOutput, "startFrame", "captions.groups.output.startFrame", issues);
            int groupEnd = PositiveInt(groupOutput, "endFrame", "captions.groups.output.endFrame", issues);
            if (groupEnd <= groupStart || groupEnd > duration)
                issues.Add(new ValidationIssue("captions.group_invalid", "captions.groups.output", "Caption group range must be inside the output timeline."));
        }
    }

    private static bool HasKnownString(JsonElement value, string property, params string[] known) =>
        value.TryGetProperty(property, out JsonElement item)
        && item.ValueKind == JsonValueKind.String
        && known.Contains(item.GetString(), StringComparer.Ordinal);

    private static void ValidateAudio(JsonElement audio, List<ValidationIssue> issues)
    {
        if (!audio.TryGetProperty("sourceGain", out JsonElement sourceGain)
            || sourceGain.ValueKind != JsonValueKind.Number
            || !sourceGain.TryGetDecimal(out decimal gain)
            || gain is < 0 or > 4)
        {
            issues.Add(new ValidationIssue("audio.source_gain_invalid", "audio.sourceGain", "Source gain must be between 0 and 4."));
        }

        if (audio.TryGetProperty("ducking", out JsonElement ducking)
            && (ducking.ValueKind != JsonValueKind.Number
                || !ducking.TryGetDecimal(out decimal duckingValue)
                || duckingValue is < 0 or > 1
                || !audio.TryGetProperty("musicAssetId", out JsonElement musicAsset)
                || musicAsset.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(musicAsset.GetString())))
        {
            issues.Add(new ValidationIssue("audio.ducking_invalid", "audio.ducking", "Ducking requires a music asset and a value between 0 and 1."));
        }
    }

    private static int PositiveInt(JsonElement objectValue, string property, string path, List<ValidationIssue> issues)
    {
        if (objectValue.ValueKind == JsonValueKind.Object
            && objectValue.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number > 0)
            return number;
        issues.Add(new ValidationIssue("plan.positive_integer_required", path, "A positive integer is required."));
        return 0;
    }

    private static int PositiveOrZeroInt(JsonElement objectValue, string property, string path, List<ValidationIssue> issues)
    {
        if (objectValue.ValueKind == JsonValueKind.Object
            && objectValue.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number >= 0)
            return number;
        issues.Add(new ValidationIssue("plan.non_negative_integer_required", path, "A non-negative integer is required."));
        return 0;
    }

    private static long PositiveLong(JsonElement objectValue, string property, string path, List<ValidationIssue> issues)
    {
        if (objectValue.ValueKind == JsonValueKind.Object
            && objectValue.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
            && number > 0)
            return number;
        issues.Add(new ValidationIssue("plan.positive_integer_required", path, "A positive integer is required."));
        return 0;
    }

    private static string? ReadRequiredString(
        JsonElement objectValue,
        string property,
        string path,
        List<ValidationIssue> issues)
    {
        if (objectValue.ValueKind == JsonValueKind.Object
            && objectValue.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString();
        issues.Add(new ValidationIssue("plan.required_string", path, "A non-empty string is required."));
        return null;
    }

    private static void ValidateSourceRange(
        JsonElement range,
        long sourceDuration,
        string path,
        List<ValidationIssue> issues)
    {
        if (range.ValueKind != JsonValueKind.Object
            || !range.TryGetProperty("startMicroseconds", out JsonElement startValue)
            || startValue.ValueKind != JsonValueKind.Number
            || !startValue.TryGetInt64(out long start)
            || !range.TryGetProperty("endMicroseconds", out JsonElement endValue)
            || endValue.ValueKind != JsonValueKind.Number
            || !endValue.TryGetInt64(out long end)
            || start < 0
            || end <= start
            || end > sourceDuration)
        {
            issues.Add(new ValidationIssue("timeline.source_out_of_bounds", path, "Source range must be inside its Master duration."));
        }
    }

    private static void RequiredString(JsonElement objectValue, string property, string path, List<ValidationIssue> issues)
    {
        if (objectValue.ValueKind != JsonValueKind.Object
            || !objectValue.TryGetProperty(property, out JsonElement value)
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
