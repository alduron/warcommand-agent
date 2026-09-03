using WarCommand.Agent.Core.Contracts;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The clipboard text is the whole coordinate relay: somebody pastes it into game chat and every
/// squadmate who clicks it gets the map pinned. Its shape is a fact about the game.
/// </summary>
public sealed class CoordinateHandoffTextTests
{
    private static CoordinateHandoffSection Handoff =>
        BundledContracts.GameProfile().Current.CoordinateHandoff;

    [Fact]
    public void The_default_format_is_the_measured_shape()
    {
        var text = CoordinateHandoffText.Render(Handoff, "MED-24", 94.70m, 107.25m);

        // MEASURED in game: written this way the coordinate renders as a CLICKABLE link that pins
        // the map, and the ticket in front survives the parse. The copy path used to write
        // "94.70 107.25" from a hardcoded interpolation, which pins nothing and names no job.
        Assert.Equal("MED-24 x94.70, y107.25", text);
    }

    [Fact]
    public void Both_axes_always_carry_two_decimals()
    {
        var text = CoordinateHandoffText.Render(Handoff, "MTR-01", 7m, 100.5m);

        // A grid that drops a trailing zero is a different string to whatever parses it, and the
        // game's own readout always shows two places.
        Assert.Equal("MTR-01 x7.00, y100.50", text);
    }

    [Fact]
    public void A_template_without_a_ticket_leaves_no_gap()
    {
        var text = CoordinateHandoffText.Render(Handoff, "MTR-01", 1.5m, 2.5m, formatId: "prefixed");

        Assert.Equal("x1.50 y2.50", text);
    }

    [Fact]
    public void An_unknown_format_falls_back_to_the_default_rather_than_writing_nothing()
    {
        var text = CoordinateHandoffText.Render(Handoff, "CAS-09", 1m, 2m, formatId: "no_such_format");

        Assert.Equal("CAS-09 x1.00, y2.00", text);
    }

    [Fact]
    public void A_missing_ticket_does_not_leave_a_leading_space()
    {
        var text = CoordinateHandoffText.Render(Handoff, ticket: null, 1m, 2m);

        Assert.Equal("x1.00, y2.00", text);
    }
}
