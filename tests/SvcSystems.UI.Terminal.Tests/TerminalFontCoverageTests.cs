using System.Text;
using Avalonia.Media;
using SvcSystems.UI.Terminal;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

public sealed class TerminalFontCoverageTests : AvaloniaTestBase
{
    private const string Esc = "\u001b";
    private const string Bell = "\u0007";

    [Fact]
    public void MinttyFontCoverageQuery_ReportsOnlyCodepointsTheFontContains()
    {
        bool handled = TerminalFontCoverageService.TryCreateResponse(
            7771,
            "?;65;66;67",
            static codepoint => codepoint is 65 or 67,
            out string response);

        Assert.True(handled);
        Assert.Equal($"{Esc}]7771;!;65;;67{Bell}", response);
    }

    [Theory]
    [InlineData(7772, "?;65")]
    [InlineData(7771, "65")]
    [InlineData(7771, "?;invalid")]
    public void MinttyFontCoverageQuery_RejectsUnrelatedOrInvalidRequests(int oscCode, string payload)
    {
        bool handled = TerminalFontCoverageService.TryCreateResponse(
            oscCode,
            payload,
            static _ => true,
            out string response);

        Assert.False(handled);
        Assert.Empty(response);
    }

    [Fact]
    public Task MinttyFontCoverageQuery_IsAnsweredByTheAttachedControl()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new();
            TerminalControl control = new() { Model = model, FontFamily = FontFamily.Default };
            List<string> input = [];
            model.UserInput += (_, args) => input.Add(Encoding.UTF8.GetString(args.Data.Span));

            model.Feed($"{Esc}]7771;?;65\u0007");

            Assert.Contains($"{Esc}]7771;!;65\u0007", string.Concat(input), StringComparison.Ordinal);
        });
    }

    [Fact]
    public Task MinttyFontCoverageQuery_DoesNotAnswerOtherOscRequests()
    {
        return RunInHeadlessSession(() =>
        {
            using TerminalControlModel model = new();
            TerminalControl control = new() { Model = model, FontFamily = FontFamily.Default };
            List<string> input = [];
            model.UserInput += (_, args) => input.Add(Encoding.UTF8.GetString(args.Data.Span));

            model.Feed($"{Esc}]7772;?;65\u0007");

            Assert.Empty(input);
        });
    }
}
