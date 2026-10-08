using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using SvcSystems.UI.Terminal;
using System.Threading;

namespace SvcSystems.UI.Terminal.Tests;

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

        Styles.Add(new TerminalTheme());
    }
}
