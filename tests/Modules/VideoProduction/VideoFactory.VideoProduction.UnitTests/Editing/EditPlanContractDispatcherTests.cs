using System.Text.Json;
using VideoFactory.VideoProduction.Domain.Validation;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class EditPlanContractDispatcherTests
{
    [Fact]
    public void DispatchesV2AndReturnsNormalizedContract()
    {
        using JsonDocument document = JsonDocument.Parse("{\"schemaVersion\":2,\"jobId\":\"job\",\"variantId\":\"variant\",\"revisionId\":\"revision\",\"analysisRevisionId\":\"analysis\",\"output\":{\"durationFrames\":30},\"source\":{\"originalAssetId\":\"original\",\"masterAssetId\":\"master\"},\"video\":{\"clips\":[{\"output\":{\"startFrame\":0,\"endFrame\":30}}]},\"effects\":[],\"captions\":{\"words\":[]},\"audio\":{\"sourceGain\":1},\"skills\":[]}");

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(2, result.Version);
        Assert.Equal(EditPlanContractStatus.Accepted, result.Status);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void RejectsUnknownFutureVersionWithoutTryingToInterpretIt()
    {
        using JsonDocument document = JsonDocument.Parse("{\"schemaVersion\":99,\"code\":\"not-a-plan\"}");

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(EditPlanContractStatus.UnsupportedVersion, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "plan.schema_version_unsupported");
    }

    [Fact]
    public void RejectsCaptionGroupOutsideTwoLineSafeLayout()
    {
        using JsonDocument document = JsonDocument.Parse("{\"schemaVersion\":2,\"jobId\":\"job\",\"variantId\":\"variant\",\"revisionId\":\"revision\",\"analysisRevisionId\":\"analysis\",\"output\":{\"durationFrames\":30},\"source\":{\"originalAssetId\":\"original\",\"masterAssetId\":\"master\"},\"video\":{\"clips\":[{\"output\":{\"startFrame\":0,\"endFrame\":30}}]},\"effects\":[],\"captions\":{\"words\":[],\"groups\":[{\"text\":\"Привет мир\",\"words\":[{\"text\":\"Привет\",\"output\":{\"startFrame\":0,\"endFrame\":15}}],\"output\":{\"startFrame\":0,\"endFrame\":15},\"maxLines\":3,\"highlight\":\"activeWord\",\"animation\":\"fade\",\"placement\":\"bottomSafe\"}]},\"audio\":{\"sourceGain\":1},\"skills\":[]}");

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(EditPlanContractStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "captions.group_invalid");
    }

    [Fact]
    public void RejectsOverlappingVisualTransitions()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "schemaVersion": 2,
              "jobId": "job", "variantId": "variant", "revisionId": "revision", "analysisRevisionId": "analysis",
              "output": { "durationFrames": 60 },
              "source": { "originalAssetId": "original", "masterAssetId": "master" },
              "video": { "clips": [{ "output": { "startFrame": 0, "endFrame": 60 } }] },
              "effects": [
                { "id": "one", "type": "visualTransition", "output": { "startFrame": 20, "endFrame": 30 }, "sourceSkillId": "visual-director", "parameters": { "amount": 0.2 } },
                { "id": "two", "type": "visualTransition", "output": { "startFrame": 25, "endFrame": 35 }, "sourceSkillId": "visual-director", "parameters": { "amount": 0.2 } }
              ],
              "captions": { "words": [] },
              "audio": { "sourceGain": 1 },
              "skills": [{ "id": "visual-director", "version": "1.0.0", "status": "applied" }]
            }
            """);

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(EditPlanContractStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "effect.conflict");
    }

    [Fact]
    public void RejectsDuckingWithoutMusicAsset()
    {
        using JsonDocument document = JsonDocument.Parse("{\"schemaVersion\":2,\"jobId\":\"job\",\"variantId\":\"variant\",\"revisionId\":\"revision\",\"analysisRevisionId\":\"analysis\",\"output\":{\"durationFrames\":30},\"source\":{\"originalAssetId\":\"original\",\"masterAssetId\":\"master\"},\"video\":{\"clips\":[{\"output\":{\"startFrame\":0,\"endFrame\":30}}]},\"effects\":[],\"captions\":{\"words\":[]},\"audio\":{\"sourceGain\":1,\"ducking\":0.5},\"skills\":[]}");

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(EditPlanContractStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "audio.ducking_invalid");
    }
}
