using Avalonia.Headless;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(SvcSystems.UI.Terminal.Tests.TestApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
