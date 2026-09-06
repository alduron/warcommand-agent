using System;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Tests.Core;
using WarCommand.Agent.Overlay;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// The tag line. Reported as clipped: a delivery carrying four tags rendered as 'RIFLE MA...'.
/// </summary>
public class TagsAreTheirOwnLineTests
{
    private static readonly Guid Viewer = Guid.NewGuid();

    [Fact]
    public void The_type_column_carries_the_type_alone()
    {
        var row = ViewModel(Tagged("smoke", "danger_close"));

        Assert.Equal("MORTAR", row.TypeAndQualifier);
    }

    [Fact]
    public void Every_tag_reaches_the_tag_line()
    {
        var row = ViewModel(Tagged("smoke", "danger_close"));

        Assert.Equal("SMOKE DANGER CLOSE", row.TagsDisplay);
    }

    [Fact]
    public void A_row_with_no_tags_leaves_the_line_empty_so_it_collapses()
    {
        // Empty rather than null: the XAML trigger fires on Text="" and a null binding does not,
        // so the line would keep its height on every untagged row.
        var row = ViewModel(Tagged());

        Assert.Equal(string.Empty, row.TagsDisplay);
    }

    [Fact]
    public void The_tag_line_survives_a_reconcile()
    {
        var target = ViewModel(Tagged());
        target.CopyFrom(ViewModel(Tagged("smoke")));

        Assert.Equal("SMOKE", target.TagsDisplay);
    }

    [Fact]
    public void Each_tag_is_its_own_entry_so_the_row_can_draw_it_as_a_tag()
    {
        // The web draws a chip per tag. One run-on line here made the same request look like two
        // different things, so the overlay draws a chip each from this.
        var row = ViewModel(Tagged("smoke", "danger_close"));

        Assert.Equal(["SMOKE", "DANGER CLOSE"], row.Tags);
    }

    [Fact]
    public void The_tag_line_and_the_tag_chips_never_disagree()
    {
        var row = ViewModel(Tagged("smoke", "danger_close"));

        Assert.Equal(row.TagsDisplay, string.Join(' ', row.Tags));
    }

    [Fact]
    public void Reconciling_an_unchanged_row_raises_no_tag_change()
    {
        // A fresh list every poll would rebuild every chip on every row.
        var target = ViewModel(Tagged("smoke"));
        var raised = new List<string?>();
        target.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        target.CopyFrom(ViewModel(Tagged("smoke")));

        Assert.DoesNotContain(nameof(BoardRowViewModel.Tags), raised);
    }

    private static BoardRowViewModel ViewModel(BoardRow row) =>
        BoardRowViewModel.FromPrimary(row, Viewer, DateTimeOffset.UtcNow);

    private static BoardRow Tagged(params string[] modifiers) =>
        Rows.A() with
        {
            OverlayLabel = "MORTAR",
            TicketCode = "MTR-14",
            Modifiers = modifiers,
        };
}
