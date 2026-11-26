using System.Text.Json;
using System.Text.Json.Nodes;
using VideoFactory.VideoProduction.Application.Packaging;

namespace VideoFactory.VideoProduction.UnitTests.Packaging;

public sealed class FinalManifestContractDispatcherTests
{
    [Fact]
    public void AcceptsV2WithCompleteEvidenceChain()
    {
        using JsonDocument document = JsonDocument.Parse(ValidV2);

        FinalManifestContractDispatch result = FinalManifestContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(2, result.Version);
        Assert.Equal(FinalManifestContractStatus.Accepted, result.Status);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("criticReport", "manifest.critic_report_required")]
    [InlineData("audit", "manifest.audit_required")]
    public void RejectsV2WithIncompleteEvidence(string property, string expectedCode)
    {
        using JsonDocument source = JsonDocument.Parse(ValidV2);
        Dictionary<string, object?> manifest = JsonSerializer.Deserialize<Dictionary<string, object?>>(ValidV2)!;
        if (property == "audit") manifest.Remove("audit");
        if (property == "criticReport")
        {
            using JsonDocument chain = JsonDocument.Parse(manifest["criticChain"]!.ToString()!);
            manifest["criticChain"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["remountNumber"] = 0,
                    ["sourceRevisionId"] = "source",
                    ["sourcePlanHash"] = "hash",
                    ["render"] = new { artifactId = "render", sha256 = "render-hash", sizeBytes = 20 },
                    ["status"] = "accepted"
                }
            };
        }
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(manifest));

        FinalManifestContractDispatch result = FinalManifestContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(FinalManifestContractStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    [Fact]
    public void ContinuesToReadLegacyV1()
    {
        using JsonDocument document = JsonDocument.Parse("{\"schemaVersion\":1,\"jobId\":\"job\",\"variantId\":\"variant\"}");

        FinalManifestContractDispatch result = FinalManifestContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(FinalManifestContractStatus.Accepted, result.Status);
    }

    [Fact]
    public void RejectsDisconnectedRemountChain()
    {
        JsonObject manifest = JsonNode.Parse(ValidV2)!.AsObject();
        JsonObject first = manifest["criticChain"]![0]!.AsObject();
        first["status"] = "remount";
        first["correctionRevisionId"] = "correction-a";
        JsonObject second = first.DeepClone().AsObject();
        second["remountNumber"] = 1;
        second["sourceRevisionId"] = "correction-b";
        second["status"] = "accepted";
        second["correctionRevisionId"] = null;
        manifest["criticChain"]!.AsArray().Add(second);
        manifest["planRevisionId"] = "correction-b";
        using JsonDocument document = JsonDocument.Parse(manifest.ToJsonString());

        FinalManifestContractDispatch result = FinalManifestContractDispatcher.Dispatch(document.RootElement);

        Assert.Equal(FinalManifestContractStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, issue => issue.Code == "manifest.remount_chain_broken");
    }

    [Fact]
    public void MalformedV2ReturnsIssuesWithoutThrowing()
    {
        JsonObject manifest = JsonNode.Parse(ValidV2)!.AsObject();
        manifest["audit"]!["schemaVersion"] = "invalid";
        manifest["criticChain"]![0]!["status"] = 42;
        using JsonDocument document = JsonDocument.Parse(manifest.ToJsonString());

        FinalManifestContractDispatch? result = null;
        Exception? exception = Record.Exception(() =>
            result = FinalManifestContractDispatcher.Dispatch(document.RootElement));

        Assert.Null(exception);
        Assert.Equal(FinalManifestContractStatus.Invalid, result?.Status);
    }

    private const string ValidV2 = """
        {
          "schemaVersion": 2,
          "jobId": "job",
          "variantId": "variant",
          "planRevisionId": "approved",
          "planHash": "plan-hash",
          "audit": { "schemaVersion": 1, "skillCount": 11, "sha256": "audit-hash" },
          "criticChain": [{
            "remountNumber": 0,
            "sourceRevisionId": "approved",
            "sourcePlanHash": "plan-hash",
            "render": { "artifactId": "render", "sha256": "render-hash", "sizeBytes": 20 },
            "criticReport": { "artifactId": "report", "sha256": "report-hash", "sizeBytes": 10 },
            "status": "accepted",
            "correctionRevisionId": null
          }],
          "render": { "artifactId": "render", "sha256": "render-hash", "sizeBytes": 20 },
          "qa": { "artifactId": "qa", "sha256": "qa-hash", "sizeBytes": 30 },
          "final": { "artifactId": "final", "sha256": "final-hash", "sizeBytes": 20 }
        }
        """;
}
