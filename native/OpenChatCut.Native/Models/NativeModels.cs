using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenChatCut.Native.Models;

public sealed record ProjectSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("description")] string? Description = null);

public sealed class ProjectSnapshot
{
    [JsonPropertyName("meta")]
    public ProjectSummary Meta { get; init; } = new("", "未命名工程", 0);

    [JsonPropertyName("doc")]
    public JsonElement Doc { get; init; }

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = "";
}

public sealed class ProjectListResponse
{
    [JsonPropertyName("projects")]
    public List<ProjectSummary> Projects { get; init; } = [];
}

public sealed class NativeAgentToolCatalog
{
    [JsonPropertyName("tools")]
    public List<JsonElement> Tools { get; init; } = [];
}

public sealed record NativeAgentMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed class NativeAgentCreatedRun
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; init; } = "";
}

public sealed record NativeAgentEvent(string Type, JsonElement Data);

public sealed record NativeAgentRunResult(string Status, string Text);

public sealed class ProjectHistoryState
{
    [JsonPropertyName("canUndo")]
    public bool CanUndo { get; init; }

    [JsonPropertyName("canRedo")]
    public bool CanRedo { get; init; }
}

public sealed class NativeTrackView
{
    public NativeTrackView(string id, string label, string kind, IReadOnlyList<NativeClipView> clips)
    {
        Id = id;
        Label = label;
        Kind = kind;
        Clips = clips;
    }

    public string Id { get; }
    public string Label { get; }
    public string Kind { get; }
    public IReadOnlyList<NativeClipView> Clips { get; }
}

public sealed class NativeClipView
{
    public NativeClipView(
        string id,
        string name,
        string kind,
        string track,
        int startFrame,
        int durationInFrames,
        int fps,
        string? source)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Track = track;
        StartFrame = startFrame;
        DurationInFrames = durationInFrames;
        Fps = Math.Max(1, fps);
        Source = source;
    }

    public string Id { get; }
    public string Name { get; }
    public string Kind { get; }
    public string Track { get; }
    public int StartFrame { get; }
    public int DurationInFrames { get; }
    public int Fps { get; }
    public string? Source { get; }
    public string StartLabel => $"{StartFrame / (double)Fps:0.0}s";
    public string DurationLabel => $"{DurationInFrames / (double)Fps:0.0}s";
    public string KindLabel => Kind switch
    {
        "motion-graphic" => "MG",
        "video" => "VIDEO",
        "audio" => "AUDIO",
        "image" => "IMAGE",
        "text" => "TEXT",
        _ => Kind.ToUpperInvariant(),
    };
}

public sealed class ProjectProjection
{
    private ProjectProjection(
        string timelineName,
        int fps,
        int width,
        int height,
        int durationInFrames,
        IReadOnlyList<NativeTrackView> tracks)
    {
        TimelineName = timelineName;
        Fps = fps;
        Width = width;
        Height = height;
        DurationInFrames = durationInFrames;
        Tracks = tracks;
    }

    public string TimelineName { get; }
    public int Fps { get; }
    public int Width { get; }
    public int Height { get; }
    public int DurationInFrames { get; }
    public string DurationLabel => $"{DurationInFrames / (double)Math.Max(Fps, 1):0.0}s";
    public IReadOnlyList<NativeTrackView> Tracks { get; }

    public static ProjectProjection From(JsonElement doc)
    {
        var timelines = Property(doc, "timelines");
        var activeId = String(Property(doc, "activeTimelineId"));
        var timeline = timelines.ValueKind == JsonValueKind.Array
            ? timelines.EnumerateArray().FirstOrDefault(item => String(Property(item, "id")) == activeId)
            : default;
        if (timeline.ValueKind == JsonValueKind.Undefined && timelines.ValueKind == JsonValueKind.Array)
        {
            timeline = timelines.EnumerateArray().FirstOrDefault();
        }

        var fps = Integer(Property(timeline, "fps"), 30);
        var width = Integer(Property(timeline, "width"), 1920);
        var height = Integer(Property(timeline, "height"), 1080);
        var items = Property(timeline, "items");
        var trackOrder = Property(timeline, "trackOrder");
        var trackInfo = Property(timeline, "tracks");
        var trackIds = trackOrder.ValueKind == JsonValueKind.Array
            ? trackOrder.EnumerateArray().Select(value => String(value)).Where(id => id.Length > 0).ToList()
            : [];

        var clipsByTrack = new Dictionary<string, List<NativeClipView>>(StringComparer.Ordinal);
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var track = String(Property(item, "track"), "track_v1");
                if (!trackIds.Contains(track, StringComparer.Ordinal)) trackIds.Add(track);
                var clip = new NativeClipView(
                    String(Property(item, "id"), "clip"),
                    String(Property(item, "name"), "未命名片段"),
                    String(Property(item, "kind"), "video"),
                    track,
                    Integer(Property(item, "startFrame")),
                    Math.Max(1, Integer(Property(item, "durationInFrames"), 30)),
                    fps,
                    NullableString(Property(item, "src")));
                clipsByTrack.TryAdd(track, []);
                clipsByTrack[track].Add(clip);
            }
        }

        var tracks = trackIds.Select((id, index) =>
        {
            var kind = String(Property(Property(trackInfo, id), "kind"),
                id.StartsWith("a", StringComparison.OrdinalIgnoreCase) ? "audio" : "video");
            var prefix = kind == "audio" ? "A" : kind == "caption" ? "C" : "V";
            var label = $"{prefix}{index + 1}";
            clipsByTrack.TryGetValue(id, out var clips);
            return new NativeTrackView(id, label, kind, clips ?? []);
        }).ToList();

        var duration = clipsByTrack.Values.SelectMany(value => value)
            .Select(clip => clip.StartFrame + clip.DurationInFrames)
            .DefaultIfEmpty(fps)
            .Max();
        return new ProjectProjection(
            String(Property(timeline, "name"), "序列 1"),
            fps,
            width,
            height,
            duration,
            tracks);
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property
            : default;
    }

    private static string String(JsonElement value, string fallback = "")
    {
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    private static string? NullableString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int Integer(JsonElement value, int fallback = 0)
    {
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : fallback;
    }
}
