using WarCommand.Agent.Core.Dev;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

public class FakeCoordinateSourceTests
{
    [Fact]
    public async Task Answers_with_its_own_id_even_when_the_script_disagrees()
    {
        var source = new FakeCoordinateSource([new MapPoint(1m, 2m, "wrong", null, null)]);

        var point = await source.TryReadAsync(CancellationToken.None);

        Assert.NotNull(point);
        Assert.Equal(FakeCoordinateSource.SourceId, point!.Source);
    }

    [Fact]
    public async Task Cycles_through_the_script_in_order()
    {
        var script = new[]
        {
            new MapPoint(1m, 1m, FakeCoordinateSource.SourceId, null, null),
            new MapPoint(2m, 2m, FakeCoordinateSource.SourceId, null, null),
        };
        var source = new FakeCoordinateSource(script);

        var first = await source.TryReadAsync(CancellationToken.None);
        var second = await source.TryReadAsync(CancellationToken.None);
        var third = await source.TryReadAsync(CancellationToken.None);

        Assert.Equal(1m, first!.X);
        Assert.Equal(2m, second!.X);
        Assert.Equal(1m, third!.X);
    }

    [Fact]
    public void Is_always_available_and_never_the_only_option_forced_on_production()
    {
        var source = new FakeCoordinateSource();

        Assert.True(source.IsAvailable);
        Assert.Equal("dev_fake", source.Id);
    }
}
