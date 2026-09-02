using WarCommand.Agent.Core.Tray;

namespace WarCommand.Agent.Tests.Tray;

/// <summary>
/// The one row that reaches the queue. It shipped raising a command the composition root had no
/// case for, so it rendered and did nothing, which is exactly what
/// Convention_WarCommandTrayMenuRendersOnlyHonourableRows forbids.
/// </summary>
public class TrayWebBoardTests
{
    private static readonly TrayMenuState Empty = new();

    private static TrayMenuItem? Row(TrayMenuState state) =>
        TrayMenu.Build(state).FirstOrDefault(i => i.Command == TrayCommand.OpenWebBoard);

    /// <summary>
    /// Gated on the URL, never on being paired. A signed-in agent standing on no deployment has
    /// nothing to open, and a row that cannot answer a click is worse than no row.
    /// </summary>
    [Fact]
    public void No_url_means_no_row()
    {
        Assert.Null(Row(Empty));
        Assert.Null(Row(Empty with { IsPaired = true }));
        Assert.Null(Row(Empty with { IsPaired = true, GroupName = "61ST" }));
    }

    [Fact]
    public void The_row_carries_the_url_it_opens()
    {
        var row = Row(Empty with { WebBoardUrl = "https://warcommand.app/g/61st/d/alpha" });

        Assert.NotNull(row);
        Assert.Equal("Open web board", row.Text);
        Assert.Equal("https://warcommand.app/g/61st/d/alpha", row.Argument);
    }

    /// <summary>Top level, not a child of the group name: it is the only route to the queue.</summary>
    [Fact]
    public void The_row_is_top_level()
    {
        var items = TrayMenu.Build(Empty with { WebBoardUrl = "https://warcommand.app/g/61st/d/alpha" });

        Assert.Contains(items, i => i.Command == TrayCommand.OpenWebBoard);
        Assert.DoesNotContain(
            items.SelectMany(i => i.Children),
            i => i.Command == TrayCommand.OpenWebBoard);
    }
}
