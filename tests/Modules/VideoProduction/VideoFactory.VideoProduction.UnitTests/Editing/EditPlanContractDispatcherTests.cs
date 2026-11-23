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
}
