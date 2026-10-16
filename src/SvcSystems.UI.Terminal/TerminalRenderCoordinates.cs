namespace SvcSystems.UI.Terminal;

/// <summary>Maps logical render coordinates onto the device-pixel grid.</summary>
internal static class TerminalRenderCoordinates
{
    internal static double Snap(double coordinate, double renderScaling)
    {
        if (!double.IsFinite(renderScaling) || renderScaling <= 0)
        {
            return coordinate;
        }

        return Math.Round(coordinate * renderScaling, MidpointRounding.AwayFromZero) / renderScaling;
    }
}
