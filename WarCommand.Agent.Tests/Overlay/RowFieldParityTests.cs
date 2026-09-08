using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WarCommand.Agent.Core.Model;
using WarCommand.Agent.Overlay;
using WarCommand.Agent.Tests.Core;

namespace WarCommand.Agent.Tests.Overlay;

/// <summary>
/// contracts/row-fields.json is the list both surfaces answer to. A field on it that the overlay
/// row does not draw fails here, and the same list fails the web suite.
/// </summary>
public class RowFieldParityTests
{
    private static readonly Guid Bear = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly BoardPoint[] TwoPoints =
    [
        new(0, "pickup", new MapPoint(85.53m, 69.42m, "manual", null, null)),
        new(1, "dropoff", new MapPoint(12.10m, 44.02m, "manual", null, null)),
    ];

    /// <summary>A loaded row the viewer asked for and BEAR took. Held, so it renders in ACTIVE.</summary>
    private static BoardRow Loaded() =>
        Rows.A(typeId: "transport_move", state: RequestState.Claimed, requester: Rows.Viewer, claimant: Bear) with
        {
            OverlayLabel = "HOTDROP",
            TicketCode = "TPT-14",
            TargetRoleIds = ["ground_transport", "air_transport"],
            Points = TwoPoints,
            Modifiers = ["smoke"],
            QuantityRequested = 3,
            CoRequesterCount = 3,
            ReleaseCount = 2,
            Note = "north gate, under fire",
        };

    /// <summary>An open urgent row: priority and the auto-cancel countdown only render on one.</summary>
    private static BoardRow OpenUrgent() =>
        Rows.A(priority: Priority.Urgent) with { Slot = 1, Points = [TwoPoints[0]] };

    /// <summary>One check per field id in the contract. A missing id is a failure, not a skip.</summary>
    private static readonly Dictionary<string, (Func<BoardRow> Row, Func<BoardRowViewModel, bool> Holds)> Checks =
        new(StringComparer.Ordinal)
        {
            ["target_role_ids"] = (Loaded, vm => vm.RoleId == "ground_transport"
                && vm.Chips.Any(c => c.Contains("ROLES", StringComparison.Ordinal))),
            ["type_id"] = (Loaded, vm => vm.TypeAndQualifier == "HOTDROP"),
            ["points"] = (Loaded, vm => vm.CoordinatesDisplay.Length > 0 && !string.IsNullOrEmpty(vm.SecondPointDisplay)),
            ["modifiers"] = (Loaded, vm => vm.Tags.Any(t => t.Contains("SMOKE", StringComparison.OrdinalIgnoreCase))),
            ["quantity_requested"] = (Loaded, vm => vm.Chips.Any(c => c.Contains('3', StringComparison.Ordinal))),
            ["requested_by_callsign"] = (Loaded, vm => vm.Requester.Contains("YOU", StringComparison.Ordinal)),
            ["co_requester_count"] = (Loaded, vm => vm.Requester.Contains("+2", StringComparison.Ordinal)),
            ["claimed_by_callsign"] = (Loaded, vm => vm.StateWord?.Contains("BEAR", StringComparison.Ordinal) == true),
            ["state"] = (Loaded, vm => vm.Accent == RowAccent.Warned),
            ["priority"] = (OpenUrgent, vm => vm.StateWord == "URGENT" || vm.Accent == RowAccent.Urgent),
            ["note"] = (Loaded, vm => vm.NoteDisplay.Contains("north gate", StringComparison.Ordinal)),
            ["ticket_code"] = (Loaded, vm => vm.TicketCode == "TPT-14"),
            ["release_count"] = (Loaded, vm => vm.Chips.Any(c => c.StartsWith("RETRY", StringComparison.Ordinal))),
            ["expires_at"] = (OpenUrgent, vm => vm.HasCountdown),
        };

    private static BoardRowViewModel Build(BoardRow row, RowSurface surface) =>
        BoardRowViewModel.FromPrimary(
            row,
            Rows.Viewer,
            Rows.Epoch.AddSeconds(12),
            catalog: ContractFixtures.Catalog,
            line: 1,
            surface: surface);

    private static List<string> RequiredOfTheOverlay()
    {
        using var document = JsonDocument.Parse(ContractFixtures.RowFieldsJson);
        var required = new List<string>();

        foreach (var field in document.RootElement.GetProperty("fields").EnumerateArray())
        {
            var overlay = field.GetProperty("overlay");
            if (overlay.ValueKind == JsonValueKind.String && overlay.GetString() == "required")
            {
                required.Add(field.GetProperty("id").GetString()!);
            }
        }

        return required;
    }

    [Fact]
    public void Every_required_field_is_checked_here()
    {
        // A new field with no check would otherwise pass by drawing nothing.
        var unchecked_ = RequiredOfTheOverlay().Where(id => !Checks.ContainsKey(id)).ToList();
        Assert.Empty(unchecked_);
    }

    [Fact]
    public void The_queue_row_carries_every_required_field()
    {
        foreach (var id in RequiredOfTheOverlay())
        {
            var (row, holds) = Checks[id];
            Assert.True(holds(Build(row(), RowSurface.Queue)), $"the queue row drops {id}");
        }
    }

    [Fact]
    public void The_active_row_carries_every_required_field()
    {
        foreach (var id in RequiredOfTheOverlay())
        {
            var (row, holds) = Checks[id];
            Assert.True(holds(Build(row(), RowSurface.Active)), $"the ACTIVE row drops {id}");
        }
    }

    [Fact]
    public void Accepting_a_row_changes_the_state_word_and_nothing_else()
    {
        // The regression: a row was a strictly poorer thing the moment the viewer took it.
        var row = Loaded() with { ClaimantParticipantId = Rows.Viewer, ClaimantCallsign = "YOU" };
        var queue = Build(row, RowSurface.Queue);
        var active = Build(row, RowSurface.Active);

        Assert.Equal(queue.TypeAndQualifier, active.TypeAndQualifier);
        Assert.Equal(queue.TagsDisplay, active.TagsDisplay);
        Assert.Equal(queue.Tags, active.Tags);
        Assert.Equal(queue.Chips, active.Chips);
        Assert.Equal(queue.CoordinatesDisplay, active.CoordinatesDisplay);
        Assert.Equal(queue.SecondPointDisplay, active.SecondPointDisplay);
        Assert.Equal(queue.LegDisplay, active.LegDisplay);
        Assert.Equal(queue.SolutionDisplay, active.SolutionDisplay);
        Assert.Equal(queue.NoteDisplay, active.NoteDisplay);
        Assert.Equal(queue.Requester, active.Requester);
        Assert.Equal(queue.TicketCode, active.TicketCode);
        Assert.Equal(queue.RoleId, active.RoleId);
        Assert.Equal(queue.HasCountdown, active.HasCountdown);

        // The one difference: ACTIVE names the counterparty rather than repeating [YOU].
        Assert.Equal("[YOU]", queue.StateWord);
        Assert.Equal("FOR GHOST", active.StateWord);
    }

    [Fact]
    public void Both_halves_of_a_claim_name_the_other_person_the_same_way()
    {
        var taken = Build(Loaded(), RowSurface.Active);
        var doing = Build(
            Loaded() with { RequestedByParticipantId = Bear, RequestedByCallsign = "BEAR", ClaimantParticipantId = Rows.Viewer },
            RowSurface.Active);

        Assert.Equal("BY BEAR", taken.StateWord);
        Assert.Equal("FOR BEAR", doing.StateWord);
    }
}
