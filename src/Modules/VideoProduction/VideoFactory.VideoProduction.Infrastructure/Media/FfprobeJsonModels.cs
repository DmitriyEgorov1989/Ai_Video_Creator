using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

internal sealed class FfprobeDocument
{
    public FfprobeFormat? Format { get; init; }

    public List<FfprobeStream>? Streams { get; init; }
}

internal sealed class FfprobeFormat
{
    [JsonPropertyName("format_name")]
    public string? FormatName { get; init; }

    public string? Duration { get; init; }
}

internal sealed class FfprobeStream
{
    public int Index { get; init; }

    [JsonPropertyName("codec_type")]
    public string? CodecType { get; init; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    [JsonPropertyName("pix_fmt")]
    public string? PixelFormat { get; init; }

    [JsonPropertyName("avg_frame_rate")]
    public string? AverageFrameRate { get; init; }

    [JsonPropertyName("r_frame_rate")]
    public string? RealFrameRate { get; init; }

    [JsonPropertyName("sample_aspect_ratio")]
    public string? SampleAspectRatio { get; init; }

    [JsonPropertyName("color_transfer")]
    public string? ColorTransfer { get; init; }

    [JsonPropertyName("color_primaries")]
    public string? ColorPrimaries { get; init; }

    [JsonPropertyName("color_space")]
    public string? ColorSpace { get; init; }

    [JsonPropertyName("sample_rate")]
    public JsonElement? SampleRate { get; init; }

    public int? Channels { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }

    [JsonPropertyName("side_data_list")]
    public List<FfprobeSideData>? SideDataList { get; init; }
}

internal sealed class FfprobeSideData
{
    public int? Rotation { get; init; }
}
