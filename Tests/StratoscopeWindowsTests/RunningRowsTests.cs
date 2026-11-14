using Stratoscope.Core;

namespace Stratoscope.Tests;

/// <summary>The rows of the running requests share an area of fixed height; their pitch is the area divided by the count, within limits.</summary>
public class RunningRowsTests
{
    [Fact]
    public void TheRowsFillTheAreaBetweenTheLimits()
    {
        for (int n = 1; n <= RunningRows.MaxRows; n++)
        {
            var row = RunningRows.For(n);
            Assert.InRange(row.Pitch, 1, RunningRows.MaxPitch);
            // Up to the maximum pitch the rows fill the area exactly; after it, one request is not taller than a row ever was.
            Assert.Equal(Math.Min(RunningRows.MaxPitch, RunningRows.AreaHeight / n), row.Pitch, 6);
            Assert.True(row.Pitch * n <= RunningRows.AreaHeight + 1e-9);
            Assert.True(row.Bar <= row.Pitch);
        }
        Assert.Equal(RunningRows.MaxPitch, RunningRows.For(1).Pitch);
        Assert.Equal(RunningRows.AreaHeight / 8, RunningRows.For(8).Pitch);
        Assert.Equal(RunningRows.For(8), RunningRows.For(20));      // never more than eight rows
    }

    [Fact]
    public void TheRowsGetThinnerWithTheCountAndTextOnlyWhereItFits()
    {
        var styles = Enumerable.Range(1, 8).Select(n => RunningRows.For(n).Style).ToArray();
        Assert.Equal(RunningRowStyle.Full, styles[0]);
        Assert.Equal(RunningRowStyle.BarOnly, styles[7]);
        Assert.Equal(styles.OrderBy(s => (int)s), styles);          // never a fuller row after a thinner one
        for (int n = 2; n <= 8; n++) Assert.True(RunningRows.For(n).Pitch <= RunningRows.For(n - 1).Pitch);
        Assert.All(Enumerable.Range(1, 8).Select(RunningRows.For).Where(r => r.Style != RunningRowStyle.BarOnly), r => Assert.True(r.Pitch >= RunningRows.CompactPitch));
    }

    private static BatchSlot Slot(string state, double? limit) => new BatchSlot { State = state, MaxTokens = limit, Generated = 100 };

    [Fact]
    public void TheLimitIsSharedOnlyWhenEveryGeneratingRequestHasTheSame()
    {
        Assert.Equal(32_000, RunningRows.SharedLimit(new[] { Slot("decoding", 32_000), Slot("decoding", 32_000), Slot("reading", null) }));
        Assert.Null(RunningRows.SharedLimit(new[] { Slot("decoding", 32_000), Slot("decoding", 16_000) }));
        Assert.Null(RunningRows.SharedLimit(new[] { Slot("decoding", 32_000), Slot("decoding", null) }));
        Assert.Null(RunningRows.SharedLimit(new[] { Slot("reading", null), Slot("reading", null) }));
    }
}
