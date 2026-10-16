using System.Globalization;
using System.Text.Json;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

/// <summary>
/// Runs the pinned <c>ucs-detect</c> detector against the headless terminal and asserts the
/// JSON report. There is one test per detector function in <c>ucs_detect/terminal.py</c> and
/// one per Unicode category measured by <c>ucs_detect/measure.py</c>, so a regression names
/// the exact function that broke.
/// </summary>
public sealed class UcsDetectIntegrationTests : IClassFixture<UcsDetectTerminalFixture>
{
    private const string Escape = "\u001b";
    private const string StringTerminator = Escape + "\\";

    private readonly UcsDetectTerminalFixture _terminal;

    public UcsDetectIntegrationTests(UcsDetectTerminalFixture terminal)
    {
        _terminal = terminal;
    }

    public static TheoryData<string> MeasureTestSupportCategories { get; } = new()
    {
        "unicode_wide_results",
        "sri_results",
        "sfz_results",
        "ri_results",
        "emoji_zwj_results",
        "emoji_vs16_results",
        "emoji_vs15_results",
        "narrow_results",
    };

    [Fact]
    public void UcsDetect_ReportsSuccessfulRunAndPinnedEnvironment()
    {
        JsonElement root = _terminal.Report.RootElement;
        Assert.Equal(UcsDetectReportSchema.UcsDetectVersion, root.GetProperty("ucs_detect_version").GetString());
        Assert.Equal(UcsDetectReportSchema.UnicodeVersion, root.GetProperty("unicode_version").GetString());
        Assert.Equal(UcsDetectReportSchema.WcwidthVersion, root.GetProperty("wcwidth_version").GetString());
        Assert.Equal("XTerm.NET", root.GetProperty("software_name").GetString());
        Assert.Equal("3.0.0", root.GetProperty("software_version").GetString());
        Assert.Equal("UTF-8", root.GetProperty("preferred_encoding").GetString());
        Assert.Equal("Linux", root.GetProperty("system").GetString());
        Assert.True(root.GetProperty("ambiguous_width").ValueKind == JsonValueKind.Number);
        Assert.True(root.GetProperty("session_arguments").GetProperty("all").GetBoolean());
        Assert.True(File.Exists(_terminal.ReportPath), $"JSON report was not retained: {_terminal.ReportPath}");
        Assert.True(File.Exists(_terminal.DependencyVersionsPath), $"Dependency versions were not retained: {_terminal.DependencyVersionsPath}");
        Assert.True(File.Exists(_terminal.TranscriptPath), $"PTY transcript was not retained: {_terminal.TranscriptPath}");
    }

    // --- ucs_detect/terminal.py: terminal fingerprinting, one test per detector function ---

    [Fact]
    public void UcsDetect_MakeTerminal_ReportsTerminalTypeAndColorDepth()
    {
        Assert.Equal("xterm", Feature("ttype").GetString());
        JsonElement colors = Feature("number_of_colors");
        Assert.Equal(JsonValueKind.Number, colors.ValueKind);
        Assert.True(colors.GetInt64() >= 16_777_216, $"Expected truecolor depth, got {colors}.");
    }

    [Fact]
    public void UcsDetect_GetTtySize_ReportsRequestedColumnsAndRows()
    {
        AssertInt("width", 131);
        AssertInt("height", 28);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineColors_ReportsForegroundAndBackground()
    {
        AssertHexColorPair("foreground_color_rgb", "foreground_color_hex", [65535, 65535, 65535]);
        AssertHexColorPair("background_color_rgb", "background_color_hex", [0, 0, 0]);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineDaAndSixel_ReportsDeviceAttributesAndSixel()
    {
        JsonElement attributes = ObjectFeature("device_attributes");
        AssertKeySet(attributes, UcsDetectReportSchema.DeviceAttributeKeys, "device_attributes");
        AssertInt(attributes, "service_class", 65);
        Assert.Equal(
            UcsDetectReportSchema.DeviceAttributeExtensions,
            ReadIntArray(attributes.GetProperty("extensions")));
        string raw = attributes.GetProperty("raw").GetString() ?? string.Empty;
        Assert.StartsWith($"{Escape}[?65;", raw, StringComparison.Ordinal);
        Assert.EndsWith("c", raw, StringComparison.Ordinal);

        AssertBooleanFeature("sixel", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineSoftware_ReportsXtvVersionIdentity()
    {
        Assert.Equal("XTVERSION", Feature("software_method").GetString());
        Assert.Contains("XTerm.NET", Feature("software_name").GetString(), StringComparison.Ordinal);
        Assert.Equal("3.0.0", Feature("software_version").GetString());
        Assert.Equal("XTerm.NET(3.0.0)", Feature("xtversion_raw").GetString());
        AssertBooleanFeature("xtversion-bad-screenleak", false);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineCellSize_ReportsCellPixelDimensions()
    {
        AssertInt("cell_height", 52);
        AssertInt("cell_width", 21);
    }

    [Fact]
    public void UcsDetect_MaybeDeterminePixelSize_ReportsPixelDimensions()
    {
        AssertInt("pixels_height", 1456);
        AssertInt("pixels_width", 2751);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineScreenRatio_ReportsRatioAndName()
    {
        Assert.Equal("16:9", Feature("screen_ratio").GetString());
        Assert.Equal("HD", Feature("screen_ratio_name").GetString());
    }

    [Fact]
    public void UcsDetect_MaybeDetermineOsc52Clipboard_ReportsDetectionMethods()
    {
        AssertBooleanFeature("osc52_clipboard", true);
        JsonElement detection = ObjectFeature("osc52_detection");
        AssertKeySet(detection, UcsDetectReportSchema.Osc52DetectionKeys, "osc52_detection");
        AssertBoolean(detection, "da1_extension_52", false);
        AssertBoolean(detection, "xtgettcap_ms", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineDecModes_ReportsEveryProbedMode()
    {
        JsonElement modes = ObjectFeature("modes");
        List<int> supported = [];
        int count = 0;
        foreach (JsonProperty mode in modes.EnumerateObject())
        {
            count++;
            Assert.True(
                int.TryParse(mode.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int number),
                $"DEC private mode key '{mode.Name}' is not numeric.");
            AssertKeySet(mode.Value, UcsDetectReportSchema.DecPrivateModeDescriptorKeys, $"modes.{mode.Name}");
            Assert.Equal(JsonValueKind.Number, mode.Value.GetProperty("value").ValueKind);
            Assert.Equal(JsonValueKind.String, mode.Value.GetProperty("value_description").ValueKind);
            Assert.Equal(JsonValueKind.String, mode.Value.GetProperty("mode_name").ValueKind);
            Assert.Equal(JsonValueKind.String, mode.Value.GetProperty("mode_description").ValueKind);
            Assert.True(
                mode.Value.GetProperty("supported").ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"modes.{mode.Name}.supported is not a boolean.");
            if (mode.Value.GetProperty("supported").GetBoolean())
            {
                supported.Add(number);
            }
        }

        Assert.Equal(UcsDetectReportSchema.ProbedDecPrivateModeCount, count);
        Assert.Equal<int>(
            UcsDetectReportSchema.SupportedDecPrivateModes,
            supported.Order());

        JsonElement focus = modes.GetProperty("1004");
        Assert.Equal("FOCUS_IN_OUT_EVENTS", focus.GetProperty("mode_name").GetString());
        Assert.True(focus.GetProperty("changeable").GetBoolean());
        Assert.False(focus.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void UcsDetect_MaybeDetermineKittyKeyboard_ReportsSupportedFlags()
    {
        JsonElement keyboard = ObjectFeature("kitty_keyboard");
        AssertKeySet(keyboard, UcsDetectReportSchema.KittyKeyboardKeys, "kitty_keyboard");
        AssertInt(keyboard, "supported_flags", 31);
        AssertBoolean(keyboard, "disambiguate", true);
        AssertBoolean(keyboard, "report_all_keys", true);
        AssertBoolean(keyboard, "report_alternates", true);
        AssertBoolean(keyboard, "report_events", true);
        AssertBoolean(keyboard, "report_text", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineXtGetTcap_ReportsCapabilities()
    {
        JsonElement tcap = ObjectFeature("xtgettcap");
        AssertKeySet(tcap, UcsDetectReportSchema.XtGetTcapKeys, "xtgettcap");
        AssertBoolean(tcap, "supported", true);

        JsonElement capabilities = tcap.GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Object, capabilities.ValueKind);
        foreach ((string name, string value) in UcsDetectReportSchema.RequiredXtGetTcapCapabilities)
        {
            Assert.True(
                capabilities.TryGetProperty(name, out JsonElement actual),
                $"XTGETTCAP did not return '{name}'.");
            Assert.Equal(value, actual.GetString());
        }

        AssertBooleanFeature("xtgettcap-bad-screenleak", false);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineKittyGraphics_ReportsSupport()
    {
        AssertBooleanFeature("kitty_graphics", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineIterm2Features_ReportsEachFeature()
    {
        JsonElement container = ObjectFeature("iterm2_features");
        AssertKeySet(container, UcsDetectReportSchema.Iterm2FeatureContainerKeys, "iterm2_features");
        AssertBoolean(container, "supported", true);

        JsonElement features = container.GetProperty("features");
        AssertKeySet(features, UcsDetectReportSchema.Iterm2FeatureKeys, "iterm2_features.features");
        foreach (string feature in UcsDetectReportSchema.Iterm2FeatureKeys)
        {
            if (feature == "truecolor")
            {
                AssertInt(features, feature, 24);
            }
            else
            {
                AssertBoolean(features, feature, UcsDetectReportSchema.Iterm2FeatureValues[feature]);
            }
        }
    }

    [Fact]
    public void UcsDetect_MaybeDetermineIterm2Graphics_ReportsSupport()
    {
        AssertBooleanFeature("iterm2_graphics", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineTextSizing_ReportsWidthAndScale()
    {
        JsonElement sizing = ObjectFeature("text_sizing");
        AssertKeySet(sizing, UcsDetectReportSchema.TextSizingKeys, "text_sizing");
        AssertBoolean(sizing, "width", true);
        AssertBoolean(sizing, "scale", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineTofu_ReportsFontCoverageProtocol()
    {
        JsonElement tofu = ObjectFeature("tofu");
        AssertKeySet(tofu, UcsDetectReportSchema.TofuKeys, "tofu");
        AssertBoolean(tofu, "supported", true);
        Assert.Equal("mintty", tofu.GetProperty("protocol").GetString());
    }

    [Fact]
    public void UcsDetect_MaybeDetermineTabStopWidth_ReportsWidth()
    {
        AssertInt("tab_stop_width", 8);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineKittyNotifications_ReportsSupport()
    {
        JsonElement notifications = ObjectFeature("kitty_notifications");
        AssertKeySet(notifications, UcsDetectReportSchema.KittyNotificationKeys, "kitty_notifications");
        AssertBoolean(notifications, "supported", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineKittyClipboard_ReportsSupport()
    {
        AssertBooleanFeature("kitty_clipboard_protocol", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineKittyPointerShapes_ReportsSupport()
    {
        JsonElement shapes = ObjectFeature("kitty_pointer_shapes");
        AssertKeySet(shapes, UcsDetectReportSchema.KittyPointerShapeKeys, "kitty_pointer_shapes");
        AssertBoolean(shapes, "supported", true);
        Assert.Equal("0", shapes.GetProperty("current").GetString());
    }

    [Fact]
    public void UcsDetect_MaybeDetermineStyledUnderlines_ReportsStyledAndColored()
    {
        AssertBooleanFeature("styled_underlines", true);
        AssertBooleanFeature("colored_underlines", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineColorScheme_ReportsPreference()
    {
        AssertBooleanFeature("color_scheme", false);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineKittyQuery_ReportsSupport()
    {
        AssertBooleanFeature("kitty_query", false);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineDecrqss_ReportsSettings()
    {
        AssertBooleanFeature("decrqss", true);
        JsonElement settings = ObjectFeature("decrqss_settings");
        foreach (JsonProperty setting in settings.EnumerateObject())
        {
            Assert.True(
                UcsDetectReportSchema.DecrqssSettingKeys.Contains(setting.Name),
                $"DECRQSS reported an unknown setting '{setting.Name}'.");
            Assert.Equal(JsonValueKind.String, setting.Value.ValueKind);
        }

        foreach (string required in UcsDetectReportSchema.RequiredDecrqssSettingKeys)
        {
            Assert.True(
                settings.TryGetProperty(required, out _),
                $"DECRQSS did not report '{required}'. Report: {_terminal.ReportPath}");
        }

        Assert.Equal("1;28", settings.GetProperty("decstbm").GetString());
        Assert.Equal("1;131", settings.GetProperty("decslrm").GetString());
        Assert.Equal("28", settings.GetProperty("decslpp").GetString());
        Assert.Equal("28", settings.GetProperty("decsnls").GetString());
        Assert.Equal("2", settings.GetProperty("decscusr").GetString());
    }

    [Fact]
    public void UcsDetect_MaybeDetermineDecrqssTruecolor_ReportsProbe()
    {
        AssertBooleanFeature("decrqss_truecolor_probe", true);
    }

    [Fact]
    public void UcsDetect_MaybeDetermineDecrqcra_ReportsSupport()
    {
        AssertBooleanFeature("decrqcra", true);
    }

    // --- ucs_detect/measure.py: Unicode measurement, one test per measured category ---

    [Theory]
    [MemberData(nameof(MeasureTestSupportCategories))]
    public void UcsDetect_MeasureTestSupport_ReportsFullConformance(string category)
    {
        AssertUnicodeCategoryAtFullConformance(category);
    }

    [Fact]
    public void UcsDetect_MeasureTestTofuSupport_ReportsFontCoverage()
    {
        JsonElement testResults = _terminal.Report.RootElement.GetProperty("test_results");
        Assert.True(
            testResults.TryGetProperty("tofu_results", out JsonElement results) &&
            results.ValueKind == JsonValueKind.Object &&
            results.EnumerateObject().Any(),
            $"ucs-detect did not report font coverage results. Report: {_terminal.ReportPath}");

        foreach (JsonProperty result in results.EnumerateObject())
        {
            Assert.True(
                result.Value.TryGetProperty("n_total", out JsonElement total) &&
                total.ValueKind == JsonValueKind.Number && total.GetInt64() > 0,
                $"{result.Name} font coverage tested zero codepoints. Report: {_terminal.ReportPath}");
            Assert.True(
                result.Value.TryGetProperty("pct_success", out JsonElement coverage) &&
                coverage.ValueKind == JsonValueKind.Number && coverage.GetDouble() is >= 0 and <= 100,
                $"{result.Name} font coverage result was not a valid percentage. Report: {_terminal.ReportPath}");
        }
    }

    [Fact]
    public void UcsDetect_MeasureTestLanguageSupport_ReportsFullConformance()
    {
        AssertUnicodeCategoryAtFullConformance("language_results");
    }

    // --- Wire probes: confirm the questions actually reach the headless terminal ---

    [Fact]
    public void UcsDetect_SendsTheDeviceStatusReportProbeToTheHeadlessTerminal()
    {
        Assert.Contains($"{Escape}[6n", _terminal.TerminalOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UcsDetect_SendsTheStatusStringProbeToTheHeadlessTerminal()
    {
        Assert.Contains($"{Escape}P$qm{StringTerminator}", _terminal.TerminalOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UcsDetect_SendsTheIterm2FeaturesProbeToTheHeadlessTerminal()
    {
        Assert.Contains($"{Escape}]7771;?;", _terminal.TerminalOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UcsDetect_ReceivesTheIterm2FeaturesReplyFromTheHeadlessTerminal()
    {
        Assert.Contains($"{Escape}]7771;!;", _terminal.TerminalInput, StringComparison.Ordinal);
    }

    // --- Report completeness: any new detector feature forces a coverage decision ---

    [Fact]
    public void UcsDetect_Report_ExposesExactlyTheKnownTopLevelKeys()
    {
        AssertKeySet(_terminal.Report.RootElement, UcsDetectReportSchema.TopLevelKeys, "report");
    }

    [Fact]
    public void UcsDetect_Report_ExposesExactlyTheKnownTerminalResultKeys()
    {
        AssertKeySet(
            _terminal.Report.RootElement.GetProperty("terminal_results"),
            UcsDetectReportSchema.TerminalResultKeys,
            "terminal_results");
    }

    [Fact]
    public void UcsDetect_Report_ExposesExactlyTheKnownUnicodeCategories()
    {
        AssertKeySet(
            _terminal.Report.RootElement.GetProperty("test_results"),
            UcsDetectReportSchema.UnicodeResultCategories,
            "test_results");
    }

    private JsonElement Feature(string path)
    {
        JsonElement current = _terminal.Report.RootElement.GetProperty("terminal_results");
        foreach (string segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out JsonElement next))
            {
                Assert.Fail($"ucs-detect omitted terminal feature '{path}'. Report: {_terminal.ReportPath}");
                return default;
            }

            current = next;
        }

        return current;
    }

    private JsonElement ObjectFeature(string name)
    {
        JsonElement value = Feature(name);
        Assert.True(
            value.ValueKind == JsonValueKind.Object,
            $"terminal_results.{name} was {value.ValueKind}, expected an object. Report: {_terminal.ReportPath}");
        return value;
    }

    private void AssertBooleanFeature(string name, bool expected)
    {
        JsonElement value = Feature(name);
        Assert.Equal(expected ? JsonValueKind.True : JsonValueKind.False, value.ValueKind);
    }

    private void AssertInt(string name, long expected)
    {
        JsonElement value = Feature(name);
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.Equal(expected, value.GetInt64());
    }

    private static void AssertInt(JsonElement parent, string name, long expected)
    {
        JsonElement value = parent.GetProperty(name);
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.Equal(expected, value.GetInt64());
    }

    private static void AssertBoolean(JsonElement parent, string name, bool expected)
    {
        JsonElement value = parent.GetProperty(name);
        Assert.Equal(expected ? JsonValueKind.True : JsonValueKind.False, value.ValueKind);
    }

    private void AssertHexColorPair(string rgbName, string hexName, int[] expected)
    {
        int[] rgb = ReadIntArray(Feature(rgbName));
        Assert.Equal<int>(expected, rgb);
        Assert.All(rgb, static channel => Assert.InRange(channel, 0, 65535));
        Assert.Equal(FormatRgb(rgb), Feature(hexName).GetString());
    }

    private static string FormatRgb(int[] rgb)
    {
        return "#" + string.Concat(rgb.Select(static channel => channel.ToString("x4", CultureInfo.InvariantCulture)));
    }

    private static int[] ReadIntArray(JsonElement element)
    {
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        return element.EnumerateArray().Select(static value => value.GetInt32()).ToArray();
    }

    private static void AssertKeySet(JsonElement element, IEnumerable<string> expected, string context)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        string[] actualKeys = element.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedKeys = expected.Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal),
            $"{context} keys differ.{Environment.NewLine}" +
            $"expected: {string.Join(", ", expectedKeys)}{Environment.NewLine}" +
            $"actual:   {string.Join(", ", actualKeys)}");
    }

    private void AssertUnicodeCategoryAtFullConformance(string category)
    {
        JsonElement testResults = _terminal.Report.RootElement.GetProperty("test_results");
        Assert.True(
            testResults.TryGetProperty(category, out JsonElement results) &&
            results.ValueKind == JsonValueKind.Object &&
            results.EnumerateObject().Any(),
            $"{category} did not produce results. Report: {_terminal.ReportPath}");

        List<string> failures = [];
        foreach (JsonProperty result in results.EnumerateObject())
        {
            if (!result.Value.TryGetProperty("pct_success", out JsonElement success) ||
                success.ValueKind != JsonValueKind.Number || success.GetDouble() != 100)
            {
                failures.Add($"{result.Name}: {success}, expected 100%");
            }

            if (!result.Value.TryGetProperty("n_total", out JsonElement total) ||
                total.ValueKind != JsonValueKind.Number || total.GetInt64() <= 0)
            {
                failures.Add($"{result.Name}: missing or empty character count");
            }

            if (!result.Value.TryGetProperty("n_errors", out JsonElement errors) ||
                errors.ValueKind != JsonValueKind.Number || errors.GetInt64() != 0)
            {
                failures.Add($"{result.Name}: missing or nonzero error count ({errors})");
            }

            if (result.Value.TryGetProperty("failed_codepoints", out JsonElement failedCodepoints) &&
                failedCodepoints.ValueKind == JsonValueKind.Array && failedCodepoints.GetArrayLength() > 0)
            {
                failures.Add($"{result.Name}: {failedCodepoints.GetArrayLength()} failed codepoints recorded");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"ucs-detect Unicode category {category} failed: {string.Join(", ", failures.Take(30))}." +
            $"{Environment.NewLine}Report: {_terminal.ReportPath}.{Environment.NewLine}" +
            $"PTY transcript: {_terminal.TranscriptPath}");
    }
}
