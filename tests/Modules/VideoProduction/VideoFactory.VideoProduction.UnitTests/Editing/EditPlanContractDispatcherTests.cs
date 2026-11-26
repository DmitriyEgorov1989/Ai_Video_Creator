using System.Text.Json;
using System.Text.Json.Nodes;
using VideoFactory.VideoProduction.Domain.Validation;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class EditPlanContractDispatcherTests
{
    [Fact]
    public void DispatchesV3WithTwoKnownMasterSources()
    {
        using JsonDocument document = CreateV3Document();

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(3, result.Version);
        Assert.Equal(EditPlanContractStatus.Accepted, result.Status);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void DispatchesV4WithCompleteEditingSkillAudit()
    {
        using JsonDocument document = CreateV4Document();

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(4, result.Version);
        Assert.Equal(EditPlanContractStatus.Accepted, result.Status);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("missing", "skill.missing")]
    [InlineData("duplicate", "skill.duplicate")]
    [InlineData("unknown", "skill.unknown")]
    public void RejectsIncompleteOrUnknownV4SkillAudit(string mutation, string expectedCode)
    {
        JsonObject plan = CreateV4Node();
        JsonArray skills = plan["skills"]!.AsArray();
        if (mutation == "missing") skills.RemoveAt(skills.Count - 1);
        if (mutation == "duplicate") skills[skills.Count - 1]!["id"] = "smart-reframe";
        if (mutation == "unknown") skills[skills.Count - 1]!["id"] = "free-form-code";
        using JsonDocument document = JsonDocument.Parse(plan.ToJsonString());

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(EditPlanContractStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    [Fact]
    public void MalformedV4SkillStatusReturnsIssueWithoutThrowing()
    {
        JsonObject plan = CreateV4Node();
        plan["skills"]![0]!["status"] = 42;
        using JsonDocument document = JsonDocument.Parse(plan.ToJsonString());

        EditPlanContractDispatch? result = null;
        Exception? exception = Record.Exception(() =>
            result = EditPlanContractDispatcher.Dispatch(document.RootElement));

        Assert.Null(exception);
        Assert.Equal(EditPlanContractStatus.Invalid, result?.Status);
        Assert.Contains(result!.Issues, issue => issue.Code == "skill.status_invalid");
    }

    [Fact]
    public void MalformedV4OutputRangeReturnsIssueWithoutThrowing()
    {
        JsonObject plan = CreateV4Node();
        plan["video"]!["clips"]![0]!["output"] = 42;
        using JsonDocument document = JsonDocument.Parse(plan.ToJsonString());

        EditPlanContractDispatch? result = null;
        Exception? exception = Record.Exception(() =>
            result = EditPlanContractDispatcher.Dispatch(document.RootElement));

        Assert.Null(exception);
        Assert.Equal(EditPlanContractStatus.Invalid, result?.Status);
        Assert.Contains(result!.Issues, issue => issue.Code == "plan.non_negative_integer_required");
    }

    [Theory]
    [InlineData("duplicate", "source.duplicate")]
    [InlineData("unknown", "source.unknown")]
    [InlineData("out-of-bounds", "timeline.source_out_of_bounds")]
    public void RejectsInvalidV3SourceReferences(string mutation, string expectedCode)
    {
        JsonObject plan = JsonNode.Parse(ValidV3Json)!.AsObject();
        JsonArray sources = plan["sources"]!.AsArray();
        JsonArray clips = plan["video"]!["clips"]!.AsArray();
        if (mutation == "duplicate") sources[1]!["masterAssetId"] = "master-a";
        if (mutation == "unknown") clips[1]!["sourceAssetId"] = "unknown-master";
        if (mutation == "out-of-bounds") clips[1]!["master"]!["endMicroseconds"] = 1_000_001;
        using JsonDocument document = JsonDocument.Parse(plan.ToJsonString());

        EditPlanContractDispatch result = EditPlanContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(EditPlanContractStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    private static JsonDocument CreateV3Document() => JsonDocument.Parse(ValidV3Json);

    private static JsonDocument CreateV4Document() => JsonDocument.Parse(CreateV4Node().ToJsonString());

    private static JsonObject CreateV4Node()
    {
        JsonObject plan = JsonNode.Parse(ValidV3Json)!.AsObject();
        plan["schemaVersion"] = 4;
        string[] skillIds =
        [
            "multi-clip-director", "semantic-story", "hook-builder", "smart-cut",
            "best-shot", "pacing-director", "speech-tightening", "caption-director",
            "visual-director", "smart-reframe", "audio-director"
        ];
        plan["skills"] = new JsonArray(skillIds.Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id,
            ["version"] = "1.0.0",
            ["status"] = id == "speech-tightening" ? "unavailable" : "applied",
            ["reasons"] = new JsonArray(new JsonObject
            {
                ["code"] = "decision.recorded",
                ["message"] = "The deterministic decision was recorded."
            })
        }).ToArray());
        return plan;
    }

    private const string ValidV3Json = """
            {
              "schemaVersion": 3,
              "jobId": "job", "variantId": "variant", "revisionId": "revision", "analysisRevisionId": "analysis",
              "output": { "durationFrames": 60 },
              "sources": [
                { "originalAssetId": "original-a", "masterAssetId": "master-a", "durationMicroseconds": 1000000 },
                { "originalAssetId": "original-b", "masterAssetId": "master-b", "durationMicroseconds": 1000000 }
              ],
              "video": { "clips": [
                { "sourceAssetId": "master-a", "source": { "startMicroseconds": 0, "endMicroseconds": 1000000 }, "master": { "startMicroseconds": 0, "endMicroseconds": 1000000 }, "output": { "startFrame": 0, "endFrame": 30 } },
                { "sourceAssetId": "master-b", "source": { "startMicroseconds": 0, "endMicroseconds": 1000000 }, "master": { "startMicroseconds": 0, "endMicroseconds": 1000000 }, "output": { "startFrame": 30, "endFrame": 60 } }
              ] },
              "effects": [],
              "captions": { "words": [] },
              "audio": { "sourceGain": 1 },
              "skills": []
            }
            """;

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
