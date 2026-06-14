using STranslate.Plugin;

namespace STranslate.Core;

internal enum OcrLayoutSource
{
    Provider,
    Smart,
    NoMerge
}

internal sealed class OcrLayoutBlock
{
    public string Text { get; set; } = string.Empty;

    public List<BoxPoint> BoxPoints { get; set; } = [];

    public List<List<BoxPoint>> LineBoxPoints { get; set; } = [];

    public OcrLayoutSource Source { get; set; }

    public double Confidence { get; set; } = 1;

    internal OcrContent ToOcrContent() =>
        new()
        {
            Text = Text,
            BoxPoints = BoxPoints.Select(point => new BoxPoint(point.X, point.Y)).ToList()
        };
}
