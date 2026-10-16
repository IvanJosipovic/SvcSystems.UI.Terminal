using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

/// <summary>
/// Validates the expected ucs-detect report schema itself, without running the detector.
/// These checks fail fast when the pinned feature list and the per-function key map drift apart.
/// </summary>
public sealed class UcsDetectReportSchemaTests
{
    [Fact]
    public void UcsDetect_Schema_AssignsEveryTerminalResultKeyToADetectorFunction()
    {
        HashSet<string> covered = new(StringComparer.Ordinal);
        foreach ((string function, string[] keys) in UcsDetectReportSchema.DetectorFunctions)
        {
            Assert.NotEmpty(keys);
            foreach (string key in keys)
            {
                Assert.True(
                    covered.Add(key),
                    $"Detector function {function} redeclares terminal_results key '{key}'.");
            }
        }

        string[] missing = UcsDetectReportSchema.TerminalResultKeys
            .Where(key => !covered.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] unexpected = covered
            .Where(static key => !UcsDetectReportSchema.TerminalResultKeys.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0 && unexpected.Length == 0,
            $"Every terminal_results key must be owned by exactly one detector function." +
            $"{Environment.NewLine}Unassigned: {string.Join(", ", missing)}" +
            $"{Environment.NewLine}Undeclared: {string.Join(", ", unexpected)}");
    }

    [Fact]
    public void UcsDetect_Schema_PinsTheSupportedDecPrivateModesSorted()
    {
        Assert.Equal<int>(
            UcsDetectReportSchema.SupportedDecPrivateModes.Order(),
            UcsDetectReportSchema.SupportedDecPrivateModes);
        Assert.Equal(
            UcsDetectReportSchema.SupportedDecPrivateModes.Length,
            UcsDetectReportSchema.SupportedDecPrivateModes.Distinct().Count());
    }
}
