namespace SvcSystems.UI.Terminal.Tests;

/// <summary>
/// Expected shape of the JSON report produced by the pinned <c>ucs-detect</c> version.
/// Every key in <c>terminal_results</c> is attributed to the detector function that
/// produces it, so a regression can be traced back to a single named test.
/// </summary>
internal static class UcsDetectReportSchema
{
    public const string UcsDetectVersion = "2.3.8";

    public const string UnicodeVersion = "18.0.0";

    public const string WcwidthVersion = "0.9.2";

    public const int ProbedDecPrivateModeCount = 160;

    public static readonly IReadOnlySet<string> TopLevelKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "ambiguous_width",
        "datetime",
        "environment",
        "height",
        "preferred_encoding",
        "python_version",
        "seconds_elapsed",
        "session_arguments",
        "software_name",
        "software_version",
        "system",
        "terminal_results",
        "test_results",
        "ucs_detect_version",
        "unicode_version",
        "wcwidth_version",
        "width",
    };

    public static readonly IReadOnlySet<string> UnicodeResultCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "unicode_wide_results",
        "sri_results",
        "sfz_results",
        "ri_results",
        "emoji_zwj_results",
        "emoji_vs16_results",
        "emoji_vs15_results",
        "narrow_results",
        "tofu_results",
        "language_results",
    };

    /// <summary>
    /// The complete set of <c>terminal_results</c> keys the pinned detector can emit.
    /// A new key here means the dependency added a feature that needs a coverage decision.
    /// </summary>
    public static readonly IReadOnlySet<string> TerminalResultKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "background_color_hex",
        "background_color_rgb",
        "cell_height",
        "cell_width",
        "color_scheme",
        "colored_underlines",
        "decrqcra",
        "decrqss",
        "decrqss_settings",
        "decrqss_truecolor_probe",
        "device_attributes",
        "foreground_color_hex",
        "foreground_color_rgb",
        "height",
        "iterm2_features",
        "iterm2_graphics",
        "kitty_clipboard_protocol",
        "kitty_graphics",
        "kitty_keyboard",
        "kitty_notifications",
        "kitty_pointer_shapes",
        "kitty_query",
        "modes",
        "number_of_colors",
        "osc52_clipboard",
        "osc52_detection",
        "pixels_height",
        "pixels_width",
        "screen_ratio",
        "screen_ratio_name",
        "sixel",
        "software_method",
        "software_name",
        "software_version",
        "styled_underlines",
        "tab_stop_width",
        "text_sizing",
        "tofu",
        "ttype",
        "width",
        "xtgettcap",
        "xtgettcap-bad-screenleak",
        "xtversion_raw",
        "xtversion-bad-screenleak",
    };

    /// <summary>
    /// Maps each <c>ucs-detect</c> detector function (from <c>ucs_detect/terminal.py</c>)
    /// to the <c>terminal_results</c> keys it owns. The union of the values must equal
    /// <see cref="TerminalResultKeys"/> so that every reported key is asserted by a test
    /// named after the function that produces it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> DetectorFunctions =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["make_terminal"] = ["ttype", "number_of_colors"],
            ["get_tty_size"] = ["width", "height"],
            ["maybe_determine_colors"] = ["foreground_color_rgb", "foreground_color_hex", "background_color_rgb", "background_color_hex"],
            ["maybe_determine_da_and_sixel"] = ["device_attributes", "sixel"],
            ["maybe_determine_software"] = ["software_name", "software_version", "software_method", "xtversion_raw", "xtversion-bad-screenleak"],
            ["maybe_determine_cell_size"] = ["cell_height", "cell_width"],
            ["maybe_determine_pixel_size"] = ["pixels_height", "pixels_width"],
            ["maybe_determine_screen_ratio"] = ["screen_ratio", "screen_ratio_name"],
            ["maybe_determine_osc52_clipboard"] = ["osc52_clipboard", "osc52_detection"],
            ["maybe_determine_dec_modes"] = ["modes"],
            ["maybe_determine_kitty_keyboard"] = ["kitty_keyboard"],
            ["maybe_determine_xtgettcap"] = ["xtgettcap", "xtgettcap-bad-screenleak"],
            ["maybe_determine_kitty_graphics"] = ["kitty_graphics"],
            ["maybe_determine_iterm2_features"] = ["iterm2_features"],
            ["maybe_determine_iterm2_graphics"] = ["iterm2_graphics"],
            ["maybe_determine_text_sizing"] = ["text_sizing"],
            ["maybe_determine_tofu"] = ["tofu"],
            ["maybe_determine_tab_stop_width"] = ["tab_stop_width"],
            ["maybe_determine_kitty_notifications"] = ["kitty_notifications"],
            ["maybe_determine_kitty_clipboard"] = ["kitty_clipboard_protocol"],
            ["maybe_determine_kitty_pointer_shapes"] = ["kitty_pointer_shapes"],
            ["maybe_determine_styled_underlines"] = ["styled_underlines", "colored_underlines"],
            ["maybe_determine_color_scheme"] = ["color_scheme"],
            ["maybe_determine_kitty_query"] = ["kitty_query"],
            ["maybe_determine_decrqss"] = ["decrqss", "decrqss_settings"],
            ["maybe_determine_decrqss_truecolor"] = ["decrqss_truecolor_probe"],
            ["maybe_determine_decrqcra"] = ["decrqcra"],
        };

    public static readonly IReadOnlySet<string> DeviceAttributeKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "extensions",
        "raw",
        "service_class",
    };

    public static readonly int[] DeviceAttributeExtensions = [1, 2, 4, 6, 9, 15, 16, 17, 18, 21, 22, 28, 29];

    public static readonly IReadOnlySet<string> DecPrivateModeDescriptorKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "changeable",
        "enabled",
        "mode_description",
        "mode_name",
        "supported",
        "value",
        "value_description",
    };

    /// <summary>
    /// The DEC private modes the engine answers over DECRQM. Captured from the
    /// <c>--detect-all-dec-modes</c> probe run and pinned so the probe cannot silently regress.
    /// </summary>
    public static readonly int[] SupportedDecPrivateModes =
    [
        1, 3, 4, 5, 6, 7, 9, 18, 19, 25, 35, 40, 41, 42, 45, 47, 66, 67, 69, 80, 95,
        1000, 1002, 1003, 1004, 1005, 1006, 1015, 1016, 1034, 1036, 1039, 1045, 1047, 1049, 1070,
        2004, 2026, 2027, 2048, 5522, 8452, 9001,
    ];

    public static readonly IReadOnlySet<string> KittyKeyboardKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "disambiguate",
        "report_all_keys",
        "report_alternates",
        "report_events",
        "report_text",
        "supported_flags",
    };

    public static readonly IReadOnlySet<string> XtGetTcapKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "capabilities",
        "supported",
    };

    /// <summary>Capabilities the engine is required to advertise over XTGETTCAP.</summary>
    public static readonly IReadOnlyDictionary<string, string> RequiredXtGetTcapCapabilities =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ms"] = "\u001b]52;%p1%s;%p2%s\u0007",
            ["RGB"] = "8/8/8",
            ["Su"] = "",
            ["Tc"] = "",
            ["TN"] = "xterm",
        };

    public static readonly IReadOnlySet<string> Iterm2FeatureContainerKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "features",
        "supported",
    };

    public static readonly IReadOnlySet<string> Iterm2FeatureKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "bracketed_paste",
        "clipboard_writable",
        "decslrm",
        "focus_reporting",
        "mouse",
        "notifications",
        "overline",
        "sixel",
        "strikethrough",
        "sync",
        "truecolor",
        "unicode_basic",
    };

    /// <summary>
    /// Values the UI host can currently support end to end. OSC 8 is omitted from the capability
    /// reply because the control does not expose clickable hyperlink behavior.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, bool> Iterm2FeatureValues =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["bracketed_paste"] = true,
            ["clipboard_writable"] = true,
            ["decslrm"] = true,
            ["focus_reporting"] = true,
            ["mouse"] = true,
            ["notifications"] = true,
            ["overline"] = true,
            ["sixel"] = true,
            ["strikethrough"] = true,
            ["sync"] = true,
            ["truecolor"] = true,
            ["unicode_basic"] = true,
        };

    public static readonly IReadOnlySet<string> KittyNotificationKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "supported",
    };

    public static readonly IReadOnlySet<string> KittyPointerShapeKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "current",
        "supported",
    };

    public static readonly IReadOnlySet<string> Osc52DetectionKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "da1_extension_52",
        "xtgettcap_ms",
    };

    public static readonly IReadOnlySet<string> TextSizingKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "scale",
        "width",
    };

    public static readonly IReadOnlySet<string> TofuKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "protocol",
        "supported",
    };

    /// <summary>The DECRQSS settings a terminal may answer. A run may answer only a subset.</summary>
    public static readonly IReadOnlySet<string> DecrqssSettingKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "decsace",
        "decsasd",
        "decsca",
        "decscl",
        "decscpp",
        "decscusr",
        "decslpp",
        "decslrm",
        "decsnls",
        "decssdt",
        "decstbm",
        "sgr",
    };

    /// <summary>The DECRQSS settings the engine is required to answer.</summary>
    public static readonly IReadOnlySet<string> RequiredDecrqssSettingKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "decsace",
        "decsasd",
        "decsca",
        "decscl",
        "decscusr",
        "decslpp",
        "decslrm",
        "decsnls",
        "decssdt",
        "decstbm",
        "sgr",
    };
}
