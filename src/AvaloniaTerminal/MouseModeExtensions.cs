namespace AvaloniaTerminal;

public static class MouseModeExtensions
{
    public static bool SendButtonPress(this MouseMode mode)
    {
        return mode is MouseMode.VT200 or MouseMode.ButtonEventTracking or MouseMode.AnyEvent;
    }

    public static bool SendButtonRelease(this MouseMode mode)
    {
        return mode != MouseMode.Off;
    }

    public static bool SendButtonTracking(this MouseMode mode)
    {
        return mode is MouseMode.ButtonEventTracking or MouseMode.AnyEvent;
    }

    public static bool SendMotionEvent(this MouseMode mode)
    {
        return mode == MouseMode.AnyEvent;
    }

    public static bool SendsModifiers(this MouseMode mode)
    {
        return mode is MouseMode.VT200 or MouseMode.ButtonEventTracking or MouseMode.AnyEvent;
    }
}
