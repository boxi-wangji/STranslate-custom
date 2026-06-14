using STranslate.Plugin;

namespace STranslate.Core;

internal static class OcrLayoutAnalyzer
{
    private const double MinSmartMergeConfidence = 0.48;

    internal static void Apply(OcrResult ocrResult, LayoutAnalysisMode mode)
    {
        if ((ocrResult.OcrContents.Count == 0 && ocrResult.Regions.Count == 0) || !Utilities.HasBoxPoints(ocrResult))
            return;

        var contents = AnalyzeBlocks(ocrResult, mode).Select(x => x.ToOcrContent()).ToList();
        ocrResult.OcrContents.Clear();
        ocrResult.OcrContents.AddRange(contents);
    }

    internal static List<OcrContent> Analyze(IReadOnlyList<OcrContent> contents, LayoutAnalysisMode mode)
        => AnalyzeBlocks(contents, mode).Select(x => x.ToOcrContent()).ToList();

    internal static List<OcrLayoutBlock> AnalyzeBlocks(OcrResult ocrResult, LayoutAnalysisMode mode)
    {
        return mode switch
        {
            LayoutAnalysisMode.NoMerge => CreateNoMergeBlocks(GetFlatContents(ocrResult)),
            LayoutAnalysisMode.Provider => HasProviderLayout(ocrResult)
                ? CreateProviderBlocks(ocrResult)
                : CreateProviderFallbackBlocks(ocrResult),
            LayoutAnalysisMode.Auto => HasProviderLayout(ocrResult)
                ? CreateProviderBlocks(ocrResult)
                : AnalyzeSmart(GetSmartSourceItems(ocrResult)),
            _ => AnalyzeSmart(GetSmartSourceItems(ocrResult))
        };
    }

    private static List<OcrLayoutBlock> CreateProviderFallbackBlocks(OcrResult ocrResult)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine("Provider layout requested but OCR result has no structured layout. Falling back to NoMerge.");
#endif
        return CreateNoMergeBlocks(GetFlatContents(ocrResult));
    }

    internal static List<OcrLayoutBlock> AnalyzeBlocks(IReadOnlyList<OcrContent> contents, LayoutAnalysisMode mode)
    {
        if (mode is LayoutAnalysisMode.NoMerge or LayoutAnalysisMode.Provider)
            return CreateNoMergeBlocks(contents);

        var items = CreateLayoutItems(contents);
        if (items.Count == 0)
            return CreateNoMergeBlocks(contents);

        return AnalyzeSmart(items);
    }

    private static List<OcrLayoutBlock> AnalyzeSmart(List<LayoutItem> items)
    {
        var lineSegments = BuildLineSegments(items);
        if (lineSegments.Count == 0)
            return [];

        var metrics = LayoutMetrics.From(lineSegments);
        var regions = BuildLayoutRegions(lineSegments, metrics);

        return OrderRegionsForReading(regions, metrics)
            .SelectMany(region => AnalyzeRegion(region, metrics))
            .ToList();
    }

    private static List<OcrLayoutBlock> AnalyzeRegion(LayoutRegion region, LayoutMetrics metrics)
    {
        var paragraphs = new List<ParagraphGroup>();

        foreach (var line in region.Lines.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left))
        {
            var target = FindBestParagraph(line, paragraphs, metrics);
            if (target == null)
                paragraphs.Add(new ParagraphGroup(line));
            else
                target.Paragraph.Add(line, target.Confidence);
        }

        return paragraphs
            .OrderBy(x => x.Bounds.Top)
            .ThenBy(x => x.Bounds.Left)
            .Select(x => x.ToLayoutBlock())
            .ToList();
    }

    private static bool HasProviderLayout(OcrResult ocrResult) =>
        ocrResult.Regions.Any(region => region.Paragraphs.Any(paragraph => paragraph.Lines.Count > 0));

    private static List<OcrContent> GetFlatContents(OcrResult ocrResult)
    {
        if (ocrResult.OcrContents.Count > 0)
            return ocrResult.OcrContents;

        return ocrResult.Regions
            .SelectMany(region => region.Paragraphs)
            .SelectMany(paragraph => paragraph.Lines)
            .ToList();
    }

    private static List<LayoutItem> GetSmartSourceItems(OcrResult ocrResult)
    {
        var contents = GetFlatContents(ocrResult);
        return CreateLayoutItems(contents);
    }

    private static List<OcrLayoutBlock> CreateProviderBlocks(OcrResult ocrResult)
    {
        var blocks = new List<OcrLayoutBlock>();

        foreach (var region in ocrResult.Regions)
        {
            foreach (var paragraph in region.Paragraphs)
            {
                var lines = paragraph.Lines
                    .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                    .ToList();
                if (lines.Count == 0)
                    continue;

                var text = JoinProviderParagraphText(lines);
                var lineBoxPoints = lines
                    .Where(line => line.BoxPoints.Count > 0)
                    .Select(line => CloneBoxPoints(line.BoxPoints))
                    .ToList();
                var boxPoints = paragraph.BoxPoints.Count > 0
                    ? CloneBoxPoints(paragraph.BoxPoints)
                    : CreateUnionBoxPoints(lineBoxPoints);

                blocks.Add(new OcrLayoutBlock
                {
                    Text = text,
                    BoxPoints = boxPoints,
                    LineBoxPoints = lineBoxPoints,
                    Source = OcrLayoutSource.Provider,
                    Confidence = 1
                });
            }
        }

        return blocks;
    }

    private static List<OcrLayoutBlock> CreateNoMergeBlocks(IReadOnlyList<OcrContent> contents) =>
        contents
            .Where(content => !string.IsNullOrWhiteSpace(content.Text))
            .Select(content => new OcrLayoutBlock
            {
                Text = content.Text,
                BoxPoints = CloneBoxPoints(content.BoxPoints),
                LineBoxPoints = content.BoxPoints.Count > 0 ? [CloneBoxPoints(content.BoxPoints)] : [],
                Source = OcrLayoutSource.NoMerge,
                Confidence = 1
            })
            .ToList();

    private static List<LayoutRegion> BuildLayoutRegions(List<LineSegment> lineSegments, LayoutMetrics metrics)
    {
        var regions = new List<LayoutRegion>();

        foreach (var line in lineSegments.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left))
        {
            var target = FindBestRegion(line, regions, metrics);
            if (target == null)
                regions.Add(new LayoutRegion(line));
            else
                target.Add(line);
        }

        return regions;
    }

    private static LayoutRegion? FindBestRegion(
        LineSegment line,
        List<LayoutRegion> regions,
        LayoutMetrics metrics)
    {
        LayoutRegion? bestRegion = null;
        var bestScore = double.NegativeInfinity;

        foreach (var region in regions)
        {
            if (!TryGetRegionAffinity(region, line, metrics, out var score))
                continue;

            if (score > bestScore)
            {
                bestScore = score;
                bestRegion = region;
            }
        }

        return bestRegion;
    }

    private static bool TryGetRegionAffinity(
        LayoutRegion region,
        LineSegment line,
        LayoutMetrics metrics,
        out double score)
    {
        score = double.NegativeInfinity;

        var bestReferenceScore = double.NegativeInfinity;
        foreach (var reference in region.Lines.TakeLast(4))
        {
            if (line.Bounds.Top < reference.Bounds.Top - metrics.LineHeight * 0.35)
                continue;

            var verticalGap = VerticalGap(reference.Bounds, line.Bounds);
            if (verticalGap > metrics.LineHeight * 3.2)
                continue;

            var horizontalOverlap = HorizontalOverlapRatio(reference.Bounds, line.Bounds);
            var leftDelta = Math.Abs(reference.Bounds.Left - line.Bounds.Left);
            var centerDelta = Math.Abs(reference.Bounds.CenterX - line.Bounds.CenterX);
            var hasRegionAffinity =
                horizontalOverlap >= 0.30 ||
                leftDelta <= metrics.LineHeight * 1.8 ||
                centerDelta <= Math.Max(reference.Bounds.Width, line.Bounds.Width) * 0.35;

            if (!hasRegionAffinity)
                continue;

            var leftScore = 1 - Math.Min(1, leftDelta / Math.Max(metrics.LineHeight * 2, 1));
            var centerScore = 1 - Math.Min(1, centerDelta / Math.Max(Math.Max(reference.Bounds.Width, line.Bounds.Width), 1));
            var gapScore = 1 - Math.Min(1, verticalGap / Math.Max(metrics.LineHeight * 3.2, 1));
            var widthDelta = Math.Abs(reference.Bounds.Width - line.Bounds.Width);
            var widthScore = 1 - Math.Min(1, widthDelta / Math.Max(Math.Max(reference.Bounds.Width, line.Bounds.Width), 1));
            var referenceScore = horizontalOverlap * 4 + leftScore * 2 + centerScore + gapScore + widthScore;

            if (referenceScore > bestReferenceScore)
                bestReferenceScore = referenceScore;
        }

        if (double.IsNegativeInfinity(bestReferenceScore))
            return false;

        score = bestReferenceScore;
        return true;
    }

    private static IEnumerable<LayoutRegion> OrderRegionsForReading(
        List<LayoutRegion> regions,
        LayoutMetrics metrics)
    {
        var pending = regions
            .OrderBy(x => x.Bounds.Top)
            .ThenBy(x => x.Bounds.Left)
            .ToList();

        while (pending.Count > 0)
        {
            var band = new List<LayoutRegion> { pending[0] };
            var bandBounds = pending[0].Bounds;
            pending.RemoveAt(0);

            for (var i = 0; i < pending.Count;)
            {
                var candidate = pending[i];
                if (IsSameReadingBand(bandBounds, candidate.Bounds, metrics))
                {
                    band.Add(candidate);
                    bandBounds = Bounds.Union(bandBounds, candidate.Bounds);
                    pending.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            foreach (var region in band.OrderBy(x => x.Bounds.Left).ThenBy(x => x.Bounds.Top))
                yield return region;
        }
    }

    private static bool IsSameReadingBand(Bounds bandBounds, Bounds candidateBounds, LayoutMetrics metrics)
    {
        if (VerticalOverlapRatio(bandBounds, candidateBounds) >= 0.25)
            return true;

        var topDelta = Math.Abs(bandBounds.Top - candidateBounds.Top);
        var hasVerticalContact = candidateBounds.Top <= bandBounds.Bottom + metrics.LineHeight * 1.5;
        return topDelta <= metrics.LineHeight * 2 && hasVerticalContact;
    }

    private static ParagraphCandidate? FindBestParagraph(
        LineSegment line,
        List<ParagraphGroup> paragraphs,
        LayoutMetrics metrics)
    {
        ParagraphCandidate? bestCandidate = null;
        var bestScore = double.NegativeInfinity;

        foreach (var paragraph in paragraphs)
        {
            var lastLine = paragraph.LastLine;
            if (line.Bounds.Top < lastLine.Bounds.Top)
                continue;

            if (!CanAppendToParagraph(lastLine, line, metrics, out var confidence))
                continue;

            var horizontalScore = HorizontalOverlapRatio(lastLine.Bounds, line.Bounds) * 3;
            var leftScore = 1 - Math.Min(1, Math.Abs(lastLine.Bounds.Left - line.Bounds.Left) / Math.Max(metrics.LineHeight, 1));
            var gapScore = 1 - Math.Min(1, VerticalGap(lastLine.Bounds, line.Bounds) / Math.Max(metrics.LineHeight, 1));
            var score = horizontalScore + leftScore + gapScore + confidence;

            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = new ParagraphCandidate(paragraph, confidence);
            }
        }

        return bestCandidate;
    }

    private static bool CanAppendToParagraph(
        LineSegment previous,
        LineSegment current,
        LayoutMetrics metrics,
        out double confidence)
    {
        confidence = 0;

        var verticalGap = VerticalGap(previous.Bounds, current.Bounds);
        if (verticalGap > metrics.LineHeight * 1.25)
            return false;

        if (IsListStart(current.Text))
            return false;

        if (IsListStart(previous.Text) && current.Bounds.Left > previous.Bounds.Left + metrics.LineHeight * 0.8)
        {
            confidence = 0.95;
            return true;
        }

        if (Math.Max(previous.Bounds.Height, current.Bounds.Height) >
            Math.Min(previous.Bounds.Height, current.Bounds.Height) * 1.45)
        {
            return false;
        }

        var horizontalOverlap = HorizontalOverlapRatio(previous.Bounds, current.Bounds);
        var leftDelta = Math.Abs(previous.Bounds.Left - current.Bounds.Left);
        var hasColumnAffinity = horizontalOverlap >= 0.45 || leftDelta <= metrics.LineHeight * 1.2;
        if (!hasColumnAffinity)
            return false;

        if (ShouldMergeHyphenated(previous.Text, current.Text))
        {
            confidence = 0.95;
            return true;
        }

        if (LooksLikeGridCell(previous, metrics) && LooksLikeGridCell(current, metrics))
            return false;

        if (LooksLikeStandaloneControl(previous) || LooksLikeStandaloneControl(current))
            return false;

        var indentDelta = current.Bounds.Left - previous.Bounds.Left;
        if (Math.Abs(indentDelta) > metrics.LineHeight * 2.5 && horizontalOverlap < 0.7)
            return false;

        var leftAffinity = 1 - Math.Min(1, leftDelta / Math.Max(metrics.LineHeight * 1.2, 1));
        var gapAffinity = 1 - Math.Min(1, verticalGap / Math.Max(metrics.LineHeight * 1.25, 1));
        var heightAffinity = Math.Min(previous.Bounds.Height, current.Bounds.Height) /
                             Math.Max(Math.Max(previous.Bounds.Height, current.Bounds.Height), 1);
        var horizontalAffinity = Math.Max(horizontalOverlap, leftAffinity);
        confidence = Math.Clamp(horizontalAffinity * 0.45 + gapAffinity * 0.35 + heightAffinity * 0.20, 0, 1);

        return confidence >= MinSmartMergeConfidence;
    }

    private static List<LineSegment> BuildLineSegments(List<LayoutItem> items)
    {
        var visualLines = new List<VisualLine>();

        foreach (var item in items.OrderBy(x => x.Bounds.CenterY).ThenBy(x => x.Bounds.Left))
        {
            var line = visualLines
                .Where(x => IsSameVisualLine(x.Bounds, item.Bounds))
                .OrderByDescending(x => VerticalOverlapRatio(x.Bounds, item.Bounds))
                .ThenBy(x => Math.Abs(x.Bounds.CenterY - item.Bounds.CenterY))
                .FirstOrDefault();

            if (line == null)
                visualLines.Add(new VisualLine(item));
            else
                line.Add(item);
        }

        return visualLines
            .SelectMany(SplitVisualLine)
            .OrderBy(x => x.Bounds.Top)
            .ThenBy(x => x.Bounds.Left)
            .ToList();
    }

    private static IEnumerable<LineSegment> SplitVisualLine(VisualLine line)
    {
        var sortedItems = line.Items.OrderBy(x => x.Bounds.Left).ToList();
        var groups = new List<List<LayoutItem>>();
        var group = new List<LayoutItem>();
        var lineHeight = Median(sortedItems.Select(x => x.Bounds.Height));

        foreach (var item in sortedItems)
        {
            if (group.Count > 0)
            {
                var previous = group[^1];
                var gap = item.Bounds.Left - previous.Bounds.Right;
                var maxInlineGap = Math.Max(
                    lineHeight * 1.25,
                    Math.Min(lineHeight * 2.0, Math.Min(previous.Bounds.Width, item.Bounds.Width) * 0.75));

                if (gap > maxInlineGap)
                {
                    groups.Add(group);
                    group = [];
                }
            }

            group.Add(item);
        }

        if (group.Count > 0)
            groups.Add(group);

        foreach (var itemGroup in groups)
            yield return LineSegment.From(itemGroup, groups.Count);
    }

    private static bool IsSameVisualLine(Bounds lineBounds, Bounds itemBounds)
    {
        if (VerticalOverlapRatio(lineBounds, itemBounds) >= 0.55)
            return true;

        var centerDelta = Math.Abs(lineBounds.CenterY - itemBounds.CenterY);
        return centerDelta <= Math.Min(lineBounds.Height, itemBounds.Height) * 0.45;
    }

    private static List<LayoutItem> CreateLayoutItems(IReadOnlyList<OcrContent> contents) =>
        contents
            .Select((content, index) => LayoutItem.TryCreate(content, index))
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

    private static List<BoxPoint> CreateBoxPoints(Bounds bounds) =>
    [
        new((float)bounds.Left, (float)bounds.Top),
        new((float)bounds.Right, (float)bounds.Top),
        new((float)bounds.Right, (float)bounds.Bottom),
        new((float)bounds.Left, (float)bounds.Bottom)
    ];

    private static List<BoxPoint> CreateUnionBoxPoints(IReadOnlyList<List<BoxPoint>> boxPointGroups)
    {
        var bounds = boxPointGroups
            .Where(points => points.Count > 0)
            .Select(Bounds.From)
            .ToList();

        return bounds.Count == 0
            ? []
            : CreateBoxPoints(Bounds.Union(bounds));
    }

    private static List<BoxPoint> CloneBoxPoints(IReadOnlyList<BoxPoint> boxPoints) =>
        boxPoints.Select(point => new BoxPoint(point.X, point.Y)).ToList();

    private static string JoinLineText(IReadOnlyList<LayoutItem> items)
    {
        var text = items[0].Text;
        for (var i = 1; i < items.Count; i++)
        {
            if (NeedsSpace(text, items[i].Text))
                text += " ";

            text += items[i].Text;
        }

        return text;
    }

    private static string JoinParagraphText(IReadOnlyList<LineSegment> lines)
    {
        var text = lines[0].Text;
        for (var i = 1; i < lines.Count; i++)
        {
            if (ShouldMergeHyphenated(text, lines[i].Text))
            {
                text = text[..^1] + lines[i].Text;
                continue;
            }

            if (NeedsSpace(text, lines[i].Text))
                text += " ";

            text += lines[i].Text;
        }

        return text;
    }

    private static string JoinProviderParagraphText(IReadOnlyList<OcrContent> lines)
    {
        var text = lines[0].Text;
        for (var i = 1; i < lines.Count; i++)
        {
            if (ShouldMergeHyphenated(text, lines[i].Text))
            {
                text = text[..^1] + lines[i].Text;
                continue;
            }

            if (NeedsSpace(text, lines[i].Text))
                text += " ";

            text += lines[i].Text;
        }

        return text;
    }

    private static bool NeedsSpace(string previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current))
            return false;

        var left = previous[^1];
        var right = current[0];
        if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right))
            return false;

        if (IsCjk(left) || IsCjk(right))
            return false;

        if (char.IsPunctuation(left) || char.IsPunctuation(right))
            return false;

        return true;
    }

    private static bool ShouldMergeHyphenated(string previous, string current)
    {
        if (previous.Length < 2 || string.IsNullOrWhiteSpace(current))
            return false;

        var beforeHyphen = previous[^2];
        var first = current[0];
        return previous[^1] == '-' &&
               IsLatinLetter(beforeHyphen) &&
               IsLatinLetter(first) &&
               char.IsLower(first);
    }

    private static bool LooksLikeStandaloneControl(LineSegment line)
    {
        if (IsListStart(line.Text))
            return false;

        var compactText = line.Text.Replace(" ", string.Empty);
        if (compactText.Length > 14)
            return false;

        if (line.Text.Any(char.IsWhiteSpace))
            return false;

        var widthRatio = line.Bounds.Width / Math.Max(line.Bounds.Height, 1);
        return widthRatio <= 6.2 && !HasSentenceEnding(line.Text);
    }

    private static bool LooksLikeGridCell(LineSegment line, LayoutMetrics metrics)
    {
        if (!line.HasRowPeers || IsListStart(line.Text) || HasSentenceEnding(line.Text))
            return false;

        var wordCount = line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount <= 3 &&
               line.Text.Length <= 32 &&
               line.Bounds.Width <= metrics.LineHeight * 7;
    }

    private static bool IsListStart(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] is '-' or '*' or '+' or '•' or '·' or '●' or '▪')
            return trimmed.Length == 1 || char.IsWhiteSpace(trimmed[1]);

        var i = 0;
        while (i < trimmed.Length && char.IsDigit(trimmed[i]))
            i++;

        return i > 0 &&
               i < trimmed.Length - 1 &&
               trimmed[i] is '.' or ')' or '、' &&
               char.IsWhiteSpace(trimmed[i + 1]);
    }

    private static bool HasSentenceEnding(string text) =>
        text.IndexOfAny(['.', '!', '?', ';', ':', '。', '！', '？', '；', '：']) >= 0;

    private static bool IsLatinLetter(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    private static bool IsCjk(char ch) =>
        (ch >= '\u3400' && ch <= '\u9fff') ||
        (ch >= '\uf900' && ch <= '\ufaff') ||
        (ch >= '\u3040' && ch <= '\u30ff') ||
        (ch >= '\uac00' && ch <= '\ud7af');

    private static double VerticalGap(Bounds top, Bounds bottom) =>
        Math.Max(0, bottom.Top - top.Bottom);

    private static double HorizontalOverlapRatio(Bounds first, Bounds second)
    {
        var overlap = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        return overlap / Math.Max(1, Math.Min(first.Width, second.Width));
    }

    private static double VerticalOverlapRatio(Bounds first, Bounds second)
    {
        var overlap = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return overlap / Math.Max(1, Math.Min(first.Height, second.Height));
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(x => x > 0).Order().ToList();
        if (ordered.Count == 0)
            return 0;

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private sealed class VisualLine
    {
        internal VisualLine(LayoutItem item)
        {
            Items.Add(item);
            Bounds = item.Bounds;
        }

        internal List<LayoutItem> Items { get; } = [];

        internal Bounds Bounds { get; private set; }

        internal void Add(LayoutItem item)
        {
            Items.Add(item);
            Bounds = Bounds.Union(Bounds, item.Bounds);
        }
    }

    private sealed class LayoutRegion
    {
        internal LayoutRegion(LineSegment line)
        {
            Lines.Add(line);
            Bounds = line.Bounds;
        }

        internal List<LineSegment> Lines { get; } = [];

        internal Bounds Bounds { get; private set; }

        internal void Add(LineSegment line)
        {
            Lines.Add(line);
            Bounds = Bounds.Union(Bounds, line.Bounds);
        }
    }

    private sealed record ParagraphCandidate(ParagraphGroup Paragraph, double Confidence);

    private sealed class ParagraphGroup
    {
        internal ParagraphGroup(LineSegment line) => Lines.Add(line);

        internal List<LineSegment> Lines { get; } = [];

        internal double Confidence { get; private set; } = 1;

        internal LineSegment LastLine => Lines[^1];

        internal Bounds Bounds => Bounds.Union(Lines.Select(x => x.Bounds));

        internal void Add(LineSegment line, double confidence)
        {
            Lines.Add(line);
            Confidence = Math.Min(Confidence, confidence);
        }

        internal OcrLayoutBlock ToLayoutBlock()
        {
            var bounds = Bounds.Union(Lines.Select(x => x.Bounds));
            return new OcrLayoutBlock
            {
                Text = JoinParagraphText(Lines),
                BoxPoints = CreateBoxPoints(bounds),
                LineBoxPoints = Lines.Select(line => CreateBoxPoints(line.Bounds)).ToList(),
                Source = OcrLayoutSource.Smart,
                Confidence = Confidence
            };
        }
    }

    private sealed class LineSegment
    {
        private LineSegment(List<LayoutItem> items, int visualLineSegmentCount)
        {
            Items = items;
            Bounds = Bounds.Union(items.Select(x => x.Bounds));
            Text = JoinLineText(items);
            VisualLineSegmentCount = visualLineSegmentCount;
        }

        internal List<LayoutItem> Items { get; }

        internal Bounds Bounds { get; }

        internal string Text { get; }

        internal int VisualLineSegmentCount { get; }

        internal bool HasRowPeers => VisualLineSegmentCount > 1;

        internal static LineSegment From(List<LayoutItem> items, int visualLineSegmentCount) =>
            new([.. items.OrderBy(x => x.Bounds.Left)], visualLineSegmentCount);
    }

    private sealed class LayoutItem
    {
        private LayoutItem(OcrContent content, Bounds bounds, int index)
        {
            Content = content;
            Bounds = bounds;
            Index = index;
            Text = content.Text.Trim();
        }

        internal OcrContent Content { get; }

        internal Bounds Bounds { get; }

        internal int Index { get; }

        internal string Text { get; }

        internal static LayoutItem? TryCreate(OcrContent content, int index)
        {
            if (string.IsNullOrWhiteSpace(content.Text) || content.BoxPoints.Count == 0)
                return null;

            var bounds = Bounds.From(content.BoxPoints);
            return bounds.Width <= 0 || bounds.Height <= 0
                ? null
                : new LayoutItem(content, bounds, index);
        }
    }

    private readonly record struct LayoutMetrics(double LineHeight)
    {
        internal static LayoutMetrics From(IReadOnlyList<LineSegment> lines) =>
            new(Math.Max(1, Median(lines.Select(x => x.Bounds.Height))));
    }

    private readonly record struct Bounds(double Left, double Top, double Right, double Bottom)
    {
        internal double Width => Right - Left;

        internal double Height => Bottom - Top;

        internal double CenterY => (Top + Bottom) / 2;

        internal double CenterX => (Left + Right) / 2;

        internal static Bounds From(IReadOnlyList<BoxPoint> points) =>
            new(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));

        internal static Bounds Union(IEnumerable<Bounds> bounds)
        {
            var list = bounds.ToList();
            return new Bounds(
                list.Min(x => x.Left),
                list.Min(x => x.Top),
                list.Max(x => x.Right),
                list.Max(x => x.Bottom));
        }

        internal static Bounds Union(Bounds first, Bounds second) =>
            new(
                Math.Min(first.Left, second.Left),
                Math.Min(first.Top, second.Top),
                Math.Max(first.Right, second.Right),
                Math.Max(first.Bottom, second.Bottom));
    }
}
