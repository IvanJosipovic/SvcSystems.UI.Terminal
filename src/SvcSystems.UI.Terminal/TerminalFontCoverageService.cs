using System.Globalization;
using System.Text;
using Avalonia.Media;

namespace SvcSystems.UI.Terminal;

internal static class TerminalFontCoverageService
{
    private const int MinttyFontCoverageOsc = 7771;
    private const string MinttyFontCoverageQuery = "?;";
    private const char Escape = '\u001b';
    private const char Bell = '\u0007';

    public static bool TryCreateResponse(
        XTerm.Events.TerminalEvents.OscReceivedEventArgs request,
        FontFamily fontFamily,
        out string response)
    {
        ArgumentNullException.ThrowIfNull(request);
        response = string.Empty;
        if (request.Code != MinttyFontCoverageOsc ||
            !request.Data.StartsWith(MinttyFontCoverageQuery, StringComparison.Ordinal))
        {
            return false;
        }

        bool hasGlyphTypeface = FontManager.Current.TryGetGlyphTypeface(
            new Typeface(fontFamily),
            out GlyphTypeface? glyphTypeface);
        return TryCreateResponse(
            request.Code,
            request.Data,
            hasGlyphTypeface && glyphTypeface is not null
                ? glyphTypeface.CharacterToGlyphMap.ContainsGlyph
                : static _ => false,
            out response);
    }

    internal static bool TryCreateResponse(
        int oscCode,
        string data,
        Func<int, bool> hasGlyph,
        out string response)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(hasGlyph);
        response = string.Empty;
        if (oscCode != MinttyFontCoverageOsc || !data.StartsWith(MinttyFontCoverageQuery, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> requestedCodepoints = data.AsSpan(MinttyFontCoverageQuery.Length);
        StringBuilder responseFields = new(requestedCodepoints.Length + 8);
        responseFields.Append(Escape);
        responseFields.Append("]7771;!");
        responseFields.Append(';');

        bool isFirst = true;
        while (true)
        {
            int separatorIndex = requestedCodepoints.IndexOf(';');
            ReadOnlySpan<char> field = separatorIndex < 0 ? requestedCodepoints : requestedCodepoints[..separatorIndex];
            if (!int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out int codepoint) ||
                !Rune.IsValid(codepoint))
            {
                return false;
            }

            if (!isFirst)
            {
                responseFields.Append(';');
            }

            if (hasGlyph(codepoint))
            {
                responseFields.Append(codepoint.ToString(CultureInfo.InvariantCulture));
            }

            isFirst = false;
            if (separatorIndex < 0)
            {
                break;
            }

            requestedCodepoints = requestedCodepoints[(separatorIndex + 1)..];
        }

        responseFields.Append(Bell);
        response = responseFields.ToString();
        return true;
    }
}
