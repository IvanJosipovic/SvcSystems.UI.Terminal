using System.Globalization;
using XTerm.Common;
using Xunit;

namespace SvcSystems.UI.Terminal.Tests;

/// <summary>Replays the pinned wcwidth 0.9.2 / Unicode 18 table over every Unicode scalar value.</summary>
public sealed class XTermWidthTableParityTests
{
    [Fact]
    public void EngineWidthTables_MatchThePinnedWcwidthDataForEveryCodepoint()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "wcwidth-parity.rle");
        string[] lines = File.ReadAllLines(fixturePath);
        int codePoint = 0;
        List<string> mismatches = [];

        foreach (string line in lines)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int expectedWidth = int.Parse(fields[0], CultureInfo.InvariantCulture);
            int runLength = int.Parse(fields[1], CultureInfo.InvariantCulture);
            int endCodePoint = checked(codePoint + runLength);
            for (; codePoint < endCodePoint; codePoint++)
            {
                int actualWidth = WidthTables.Lookup(codePoint);
                if (actualWidth != expectedWidth && mismatches.Count < 20)
                {
                    mismatches.Add($"U+{codePoint:X}: expected {expectedWidth}, got {actualWidth}");
                }
            }
        }

        Assert.Equal(0x110000, codePoint);
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }
}
