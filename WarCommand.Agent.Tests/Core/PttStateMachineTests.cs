using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Core.Input;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The named risk is a coordinate captured at key-up instead of key-down: requests land where the
/// cursor drifted, and it is nearly impossible to diagnose from a bug report.
/// </summary>
public class PttStateMachineTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;
    private static readonly MapPoint PointAtKeyDown = new(85.53m, 69.42m, "map_readout", "x85.53 y69.42", 0.94m);
    private static readonly MapPoint PointAfterDrift = new(12.00m, 12.00m, "map_readout", "x12.00 y12.00", 0.94m);

    private static PttOptions Options => PttOptions.From(ContractFixtures.Rules);

    private static PttStateMachine Machine(bool voiceEnabled = true) => new(Options, voiceEnabled);

    private static Draft DraftFor(string typeId, int arity, params MapPoint[] points) => new()
    {
        TypeId = typeId,
        Arity = arity,
        PointLabels = arity == 1 ? ["target"] : ["pickup", "dropoff"],
        Points = points,
        CapturedInDeploymentId = Rows.Deployment,
        Deadline = T0.AddSeconds(20),
    };

    private static ParsedRequest RequestIntent(string typeId, int arity) => new()
    {
        TypeId = typeId,
        OverlayLabel = "MORTAR",
        Arity = arity,
        PointLabels = arity == 1 ? ["target"] : ["pickup", "dropoff"],
        Priority = Priority.Normal,
    };

    [Fact]
    public void The_coordinate_is_snapshotted_on_key_down_never_key_up()
    {
        var ptt = Machine();

        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        ptt.IntentRecognized(RequestIntent("mortar_fire", 1), DraftFor("mortar_fire", 1), T0.AddMilliseconds(950));

        Assert.Equal(PointAtKeyDown, ptt.PendingDraft!.Points[0]);
    }

    [Fact]
    public void A_later_press_never_moves_a_point_already_taken()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        ptt.IntentRecognized(RequestIntent("air_transport_move", 2), DraftFor("air_transport_move", 2), T0.AddSeconds(1));

        // The mouse moved. The tap supplies point 1 and point 0 is untouched.
        ptt.KeyDown(PointAfterDrift, T0.AddSeconds(3));
        ptt.KeyUp(T0.AddSeconds(3).AddMilliseconds(40));

        Assert.Equal(PointAtKeyDown, ptt.PendingDraft!.Points[0]);
        Assert.Equal(PointAfterDrift, ptt.PendingDraft.Points[1]);
    }

    [Fact]
    public void Voice_on_key_down_opens_the_microphone_and_never_the_menu()
    {
        var ptt = Machine();

        var effects = ptt.KeyDown(PointAtKeyDown, T0);

        Assert.Equal(PttState.Capturing, ptt.State);
        Assert.Contains(effects, e => e.Kind == PttEffectKind.StartAudioCapture);
        Assert.DoesNotContain(effects, e => e.Kind == PttEffectKind.OpenMenu);
    }

    [Fact]
    public void With_voice_disabled_the_machine_never_enters_capturing()
    {
        var ptt = Machine(voiceEnabled: false);

        var effects = ptt.KeyDown(PointAtKeyDown, T0);

        // Voice off is a real mode, not a mute over a running recognizer.
        Assert.Equal(PttState.Menu, ptt.State);
        Assert.Contains(effects, e => e.Kind == PttEffectKind.OpenMenu);
        Assert.DoesNotContain(effects, e => e.Kind == PttEffectKind.StartAudioCapture);
    }

    [Fact]
    public void The_menu_opens_on_hesitation_and_not_on_a_separate_key()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);

        Assert.Empty(ptt.Tick(T0.AddMilliseconds(200)).Where(e => e.Kind == PttEffectKind.OpenMenu));
        var effects = ptt.Tick(T0.AddMilliseconds(260));

        Assert.Contains(effects, e => e.Kind == PttEffectKind.OpenMenu);
        Assert.Equal(PttState.Menu, ptt.State);
    }

    [Fact]
    public void Somebody_who_talks_immediately_never_sees_the_menu()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.SpeechDetected(T0.AddMilliseconds(80));

        var effects = ptt.Tick(T0.AddMilliseconds(400));

        Assert.DoesNotContain(effects, e => e.Kind == PttEffectKind.OpenMenu);
        Assert.Equal(PttState.Capturing, ptt.State);
    }

    [Fact]
    public void A_tap_while_a_draft_awaits_a_point_adds_that_point()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        ptt.IntentRecognized(RequestIntent("air_transport_move", 2), DraftFor("air_transport_move", 2), T0.AddSeconds(1));
        Assert.Equal(PttState.AwaitingPoint, ptt.State);

        ptt.KeyDown(PointAfterDrift, T0.AddSeconds(3));
        ptt.KeyUp(T0.AddSeconds(3).AddMilliseconds(50));

        Assert.Equal(PttState.Preview, ptt.State);
        Assert.Equal(2, ptt.PendingDraft!.Points.Count);
        Assert.Equal(PointAfterDrift, ptt.PendingDraft.Points[1]);
    }

    [Fact]
    public void A_tap_with_no_pending_draft_does_nothing()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);

        var effects = ptt.KeyUp(T0.AddMilliseconds(50));

        Assert.Equal(PttState.Idle, ptt.State);
        Assert.Null(ptt.PendingDraft);
        Assert.DoesNotContain(effects, e => e.Kind == PttEffectKind.CommitRequest);
    }

    [Fact]
    public void A_hold_while_a_draft_is_pending_discards_it_and_starts_a_new_utterance()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        ptt.IntentRecognized(RequestIntent("air_transport_move", 2), DraftFor("air_transport_move", 2), T0.AddSeconds(1));

        ptt.KeyDown(PointAfterDrift, T0.AddSeconds(3));
        var effects = ptt.KeyUp(T0.AddSeconds(4));

        // Changing your mind mid-thought must not require pressing Escape first.
        Assert.Contains(effects, e => e.Kind == PttEffectKind.DiscardDraft);
        Assert.Null(ptt.PendingDraft);
        Assert.Equal(PttState.Recognizing, ptt.State);
    }

    [Fact]
    public void Awaiting_point_expires_silently_but_says_why()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        ptt.IntentRecognized(RequestIntent("air_transport_move", 2), DraftFor("air_transport_move", 2), T0.AddSeconds(1));

        var timeout = T0.AddSeconds(1 + ContractFixtures.Rules.AwaitingPointTimeoutS + 1);
        var effects = ptt.Tick(timeout);

        Assert.Contains(effects, e => e.Kind == PttEffectKind.DiscardDraft);
        Assert.DoesNotContain(effects, e => e.Kind == PttEffectKind.CommitRequest);
        Assert.Equal(PttStateMachine.NoPointMessage, ptt.Message);
        Assert.Equal(PttState.Idle, ptt.State);
    }

    [Fact]
    public void A_complete_request_previews_then_commits()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        var shown = ptt.IntentRecognized(RequestIntent("mortar_fire", 1), DraftFor("mortar_fire", 1), T0.AddSeconds(1));

        Assert.Contains(shown, e => e.Kind == PttEffectKind.ShowPreview);
        Assert.Equal(PttState.Preview, ptt.State);

        var committed = ptt.Tick(T0.AddSeconds(1).AddMilliseconds(ContractFixtures.Rules.PreviewHoldMs + 1));

        Assert.Contains(committed, e => e.Kind == PttEffectKind.CommitRequest);
        Assert.Equal(PttState.Idle, ptt.State);
    }

    [Fact]
    public void Commands_never_preview()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));

        var effects = ptt.IntentRecognized(
            new ParsedCommand { VerbId = "accept", SlotRef = "4", Slot = 4 },
            draft: null,
            T0.AddSeconds(1));

        Assert.Contains(effects, e => e.Kind == PttEffectKind.ExecuteCommand);
        Assert.DoesNotContain(effects, e => e.Kind == PttEffectKind.ShowPreview);
        Assert.Equal(PttState.Idle, ptt.State);
    }

    [Fact]
    public void A_spoken_grid_previews_longer_than_a_captured_point()
    {
        var ptt = Machine();
        var spoken = new MapPoint(85.53m, 69.42m, "spoken_grid", "eight five point five three", 0.81m);
        ptt.KeyDown(null, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        ptt.IntentRecognized(RequestIntent("mortar_fire", 1), DraftFor("mortar_fire", 1, spoken), T0.AddSeconds(1));

        var atPreviewHold = ptt.Tick(T0.AddSeconds(1).AddMilliseconds(ContractFixtures.Rules.PreviewHoldMs + 1));
        Assert.DoesNotContain(atPreviewHold, e => e.Kind == PttEffectKind.CommitRequest);

        var later = ptt.Tick(T0.AddSeconds(4));
        Assert.Contains(later, e => e.Kind == PttEffectKind.CommitRequest);
    }

    [Fact]
    public void Below_the_intent_floor_the_overlay_shows_the_transcript_and_sends_nothing()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));

        var effects = ptt.IntentRecognized(
            new ParsedUnrecognized { Transcript = "wreck it", Confidence = 0.4 },
            draft: null,
            T0.AddSeconds(1));

        Assert.Contains(effects, e => e.Kind == PttEffectKind.ShowMessage);
        Assert.DoesNotContain(effects, e => e.Kind == PttEffectKind.CommitRequest);
        Assert.Equal("? \"wreck it\"", ptt.Message);
    }

    [Fact]
    public void Escape_at_any_non_idle_state_returns_to_idle_and_discards()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        ptt.IntentRecognized(RequestIntent("air_transport_move", 2), DraftFor("air_transport_move", 2), T0.AddSeconds(1));

        var effects = ptt.Escape(T0.AddSeconds(2));

        Assert.Contains(effects, e => e.Kind == PttEffectKind.DiscardDraft);
        Assert.Equal(PttState.Idle, ptt.State);
        Assert.Null(ptt.PendingDraft);
    }

    [Fact]
    public void Aborting_the_draft_is_step_zero_of_a_deployment_hop()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));
        ptt.IntentRecognized(RequestIntent("air_transport_move", 2), DraftFor("air_transport_move", 2), T0.AddSeconds(1));

        Assert.True(ptt.AbortDraft(T0.AddSeconds(2)));

        Assert.Null(ptt.PendingDraft);
        Assert.Null(ptt.SnapshotPoint);
        Assert.Equal(PttStateMachine.DraftDiscardedMessage, ptt.Message);
        Assert.False(ptt.AbortDraft(T0.AddSeconds(3)));
    }

    [Fact]
    public void A_disambiguation_opens_a_menu_rather_than_committing()
    {
        var ptt = Machine();
        ptt.KeyDown(PointAtKeyDown, T0);
        ptt.KeyUp(T0.AddMilliseconds(900));

        var effects = ptt.IntentRecognized(
            new ParsedDisambiguation
            {
                Alias = "flank",
                Options = [new DisambiguationOption("flank", "FLANK", null, null), new DisambiguationOption("armor_support", "ARMOR", null, null)],
            },
            draft: null,
            T0.AddSeconds(1));

        Assert.Contains(effects, e => e.Kind == PttEffectKind.ShowDisambiguation);
        Assert.DoesNotContain(effects, e => e.Kind == PttEffectKind.CommitRequest);
        Assert.Equal(PttState.Menu, ptt.State);
    }
}
