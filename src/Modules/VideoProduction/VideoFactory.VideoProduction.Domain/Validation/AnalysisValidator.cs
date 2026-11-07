using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Domain.Validation;

public static class AnalysisValidator
{
    public static ImmutableArray<ValidationIssue> Validate(
        AnalysisResult analysis,
        IReadOnlyDictionary<ArtifactId, MediaAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(assets);

        List<ValidationIssue> issues = [];
        if (analysis.SchemaVersion != 1)
        {
            Add(issues, "analysis.schema_version", "schemaVersion", "Only analysis schema version 1 is supported.");
        }

        if (analysis.RevisionId.IsEmpty) Add(issues, "analysis.revision_empty", "revisionId", "Revision ID is required.");
        MediaAsset? original = GetAsset(analysis.OriginalAssetId, "originalAssetId", assets, issues);
        MediaAsset? master = GetAsset(analysis.MasterAssetId, "masterAssetId", assets, issues);
        CheckVideoAsset(original, "originalAssetId", issues);
        CheckVideoAsset(master, "masterAssetId", issues);

        ValidateCollections(analysis, issues);
        ValidateMasterRanges(analysis, master, issues);
        ValidateSegments(analysis, original, master, issues);

        return [.. issues];
    }

    private static MediaAsset? GetAsset(
        ArtifactId id,
        string path,
        IReadOnlyDictionary<ArtifactId, MediaAsset> assets,
        List<ValidationIssue> issues)
    {
        if (id.IsEmpty)
        {
            Add(issues, "asset.id_empty", path, "Artifact ID is required.");
            return null;
        }

        if (!assets.TryGetValue(id, out MediaAsset? asset))
        {
            Add(issues, "asset.unknown", path, "The analysis refers to an unknown asset.");
        }
        else if (asset.Id != id)
        {
            Add(issues, "asset.catalog_id_mismatch", path, "Asset catalog key does not match the asset ID.");
        }

        return asset;
    }

    private static void CheckVideoAsset(
        MediaAsset? asset,
        string path,
        List<ValidationIssue> issues)
    {
        if (asset is not null && asset.Metadata.VideoStreams.Length == 0)
        {
            Add(issues, "asset.video_stream_missing", path, "Analysis source asset must contain a video stream.");
        }
    }

    private static void ValidateCollections(AnalysisResult analysis, List<ValidationIssue> issues)
    {
        if (analysis.Speech.IsDefault) Add(issues, "analysis.speech_uninitialized", "speech", "Speech collection must be initialized.");
        if (analysis.Pauses.IsDefault) Add(issues, "analysis.pauses_uninitialized", "pauses", "Pauses collection must be initialized.");
        if (analysis.Scenes.IsDefault) Add(issues, "analysis.scenes_uninitialized", "scenes", "Scenes collection must be initialized.");
        if (analysis.AllowedSegments.IsDefault) Add(issues, "analysis.segments_uninitialized", "allowedSegments", "Allowed segments must be initialized.");
    }

    private static void ValidateMasterRanges(
        AnalysisResult analysis,
        MediaAsset? master,
        List<ValidationIssue> issues)
    {
        if (master is null) return;

        if (!analysis.Speech.IsDefault)
        {
            for (int index = 0; index < analysis.Speech.Length; index++)
            {
                SpeechSegment speech = analysis.Speech[index];
                CheckMasterBounds(speech.Interval, master, $"speech[{index}].interval", issues);
                if (string.IsNullOrWhiteSpace(speech.Text))
                {
                    Add(issues, "analysis.speech_text_empty", $"speech[{index}].text", "Speech text cannot be empty.");
                }

                if (speech.Words.IsDefault)
                {
                    Add(issues, "analysis.words_uninitialized", $"speech[{index}].words", "Words collection must be initialized.");
                    continue;
                }

                long previousWordEnd = speech.Interval.StartMicroseconds;
                for (int wordIndex = 0; wordIndex < speech.Words.Length; wordIndex++)
                {
                    WordTiming word = speech.Words[wordIndex];
                    string wordPath = $"speech[{index}].words[{wordIndex}]";
                    if (string.IsNullOrWhiteSpace(word.Text))
                        Add(issues, "analysis.word_text_empty", $"{wordPath}.text", "Word text cannot be empty.");
                    if (word.Interval.DurationMicroseconds <= 0 || !speech.Interval.Contains(word.Interval))
                    {
                        Add(issues, "analysis.word_outside_speech", $"{wordPath}.interval", "Word must be a positive interval inside its speech segment.");
                    }
                    if (word.Interval.StartMicroseconds < previousWordEnd)
                        Add(issues, "analysis.words_not_ordered", $"{wordPath}.interval", "Words must be ordered and non-overlapping.");
                    previousWordEnd = Math.Max(previousWordEnd, word.Interval.EndMicroseconds);
                }
            }
        }

        if (!analysis.Pauses.IsDefault)
        {
            for (int index = 0; index < analysis.Pauses.Length; index++)
                CheckMasterBounds(analysis.Pauses[index], master, $"pauses[{index}]", issues);
        }

        if (!analysis.Scenes.IsDefault)
        {
            for (int index = 0; index < analysis.Scenes.Length; index++)
                CheckMasterBounds(analysis.Scenes[index].Interval, master, $"scenes[{index}].interval", issues);
        }
    }

    private static void ValidateSegments(
        AnalysisResult analysis,
        MediaAsset? original,
        MediaAsset? master,
        List<ValidationIssue> issues)
    {
        if (analysis.AllowedSegments.IsDefault) return;

        HashSet<SegmentId> ids = [];
        for (int index = 0; index < analysis.AllowedSegments.Length; index++)
        {
            AllowedSegment segment = analysis.AllowedSegments[index];
            string path = $"allowedSegments[{index}]";
            if (segment.Id.IsEmpty) Add(issues, "analysis.segment_id_empty", $"{path}.id", "Segment ID is required.");
            else if (!ids.Add(segment.Id)) Add(issues, "analysis.segment_id_duplicate", $"{path}.id", "Segment IDs must be unique.");

            if (segment.Mapping.OriginalAssetId != analysis.OriginalAssetId)
                Add(issues, "analysis.original_asset_mismatch", $"{path}.mapping.originalAssetId", "Mapping must use the analysis original asset.");
            if (segment.Mapping.MasterAssetId != analysis.MasterAssetId)
                Add(issues, "analysis.master_asset_mismatch", $"{path}.mapping.masterAssetId", "Mapping must use the analysis master asset.");
            if (segment.Mapping.Original.DurationMicroseconds != segment.Mapping.Master.DurationMicroseconds)
                Add(issues, "timeline.mapping_duration_mismatch", $"{path}.mapping", "Original and master intervals must have equal duration when no time transformation is defined.");

            if (original is not null && !IsInside(segment.Mapping.Original, original.Metadata.DurationMicroseconds))
                Add(issues, "timeline.original_out_of_bounds", $"{path}.mapping.original", "Original interval exceeds asset duration.");
            if (master is not null)
                CheckMasterBounds(segment.Mapping.Master, master, $"{path}.mapping.master", issues);
        }
    }

    private static void CheckMasterBounds(
        MasterRange range,
        MediaAsset master,
        string path,
        List<ValidationIssue> issues)
    {
        if (range.DurationMicroseconds <= 0 || range.EndMicroseconds > master.Metadata.DurationMicroseconds)
            Add(issues, "timeline.master_out_of_bounds", path, "Master interval exceeds asset duration.");
    }

    private static bool IsInside(OriginalRange range, long durationMicroseconds) =>
        range.DurationMicroseconds > 0 && range.EndMicroseconds <= durationMicroseconds;

    private static void Add(List<ValidationIssue> issues, string code, string path, string message) =>
        issues.Add(new ValidationIssue(code, path, message));
}
