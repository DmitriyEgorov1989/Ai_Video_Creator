using System.Text.Json;
using VideoFactory.VideoProduction.Infrastructure.Rendering;

namespace VideoFactory.Bot.Tests.Rendering;

public sealed class RenderRequestJsonValidatorTests
{
    [Fact]
    public async Task AcceptsTheSharedPositiveFixture()
    {
        string json = await ReadFixtureAsync("valid-render-request.json");

        IReadOnlyList<RenderRequestJsonValidationIssue> issues = RenderRequestJsonValidator.Validate(json);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task RejectsNullCollectionsAndUnknownEnumsWithoutThrowing()
    {
        string json = await ReadFixtureAsync("invalid-null-and-enum.json");

        IReadOnlyList<RenderRequestJsonValidationIssue> issues =
            RenderRequestJsonValidator.Validate(json);

        Assert.Contains(issues, issue => issue.Code == "video_empty");
        Assert.Contains(issues, issue => issue.Code == "enum_unknown");
    }

    [Fact]
    public void RejectsMalformedJsonWithoutThrowing()
    {
        IReadOnlyList<RenderRequestJsonValidationIssue> issues =
            RenderRequestJsonValidator.Validate("{\"plan\":");

        Assert.Contains(issues, issue => issue.Code == "json_invalid");
    }

    [Fact]
    public async Task RejectsAClipThatIsNotBackedByAnAsset()
    {
        string json = (await ReadFixtureAsync("valid-render-request.json"))
            .Replace("\"masterAssetId\": \"master-1\"", "\"masterAssetId\": \"unknown-master\"", StringComparison.Ordinal);

        IReadOnlyList<RenderRequestJsonValidationIssue> issues = RenderRequestJsonValidator.Validate(json);

        Assert.Contains(issues, issue => issue.Code == "asset_unknown");
    }

    [Fact]
    public async Task V2SchemaAllowsV3AndV4PlansButRejectsLegacyV2Plan()
    {
        string path = Path.Combine(FindRepositoryRoot(), "contracts", "render-request.schema.json");
        using JsonDocument schema = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        string[] references = schema.RootElement
            .GetProperty("$defs")
            .GetProperty("v2")
            .GetProperty("properties")
            .GetProperty("plan")
            .GetProperty("oneOf")
            .EnumerateArray()
            .Select(item => item.GetProperty("$ref").GetString()!)
            .ToArray();

        Assert.Equal(["edit-plan.v3.schema.json", "edit-plan.v4.schema.json"], references);
        Assert.DoesNotContain("edit-plan.v2.schema.json", references);
    }

    private static async Task<string> ReadFixtureAsync(string fileName)
    {
        string path = Path.Combine(FindRepositoryRoot(), "contracts", "fixtures", fileName);
        return await File.ReadAllTextAsync(path);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ai_Video_Creator.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
