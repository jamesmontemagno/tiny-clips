using TinyClips.Core.Editing;
using Windows.Foundation;

namespace TinyClips.Core.Tests;

public sealed class SensitiveTextDetectorTests
{
    [Theory]
    [InlineData("alice+tag@example.co.uk", "Email address")]
    [InlineData("Contact: alice@example.com.", "Email address")]
    [InlineData("+1 (425) 555-0199", "Phone number")]
    [InlineData("(020) 7946 0958", "Phone number")]
    [InlineData("425-555-0199", "Phone number")]
    [InlineData("425.555.0199", "Phone number")]
    [InlineData("555-0199", "Phone number")]
    [InlineData("+123456789012345", "Phone number")]
    [InlineData("192.168.10.1", "IP address")]
    [InlineData("0.0.0.0", "IP address")]
    [InlineData("Server: 192.168.10.1.", "IP address")]
    [InlineData("192.168.10.1:8080", "IP address")]
    [InlineData("IP:192.168.10.1", "IP address")]
    [InlineData("2001:db8::1", "IP address")]
    [InlineData("[::1]", "IP address")]
    [InlineData("::ffff:192.0.2.128", "IP address")]
    [InlineData("2001:db8:0:1:2:3:4:5", "IP address")]
    public void DetectsPracticalPatterns(string text, string kind)
    {
        var match = Assert.Single(Detect(text));
        Assert.Equal(kind, match.Kind);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("name@example")]
    [InlineData("example.com")]
    [InlineData("1234567")]
    [InlineData("4255550199")]
    [InlineData("1234567890123456")]
    [InlineData("+1234567890123456")]
    [InlineData("2026-10-30")]
    [InlineData("30-10-2026")]
    [InlineData("1.2.3")]
    [InlineData("123.456.7890.12")]
    [InlineData("123.456.78-90")]
    [InlineData("123.456.78 90")]
    [InlineData("999.168.1.1")]
    [InlineData("192.168.01.1")]
    [InlineData("010.0.0.1")]
    [InlineData("00.0.0.0")]
    [InlineData("192.168.1.1.2")]
    [InlineData("v192.168.1.1")]
    [InlineData("12:34:56")]
    [InlineData("2001:db8:::1")]
    public void RejectsCommonFalsePositives(string text) => Assert.Empty(Detect(text));

    [Theory]
    [InlineData("o'brien@example.com")]
    [InlineData("a!#$%&'*+-/=?^_`{|}~z@example.com")]
    public void PreservesTheEntirePunctuationRichEmailLocalPart(string text)
    {
        var suggestion = Assert.Single(Detect(text));
        Assert.Equal("Email address", suggestion.Kind);
        Assert.Equal(text, suggestion.Text);
    }

    [Fact]
    public void IpAndEmailAreNotAlsoPhones()
    {
        var suggestions = Detect("alice+1234567@example.com 192.168.10.1 +1 425 555 0199");
        Assert.Equal(3, suggestions.Count);
        Assert.Single(suggestions, s => s.Kind == "Phone number");
    }

    [Fact]
    public void MapsWholeOverlappingWordsWithIndependentScaleAndOutwardPadding()
    {
        IReadOnlyList<SensitiveTextWord>[] lines =
        [
            [
                new("Contact", new Rect(0, 0, 30, 10)),
                new("+1", new Rect(40, 20, 10, 10)),
                new("425", new Rect(55, 20, 20, 10)),
                new("555-0199", new Rect(80, 20, 40, 10)),
                new("today", new Rect(130, 20, 30, 10)),
            ],
        ];
        var result = Assert.Single(SensitiveTextDetector.Detect(lines, 200, 100, 1000, 333,
            TestContext.Current.CancellationToken));
        Assert.Equal(new Rect(190, 59, 420, 48), result.Bounds);
        Assert.Equal("+1 425 555-0199", result.Text);
    }

    [Fact]
    public void JoinsSplitAddressPunctuationWithoutJoiningArbitraryWords()
    {
        IReadOnlyList<SensitiveTextWord>[] lines =
        [
            [
                new("alice", new Rect(10, 10, 30, 10)),
                new("@", new Rect(40, 10, 10, 10)),
                new("example", new Rect(50, 10, 40, 10)),
                new(".", new Rect(90, 10, 3, 10)),
                new("com", new Rect(93, 10, 20, 10)),
            ],
        ];
        var result = Assert.Single(SensitiveTextDetector.Detect(lines, 200, 100, 200, 100,
            TestContext.Current.CancellationToken));
        Assert.Equal("alice@example.com", result.Text);
        Assert.Equal(new Rect(8, 8, 107, 14), result.Bounds);
    }

    [Fact]
    public void CoversTheEntireMappedIpv6DottedTailAcrossSplitOcrWords()
    {
        IReadOnlyList<SensitiveTextWord>[] lines =
        [
            [
                new("::ffff:192", new Rect(10, 10, 50, 10)),
                new(".", new Rect(60, 10, 3, 10)),
                new("0", new Rect(63, 10, 7, 10)),
                new(".", new Rect(70, 10, 3, 10)),
                new("2", new Rect(73, 10, 7, 10)),
                new(".", new Rect(80, 10, 3, 10)),
                new("128", new Rect(83, 10, 20, 10)),
            ],
        ];
        var result = Assert.Single(SensitiveTextDetector.Detect(lines, 200, 100, 200, 100,
            TestContext.Current.CancellationToken));
        Assert.Equal("::ffff:192.0.2.128", result.Text);
        Assert.Equal(new Rect(8, 8, 97, 14), result.Bounds);
    }

    [Theory]
    [InlineData("::ffff:192.0.2")]
    [InlineData("::ffff:192.0.2.128.5")]
    public void RejectsIpv6PrefixesOfLongerDottedTails(string text) => Assert.Empty(Detect(text));

    [Fact]
    public void ClampsToImageEdgesAndNeverAppliesDisplayDpi()
    {
        var bounds = SensitiveTextDetector.MapBounds(new Rect(0, 0, 100, 50), 100, 50, 401, 199);
        Assert.Equal(new Rect(0, 0, 401, 199), bounds);
        // Zoom and per-monitor DPI are exclusively the canvas's image-host transform. They must
        // not enter native-pixel OCR geometry or the exported annotation bounds.
        Assert.Equal(bounds, SensitiveTextDetector.CreateRedaction(new("IP address", "::1", bounds)).Bounds);
    }

    [Theory]
    [InlineData(90, 136, 24, 28, 72)]
    [InlineData(-90, 36, 204, 28, 72)]
    [InlineData(180, 136, 204, 48, 42)]
    [InlineData(270, 36, 204, 28, 72)]
    [InlineData(360, 16, 54, 48, 42)]
    [InlineData(45, 67, 0, 52, 72)]
    public void RotatesDeskewedOcrBoundsAroundImageCenterBeforeScaling(
        double angle, double x, double y, double width, double height)
    {
        var bounds = SensitiveTextDetector.MapBounds(new Rect(10, 20, 20, 10),
            100, 100, 200, 300, textAngleDegrees: angle);
        Assert.Equal(new Rect(x, y, width, height), bounds);
    }

    [Fact]
    public void DetectPassesTheOcrTextAngleThroughToSuggestionGeometry()
    {
        var word = new SensitiveTextWord("alice@example.com", new Rect(10, 20, 20, 10));
        var suggestion = Assert.Single(SensitiveTextDetector.Detect(
            new[] { (IReadOnlyList<SensitiveTextWord>)new[] { word } },
            100, 100, 200, 300, TestContext.Current.CancellationToken, textAngleDegrees: 90));
        Assert.Equal(new Rect(136, 24, 28, 72), suggestion.Bounds);
    }

    [Fact]
    public void DetectionDoesNotMutateWordsAndAppliedAnnotationForcesSolidRedaction()
    {
        var bounds = new Rect(20, 10, 100, 12);
        var word = new SensitiveTextWord("alice@example.com", bounds);
        var suggestion = Assert.Single(SensitiveTextDetector.Detect(
            new[] { (IReadOnlyList<SensitiveTextWord>)new[] { word } }, 200, 100, 200, 100,
            TestContext.Current.CancellationToken));
        Assert.Equal(bounds, word.Bounds);
        var annotation = SensitiveTextDetector.CreateRedaction(suggestion);
        var snapshot = annotation.CaptureSnapshot();
        Assert.Equal(EditTool.Redact, snapshot.Tool);
        Assert.Equal(RedactionStyle.Solid, snapshot.RedactStyle);
        Assert.Equal(suggestion.Bounds, snapshot.Bounds);
        Assert.Equal(0, snapshot.Rotation);
    }

    [Fact]
    public void CancellationPublishesNoPartialSuggestions()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SensitiveTextDetector.Detect(
            new[] { (IReadOnlyList<SensitiveTextWord>)new[] { new SensitiveTextWord("a@example.com", new Rect(0, 0, 20, 10)) } },
            100, 100, 100, 100, cancellation.Token));
    }

    private static IReadOnlyList<SensitiveTextSuggestion> Detect(string text) =>
        SensitiveTextDetector.Detect(
            new[] { (IReadOnlyList<SensitiveTextWord>)new[] { new SensitiveTextWord(text, new Rect(10, 10, 180, 20)) } },
            200, 100, 200, 100, TestContext.Current.CancellationToken);
}
