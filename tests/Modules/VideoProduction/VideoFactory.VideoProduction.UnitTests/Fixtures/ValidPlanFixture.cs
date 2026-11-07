using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Fixtures;

internal sealed record ValidPlanFixture(
    EditPlan Plan,
    AnalysisResult Analysis,
    IReadOnlyDictionary<ArtifactId, MediaAsset> Assets,
    IReadOnlyDictionary<TemplateId, TemplateDefinition> Templates)
{
    public static ValidPlanFixture Create()
    {
        ArtifactId originalId = ArtifactId.New();
        ArtifactId masterId = ArtifactId.New();
        ArtifactId musicId = ArtifactId.New();
        ArtifactId imageId = ArtifactId.New();
        MediaAsset original = new(originalId, CreateVideoMetadata(20_000_000));
        MediaAsset master = new(masterId, CreateVideoMetadata(18_000_000));
        MediaAsset music = new(musicId, CreateAudioMetadata(30_000_000));
        MediaAsset image = new(imageId, new MediaMetadata(
            "png",
            1_000_000,
            [new VideoStreamMetadata(0, "png", 512, 512, null, null, null)],
            []));
        Dictionary<ArtifactId, MediaAsset> assets = new()
        {
            [originalId] = original,
            [masterId] = master,
            [musicId] = music,
            [imageId] = image
        };

        SegmentId firstSegmentId = SegmentId.New();
        SegmentId secondSegmentId = SegmentId.New();
        SourceMapping firstMapping = new(
            originalId,
            new OriginalRange(1_000_000, 4_000_000),
            masterId,
            new MasterRange(0, 3_000_000));
        SourceMapping secondMapping = new(
            originalId,
            new OriginalRange(5_000_000, 8_000_000),
            masterId,
            new MasterRange(3_000_000, 6_000_000));
        RevisionId analysisRevision = RevisionId.New();
        AnalysisResult analysis = new(
            1,
            analysisRevision,
            originalId,
            masterId,
            [],
            [],
            [new Scene(new MasterRange(0, 6_000_000))],
            [
                new AllowedSegment(firstSegmentId, firstMapping),
                new AllowedSegment(secondSegmentId, secondMapping)
            ]);

        OutputRange firstOutput = new(0, 90);
        OutputRange secondOutput = new(90, 180);
        EditPlan plan = new(
            1,
            JobId.New(),
            VariantId.New(),
            RevisionId.New(),
            analysisRevision,
            42,
            new OutputProfile(1080, 1920, new FrameRate(30, 1), 180),
            TemplateId.Dynamic,
            new TemplateProps(CaptionPreset.Emphasis, ColorTheme.Dark, "Product demo"),
            new VideoTrack(
            [
                new VideoClip(firstSegmentId, firstMapping, firstOutput, CropPreset.CenterCrop),
                new VideoClip(secondSegmentId, secondMapping, secondOutput, CropPreset.CenterCrop)
            ]),
            new AudioTrack(
            [
                new SourceAudioClip(firstSegmentId, firstOutput, 1m),
                new SourceAudioClip(secondSegmentId, secondOutput, 1m)
            ],
            [new MusicClip(musicId, new AssetRange(0, 6_000_000), new OutputRange(0, 180), 0.25m)]),
            new CaptionTrack([new Caption("Hello", new OutputRange(0, 30))]),
            new OverlayTrack(
            [
                new TextOverlay("New", new OutputRange(0, 30)),
                new ImageOverlay(imageId, new OutputRange(30, 60))
            ]));
        Dictionary<TemplateId, TemplateDefinition> templates = new()
        {
            [TemplateId.Dynamic] = new TemplateDefinition(
                TemplateId.Dynamic,
                [CaptionPreset.None, CaptionPreset.Minimal, CaptionPreset.Emphasis],
                supportsTitle: true)
        };

        return new ValidPlanFixture(plan, analysis, assets, templates);
    }

    private static MediaMetadata CreateVideoMetadata(long duration) => new(
        "mp4",
        duration,
        [new VideoStreamMetadata(0, "h264", 1080, 1920, new FrameRate(30, 1), false, 0)],
        [new AudioStreamMetadata(1, "aac", 48_000, 2)]);

    private static MediaMetadata CreateAudioMetadata(long duration) => new(
        "m4a",
        duration,
        [],
        [new AudioStreamMetadata(0, "aac", 48_000, 2)]);
}
