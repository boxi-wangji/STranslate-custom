using STranslate.Core;
using STranslate.Plugin;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Tests;

public class OcrLayoutAnalyzerTests
{
    [Fact]
    public void SmartMergesParagraphLines()
    {
        var result = AnalyzeSmart(
            Box("This is the first line", 0, 0, 180, 20),
            Box("continued on the next line", 0, 24, 210, 20));

        Assert.Single(result);
        Assert.Equal("This is the first line continued on the next line", result[0].Text);
    }

    [Fact]
    public void SmartKeepsColumnsSeparate()
    {
        var result = AnalyzeSmart(
            Box("Left column starts here", 0, 0, 180, 20),
            Box("Right column starts here", 300, 0, 190, 20),
            Box("and continues below", 0, 24, 160, 20),
            Box("with its own text", 300, 24, 150, 20));

        Assert.Equal(2, result.Count);
        Assert.Equal("Left column starts here and continues below", result[0].Text);
        Assert.Equal("Right column starts here with its own text", result[1].Text);
    }

    [Fact]
    public void SmartCompletesLeftColumnBeforeRightColumn()
    {
        var result = AnalyzeSmart(
            Box("Left first paragraph", 0, 0, 170, 20),
            Box("continues here", 0, 24, 135, 20),
            Box("Right column starts", 300, 12, 170, 20),
            Box("continues separately", 300, 36, 180, 20),
            Box("Left second paragraph", 0, 70, 185, 20),
            Box("continues too", 0, 94, 120, 20));

        Assert.Equal(3, result.Count);
        Assert.Equal("Left first paragraph continues here", result[0].Text);
        Assert.Equal("Left second paragraph continues too", result[1].Text);
        Assert.Equal("Right column starts continues separately", result[2].Text);
    }

    [Fact]
    public void SmartDoesNotMergeUiLabelsOnSameRow()
    {
        var result = AnalyzeSmart(
            Box("File", 0, 0, 32, 20),
            Box("Edit", 66, 0, 32, 20),
            Box("View", 132, 0, 36, 20));

        Assert.Equal(["File", "Edit", "View"], result.Select(x => x.Text));
    }

    [Fact]
    public void SmartDoesNotMergeSettingsCardControls()
    {
        var result = AnalyzeSmart(
            Box("General", 0, 0, 64, 20),
            Box("Enable", 220, 0, 70, 20),
            Box("Theme", 0, 32, 56, 20),
            Box("Dark", 220, 32, 46, 20));

        Assert.Equal(["General", "Theme", "Enable", "Dark"], result.Select(x => x.Text));
    }

    [Fact]
    public void SmartDoesNotMergeTableCells()
    {
        var result = AnalyzeSmart(
            Box("First name", 0, 0, 100, 20),
            Box("Order status", 150, 0, 110, 20),
            Box("Alice Smith", 0, 28, 105, 20),
            Box("Active now", 150, 28, 95, 20),
            Box("Bob Stone", 0, 56, 90, 20),
            Box("Paused now", 150, 56, 96, 20));

        Assert.Equal(
            ["First name", "Alice Smith", "Bob Stone", "Order status", "Active now", "Paused now"],
            result.Select(x => x.Text));
    }

    [Fact]
    public void SmartKeepsTitleAndBodySeparate()
    {
        var result = AnalyzeSmart(
            Box("Account Settings", 0, 0, 220, 32),
            Box("Manage your profile details", 0, 48, 230, 20));

        Assert.Equal(2, result.Count);
        Assert.Equal("Account Settings", result[0].Text);
        Assert.Equal("Manage your profile details", result[1].Text);
    }

    [Fact]
    public void SmartKeepsListItemsAndMergesContinuation()
    {
        var result = AnalyzeSmart(
            Box("- First item", 0, 0, 90, 20),
            Box("continued detail", 24, 24, 130, 20),
            Box("- Second item", 0, 48, 105, 20));

        Assert.Equal(2, result.Count);
        Assert.Equal("- First item continued detail", result[0].Text);
        Assert.Equal("- Second item", result[1].Text);
    }

    [Fact]
    public void SmartAddsSpacesForLatinWordLevelOcr()
    {
        var result = AnalyzeSmart(
            Box("Hello", 0, 0, 42, 20),
            Box("world", 50, 0, 45, 20));

        Assert.Single(result);
        Assert.Equal("Hello world", result[0].Text);
    }

    [Fact]
    public void SmartAvoidsSpacesForCjkWordLevelOcr()
    {
        var result = AnalyzeSmart(
            Box("你", 0, 0, 20, 20),
            Box("好", 22, 0, 20, 20));

        Assert.Single(result);
        Assert.Equal("你好", result[0].Text);
    }

    [Fact]
    public void SmartMergesHyphenatedEnglishContinuation()
    {
        var result = AnalyzeSmart(
            Box("trans-", 0, 0, 54, 20),
            Box("lation", 0, 24, 56, 20));

        Assert.Single(result);
        Assert.Equal("translation", result[0].Text);
    }

    [Fact]
    public void ApplyLeavesContentsWithoutBoxPointsUnchanged()
    {
        var ocrResult = new OcrResult
        {
            OcrContents =
            [
                new() { Text = "plain text" },
                new() { Text = "second line" }
            ]
        };

        OcrLayoutAnalyzer.Apply(ocrResult, LayoutAnalysisMode.Smart);

        Assert.Equal(["plain text", "second line"], ocrResult.OcrContents.Select(x => x.Text));
    }

    [Fact]
    public void NoMergePreservesOriginalBlocks()
    {
        var result = OcrLayoutAnalyzer.Analyze(
            [
                Box("One", 0, 0, 40, 20),
                Box("Two", 0, 24, 40, 20)
            ],
            LayoutAnalysisMode.NoMerge);

        Assert.Equal(["One", "Two"], result.Select(x => x.Text));
    }

    [Fact]
    public void SettingsReadsUnknownLayoutAnalysisModeAsAuto()
    {
        var settings = JsonSerializer.Deserialize<Settings>(
            """{"LayoutAnalysisMode":"standardDocument"}""")!;

        Assert.Equal(LayoutAnalysisMode.Auto, settings.LayoutAnalysisMode);
    }

    [Fact]
    public void SettingsReadsSmartLayoutAnalysisModeAsSmart()
    {
        var settings = JsonSerializer.Deserialize<Settings>(
            """{"LayoutAnalysisMode":"smart"}""")!;

        Assert.Equal(LayoutAnalysisMode.Smart, settings.LayoutAnalysisMode);
    }

    [Fact]
    public void SettingsKeepsNoMergeLayoutAnalysisMode()
    {
        var settings = new Settings { LayoutAnalysisMode = LayoutAnalysisMode.NoMerge };

        settings.NormalizeLayoutAnalysisMode();

        Assert.Equal(LayoutAnalysisMode.NoMerge, settings.LayoutAnalysisMode);
    }

    [Fact]
    public void AutoUsesProviderLayoutWhenAvailable()
    {
        var result = new OcrResult
        {
            OcrContents = [Box("Flat fallback", 0, 100, 100, 20)],
            Regions =
            [
                new()
                {
                    Paragraphs =
                    [
                        new()
                        {
                            Lines =
                            [
                                Box("Provider first line", 0, 0, 150, 20),
                                Box("continues here", 0, 24, 120, 20)
                            ]
                        }
                    ]
                }
            ]
        };

        var blocks = OcrLayoutAnalyzer.AnalyzeBlocks(result, LayoutAnalysisMode.Auto);

        Assert.Single(blocks);
        Assert.Equal(OcrLayoutSource.Provider, blocks[0].Source);
        Assert.Equal(1, blocks[0].Confidence);
        Assert.Equal("Provider first line continues here", blocks[0].Text);
        Assert.Equal(2, blocks[0].LineBoxPoints.Count);
    }

    [Fact]
    public void AutoFallsBackToSmartWithoutProviderLayout()
    {
        var result = new OcrResult
        {
            OcrContents =
            [
                Box("Smart first line", 0, 0, 140, 20),
                Box("continues here", 0, 24, 120, 20)
            ]
        };

        var blocks = OcrLayoutAnalyzer.AnalyzeBlocks(result, LayoutAnalysisMode.Auto);

        Assert.Single(blocks);
        Assert.Equal(OcrLayoutSource.Smart, blocks[0].Source);
        Assert.Equal("Smart first line continues here", blocks[0].Text);
    }

    [Fact]
    public void ProviderWithoutStructuredLayoutFallsBackToNoMerge()
    {
        var result = new OcrResult
        {
            OcrContents =
            [
                Box("One", 0, 0, 40, 20),
                Box("Two", 0, 24, 40, 20)
            ]
        };

        var blocks = OcrLayoutAnalyzer.AnalyzeBlocks(result, LayoutAnalysisMode.Provider);

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(OcrLayoutSource.NoMerge, block.Source));
        Assert.Equal(["One", "Two"], blocks.Select(x => x.Text));
    }

    [Fact]
    public void ProviderFlatAnalyzeFallsBackToNoMerge()
    {
        var result = OcrLayoutAnalyzer.Analyze(
            [
                Box("One", 0, 0, 40, 20),
                Box("Two", 0, 24, 40, 20)
            ],
            LayoutAnalysisMode.Provider);

        Assert.Equal(["One", "Two"], result.Select(x => x.Text));
    }

    [Fact]
    public void OcrResultTextFlattensStructuredLayoutInReadingOrder()
    {
        var result = new OcrResult
        {
            Regions =
            [
                new()
                {
                    Paragraphs =
                    [
                        new()
                        {
                            Lines =
                            [
                                new() { Text = "First line" },
                                new() { Text = "continues" }
                            ]
                        },
                        new()
                        {
                            Lines =
                            [
                                new() { Text = "Second paragraph" }
                            ]
                        }
                    ]
                }
            ]
        };

        Assert.Equal($"First line continues{Environment.NewLine}Second paragraph", result.Text);
    }

    [Fact]
    public void NormalizeCoordinatesConvertsNormalizedBoxesToPixels()
    {
        var result = new OcrResult
        {
            OcrContents =
            [
                new()
                {
                    Text = "Flat",
                    CoordinateUnit = OcrCoordinateUnit.Normalized,
                    BoxPoints =
                    [
                        new(0.1f, 0.2f),
                        new(0.3f, 0.2f),
                        new(0.3f, 0.4f),
                        new(0.1f, 0.4f)
                    ]
                }
            ],
            Regions =
            [
                new()
                {
                    CoordinateUnit = OcrCoordinateUnit.Normalized,
                    BoxPoints = [new(0.05f, 0.1f), new(0.35f, 0.1f)],
                    Paragraphs =
                    [
                        new()
                        {
                            CoordinateUnit = OcrCoordinateUnit.Normalized,
                            BoxPoints = [new(0.1f, 0.2f), new(0.3f, 0.4f)],
                            Lines =
                            [
                                new()
                                {
                                    Text = "Line",
                                    CoordinateUnit = OcrCoordinateUnit.Normalized,
                                    BoxPoints = [new(0.2f, 0.25f), new(0.4f, 0.5f)]
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        Utilities.NormalizeOcrCoordinates(result, 200, 100);

        Assert.Equal(OcrCoordinateUnit.Pixel, result.OcrContents[0].CoordinateUnit);
        Assert.Equal(20, result.OcrContents[0].BoxPoints[0].X, precision: 3);
        Assert.Equal(20, result.OcrContents[0].BoxPoints[0].Y, precision: 3);
        Assert.Equal(60, result.OcrContents[0].BoxPoints[1].X, precision: 3);
        Assert.Equal(10, result.Regions[0].BoxPoints[0].Y, precision: 3);
        Assert.Equal(60, result.Regions[0].Paragraphs[0].BoxPoints[1].X, precision: 3);
        Assert.Equal(50, result.Regions[0].Paragraphs[0].Lines[0].BoxPoints[1].Y, precision: 3);
    }

    [Fact]
    public void NormalizeCoordinatesClearsNormalizedBoxesWithoutImageSize()
    {
        var result = new OcrResult
        {
            OcrContents =
            [
                new()
                {
                    Text = "Flat",
                    CoordinateUnit = OcrCoordinateUnit.Normalized,
                    BoxPoints = [new(0.1f, 0.2f)]
                }
            ]
        };

        Utilities.NormalizeOcrCoordinates(result, 0, 0);

        Assert.Empty(result.OcrContents[0].BoxPoints);
        Assert.Equal(OcrCoordinateUnit.Pixel, result.OcrContents[0].CoordinateUnit);
    }

    [Fact]
    public void NormalizeCoordinatesProjectsStructuredLayoutToFlatContents()
    {
        var result = new OcrResult
        {
            Regions =
            [
                new()
                {
                    Paragraphs =
                    [
                        new()
                        {
                            Lines =
                            [
                                Box("Projected first", 0, 0, 120, 20),
                                Box("line", 0, 24, 40, 20)
                            ]
                        }
                    ]
                }
            ]
        };

        Utilities.NormalizeOcrCoordinates(result, 200, 100);

        Assert.Single(result.OcrContents);
        Assert.Equal("Projected first line", result.OcrContents[0].Text);
        Assert.Equal(4, result.OcrContents[0].BoxPoints.Count);
    }

    [Fact]
    public void OcrCapabilityProviderControlsImageTranslationEligibility()
    {
        IOcrPlugin oldPlugin = new PlainOcrPlugin();
        IOcrPlugin boundingBoxOnly = new CapabilityOcrPlugin(OcrCapabilities.BoundingBox);
        IOcrPlugin imageTranslationOnly = new CapabilityOcrPlugin(OcrCapabilities.ImageTranslation);
        IOcrPlugin eligible = new CapabilityOcrPlugin(OcrCapabilities.BoundingBox | OcrCapabilities.ImageTranslation);

        Assert.False(oldPlugin.SupportsImageTranslation());
        Assert.False(boundingBoxOnly.SupportsImageTranslation());
        Assert.False(imageTranslationOnly.SupportsImageTranslation());
        Assert.True(eligible.SupportsImageTranslation());
    }

    private static List<OcrContent> AnalyzeSmart(params OcrContent[] contents) =>
        OcrLayoutAnalyzer.Analyze(contents, LayoutAnalysisMode.Smart);

    private static OcrContent Box(string text, float left, float top, float width, float height) =>
        new()
        {
            Text = text,
            BoxPoints =
            [
                new(left, top),
                new(left + width, top),
                new(left + width, top + height),
                new(left, top + height)
            ]
        };

    private class PlainOcrPlugin : IOcrPlugin
    {
        public IEnumerable<LangEnum> SupportedLanguages => [LangEnum.Auto];

        public void Init(IPluginContext context)
        {
        }

        public Control GetSettingUI() => new();

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrResult());

        public void Dispose()
        {
        }
    }

    private sealed class CapabilityOcrPlugin(OcrCapabilities capabilities) : PlainOcrPlugin, IOcrCapabilityProvider
    {
        public OcrCapabilities Capabilities { get; } = capabilities;
    }
}
