using Avalonia.Headless;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AvaloniaTerminal.Tests.TestApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
