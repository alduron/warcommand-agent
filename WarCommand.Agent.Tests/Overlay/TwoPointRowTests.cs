using System;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Tests.Core;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The arity-2 row: which point is which, and the leg between them. Reported as unreadable, with
/// '074deg 2.4km' read as an error code rather than a direction and a distance.
/// </summary>
public class TwoPointRowTests
{
    private static readonly Guid Viewer = Guid.NewGuid();

    [Fact]
    public void A_second_point_costs_the_row_no_extra_line()
    {
        // It shares line 2 with the callsign and the ticket, which every row already draws. The
        // caption that used to name it is an arrow in the 8-unit tie column instead.
        var row = ViewModel(Transport());

        Assert.Equal("x91.44 y58.02", row.SecondPointDisplay);
    }

    [Fact]
    public void A_one_point_row_leaves_the_second_slot_empty()
    {
        var row = ViewModel(Mortar());

        Assert.Null(row.SecondPointDisplay);
    }

    [Fact]
    public void The_leg_carries_no_bearing()
    {
        // A bearing here could only be pickup to dropoff, because nothing in the system knows
        // where a player is standing, so it could never orient a driver. Distance answers the
        // question the row exists to answer: truck job or lift.
        var row = ViewModel(Transport());

        Assert.DoesNotContain("BRG", row.LegDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain("deg", row.LegDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void The_leg_is_metres_when_the_map_scale_is_known()
    {
        // 4.6847 units between these two points at 100 m per unit is 468.5, which formats to 469.
        var row = BoardRowViewModel.FromPrimary(Transport(), Viewer, DateTimeOffset.UtcNow, 100m);

        Assert.Equal("469m", row.LegDisplay);
    }

    [Fact]
    public void The_leg_falls_back_to_map_units_when_the_scale_is_unknown()
    {
        // An honest unitless number beats a confidently wrong metre count.
        var row = ViewModel(Transport());

        Assert.EndsWith("u", row.LegDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_leg_reads_in_kilometres()
    {
        var far = Row("TRANSPORT", "TPT-17",
        [
            new BoardPoint(0, "pickup", new MapPoint(10m, 10m, "manual", null, null)),
            new BoardPoint(1, "dropoff", new MapPoint(40m, 10m, "manual", null, null)),
        ]);

        var row = BoardRowViewModel.FromPrimary(far, Viewer, DateTimeOffset.UtcNow, 100m);

        Assert.Equal("3.0km", row.LegDisplay);
    }

    [Fact]
    public void The_second_point_is_the_coordinate_alone_without_an_arrow()
    {
        // The arrow moved out of the value and into the label column, so the two coordinates line
        // up under each other instead of being offset by three characters.
        var row = ViewModel(Transport());

        Assert.NotNull(row.SecondPointDisplay);
        Assert.DoesNotContain("->", row.SecondPointDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void An_urgent_open_row_says_urgent_once()
    {
        // The red edge and the state word already carry it, and the API leaves 'urgent' in the
        // modifiers because that is what set the priority. Three times on one row pushed the facts
        // that are only said once off the end.
        var row = BoardRowViewModel.FromPrimary(
            Transport() with { Priority = Priority.Urgent, Modifiers = ["urgent", "danger_close"] },
            Viewer,
            DateTimeOffset.UtcNow);

        Assert.Equal("URGENT", row.StateWord);
        Assert.DoesNotContain("URGENT", row.Tags);
        Assert.Contains("DANGER CLOSE", row.Tags);
    }

    [Fact]
    public void The_urgent_chip_comes_back_when_the_state_word_is_something_else()
    {
        // A moved requester takes the state word, so nothing else on the row is carrying urgency.
        var row = BoardRowViewModel.FromPrimary(
            Transport() with
            {
                Priority = Priority.Urgent,
                Modifiers = ["urgent"],
                RequesterMoved = true,
            },
            Viewer,
            DateTimeOffset.UtcNow);

        Assert.Equal("REQUESTER MOVED", row.StateWord);
        Assert.Contains("URGENT", row.Tags);
    }

    private static BoardRowViewModel ViewModel(BoardRow row) =>
        BoardRowViewModel.FromPrimary(row, Viewer, DateTimeOffset.UtcNow);

    private static BoardRow Transport() => Row(
        "TRANSPORT",
        "TPT-16",
        [
            new BoardPoint(0, "pickup", new MapPoint(88.00m, 61.20m, "manual", null, null)),
            new BoardPoint(1, "dropoff", new MapPoint(91.44m, 58.02m, "manual", null, null)),
        ]);

    private static BoardRow Mortar() => Row(
        "MORTAR",
        "MTR-14",
        [new BoardPoint(0, "target", new MapPoint(85.53m, 69.42m, "manual", null, null))]);

    private static BoardRow Row(string label, string ticket, IReadOnlyList<BoardPoint> points) =>
        Rows.A() with
        {
            TicketCode = ticket,
            OverlayLabel = label,
            Points = points,
        };
}
