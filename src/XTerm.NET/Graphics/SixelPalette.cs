namespace XTerm.Graphics;

/// <summary>
/// The colour registers a Sixel image draws from.
/// </summary>
/// <remarks>
/// <para>These are not the terminal's ANSI palette and are deliberately kept apart from
/// <see cref="XTerm.Common.ColorPalette"/>. Sixel registers are a separate namespace with their
/// own numbering, an image is free to redefine them as it draws, and doing that to the palette the
/// renderer reads on its hot path would repaint the text as a side effect of showing a picture.</para>
/// <para>Sixel images that define every colour they use -- which is nearly all of them -- never
/// read the defaults. They matter for the older images that assume a VT340 is listening.</para>
/// </remarks>
internal sealed class SixelPalette
{
    /// <summary>Number of colour registers, matching the VT340.</summary>
    public const int RegisterCount = 256;

    /// <summary>
    /// The VT340 defaults, as red/green/blue percentages -- the units Sixel itself uses.
    /// </summary>
    private static readonly byte[,] Vt340Defaults =
    {
        {  0,  0,  0 }, // 0  black
        { 20, 20, 80 }, // 1  blue
        { 80, 13, 13 }, // 2  red
        { 20, 80, 20 }, // 3  green
        { 80, 20, 80 }, // 4  magenta
        { 20, 80, 80 }, // 5  cyan
        { 80, 80, 20 }, // 6  yellow
        { 53, 53, 53 }, // 7  grey 50%
        { 26, 26, 26 }, // 8  grey 25%
        { 33, 33, 60 }, // 9  blue, light
        { 60, 26, 26 }, // 10 red, light
        { 33, 60, 33 }, // 11 green, light
        { 60, 33, 60 }, // 12 magenta, light
        { 33, 60, 60 }, // 13 cyan, light
        { 60, 60, 33 }, // 14 yellow, light
        { 80, 80, 80 }, // 15 grey 75%
    };

    /// <summary>One packed BGRA value per register, ready to blit.</summary>
    private readonly uint[] _registers = new uint[RegisterCount];

    public SixelPalette()
    {
        Reset();
    }

    /// <summary>Restores the VT340 defaults; registers past 15 become opaque black.</summary>
    public void Reset()
    {
        for (int i = 0; i < RegisterCount; i++)
        {
            if (i < Vt340Defaults.GetLength(0))
                _registers[i] = PackPercent(Vt340Defaults[i, 0], Vt340Defaults[i, 1], Vt340Defaults[i, 2]);
            else
                _registers[i] = PackPercent(0, 0, 0);
        }
    }

    /// <summary>Reads a register, packed BGRA. Out-of-range indices fold into range.</summary>
    public uint this[int index] => _registers[Normalize(index)];

    /// <summary>Sets a register from red/green/blue percentages, each clamped to 0-100.</summary>
    public void SetRgb(int index, int red, int green, int blue)
    {
        _registers[Normalize(index)] = PackPercent(Clamp100(red), Clamp100(green), Clamp100(blue));
    }

    /// <summary>
    /// Sets a register from hue/lightness/saturation, the other colour space Sixel allows.
    /// </summary>
    /// <param name="index">The colour register to set.</param>
    /// <param name="hue">Degrees, 0-360.</param>
    /// <param name="lightness">Percent, 0-100.</param>
    /// <param name="saturation">Percent, 0-100.</param>
    public void SetHls(int index, int hue, int lightness, int saturation)
    {
        HlsToRgb(hue, Clamp100(lightness), Clamp100(saturation), out var r, out var g, out var b);
        _registers[Normalize(index)] = PackPercent(r, g, b);
    }

    /// <summary>
    /// Wraps rather than throws. A register number is untrusted input arriving from a hostile
    /// process's stdout, and a picture with a silly colour index is not worth a broken terminal.
    /// </summary>
    private static int Normalize(int index)
    {
        if (index < 0)
            index = -index;
        return index % RegisterCount;
    }

    private static int Clamp100(int value) => value < 0 ? 0 : value > 100 ? 100 : value;

    /// <summary>Packs 0-100 percentages into opaque BGRA, the layout the pixel buffer uses.</summary>
    private static uint PackPercent(int red, int green, int blue)
    {
        // Rounded rather than truncated so 100% lands on 255 and not 254.
        uint r = (uint)((red * 255 + 50) / 100);
        uint g = (uint)((green * 255 + 50) / 100);
        uint b = (uint)((blue * 255 + 50) / 100);
        return (0xFFu << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>
    /// Converts Sixel's HLS to RGB, all channels in percent.
    /// </summary>
    /// <remarks>
    /// Sixel's hue ring is rotated 120 degrees from the usual HSL one -- hue 0 is blue, not red --
    /// which is the detail that turns a correct-looking HSL conversion into wrong colours.
    /// </remarks>
    private static void HlsToRgb(int hue, int lightness, int saturation, out int red, out int green, out int blue)
    {
        if (saturation == 0)
        {
            red = green = blue = lightness;
            return;
        }

        int spread = saturation * (100 - Math.Abs(2 * lightness - 100));
        double max = lightness + spread / 200.0;
        double min = lightness - spread / 200.0;

        hue = ((hue % 360) + 360) % 360;
        hue = (hue + 240) % 360;

        double range = max - min;
        double r, g, b;
        switch (hue / 60)
        {
            case 0: r = max; g = min + range * hue / 60.0; b = min; break;
            case 1: r = min + range * (120 - hue) / 60.0; g = max; b = min; break;
            case 2: r = min; g = max; b = min + range * (hue - 120) / 60.0; break;
            case 3: r = min; g = min + range * (240 - hue) / 60.0; b = max; break;
            case 4: r = min + range * (hue - 240) / 60.0; g = min; b = max; break;
            default: r = max; g = min; b = min + range * (360 - hue) / 60.0; break;
        }

        red = Clamp100((int)Math.Round(r));
        green = Clamp100((int)Math.Round(g));
        blue = Clamp100((int)Math.Round(b));
    }
}
