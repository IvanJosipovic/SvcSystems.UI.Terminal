using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using AvaloniaTerminal;
using System.Threading;

namespace AvaloniaTerminal.Tests;

public abstract class AvaloniaTestBase
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaTestBase).Assembly));

    protected static Task RunInHeadlessSession(Action action)
    {
        return Session.Value.Dispatch(action, CancellationToken.None);
    }

    protected static Task RunInHeadlessSession(Func<Task> action)
    {
        return Session.Value.Dispatch(action, CancellationToken.None);
    }

    protected static async Task<T> RunInHeadlessSession<T>(Func<T> action)
    {
        T result = default!;
        await RunInHeadlessSession(() => result = action());
        return result;
    }

}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());

        const string colorsUri = "avares://AvaloniaTerminal/Styles/Colors.axaml";
        Styles.Add(new StyleInclude(new Uri(colorsUri))
        {
            Source = new Uri(colorsUri),
        });
    }
}
