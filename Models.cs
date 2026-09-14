using System.Text.Json.Serialization;
using System.Windows;

namespace LabelMeWpf;

public sealed class AnnotationFile
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "5.0.0";

    [JsonPropertyName("flags")]
    public Dictionary<string, bool> Flags { get; set; } = [];

    [JsonPropertyName("shapes")]
    public List<AnnotationShape> Shapes { get; set; } = [];

    [JsonPropertyName("imagePath")]
    public string ImagePath { get; set; } = "";

    [JsonPropertyName("imageData")]
    public string? ImageData { get; set; }

    [JsonPropertyName("imageHeight")]
    public int ImageHeight { get; set; }

    [JsonPropertyName("imageWidth")]
    public int ImageWidth { get; set; }
}

public sealed class AnnotationShape
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("points")]
    public List<double[]> Points { get; set; } = [];

    [JsonPropertyName("group_id")]
    public int? GroupId { get; set; }

    [JsonPropertyName("shape_type")]
    public string ShapeType { get; set; } = "polygon";

    [JsonPropertyName("flags")]
    public Dictionary<string, bool> Flags { get; set; } = [];

    [JsonIgnore]
    public string DisplayText => $"{Label}  ·  {(ShapeType == "rectangle" ? "矩形" : "多边形")}";

    public IEnumerable<Point> ToPoints() => Points.Select(p => new Point(p[0], p[1]));
}

public sealed class ProjectState
{
    public int FormatVersion { get; set; }
    public string? ImageFolder { get; set; }
    public List<string> Labels { get; set; } = [];
    public HashSet<string> CompletedImages { get; set; } = [];
    public int CurrentIndex { get; set; }
}
