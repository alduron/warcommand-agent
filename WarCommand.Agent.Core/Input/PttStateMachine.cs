using WarCommand.Agent.Core.Board;
using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Grammar;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Core.Input;

/// <summary>Where one press is. Driven by events from WarCommand.Agent.Input; pure here.</summary>
public enum PttState
{
    Idle,

    /// <summary>Key down with voice on. The microphone is open and the menu timer is running.</summary>
    Capturing,

    /// <summary>Key up after a hold. The recognizer has the buffer.</summary>
    Recognizing,

    /// <summary>A complete request, held for preview_hold_ms before it commits.</summary>
    Preview,

    /// <summary>A draft short of its arity. Expires after awaiting_point_timeout_s.</summary>
    AwaitingPoint,

    /// <summary>The hesitation menu, or the whole path when voice is off.</summary>
    Menu,
}

/// <summary>What the host must do. The machine itself touches no device.</summary>
public enum PttEffectKind
{
    StartAudioCapture,
    StopAudioCapture,
    OpenMenu,
    CloseMenu,
    ShowPreview,
    ShowMessage,
    ClearMessage,
    CommitRequest,
    ExecuteCommand,
    ShowDisambiguation,
    DiscardDraft,
}

/// <summary>One thing for the host to do, in the order the list gives them.</summary>
public sealed record PttEffect(PttEffectKind Kind)
{
    public string? Message { get; init; }

    public Draft? Draft { get; init; }

    public ParseResult? Intent { get; init; }
}

/// <summary>
/// The timings the PTT machine runs on. Everything the catalog carries comes from grammar_rules;
/// the rest are UX constants that no contract names today.
/// </summary>
public sealed record PttOptions
{
    /// <summary>A press shorter than this, with no speech, is a tap.</summary>
    public required int TapMaxMs { get; init; }

    public required int PreviewHoldMs { get; init; }

    public required int AwaitingPointTimeoutS { get; init; }

    /// <summary>Hold with no speech energy for this long and the menu opens. Not in any contract.</summary>
    public int MenuHesitationMs { get; init; } = 250;

    /// <summary>A spoken grid previews longer, because reading a grid back takes longer.</summary>
    public int SpokenGridPreviewMs { get; init; } = 2000;

    /// <summary>How long a transient overlay line stays up.</summary>
    public int MessageHoldMs { get; init; } = 2000;

    public static PttOptions From(GrammarRulesDef rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new PttOptions
        {
            TapMaxMs = rules.TapMaxMs,
            PreviewHoldMs = rules.PreviewHoldMs,
            AwaitingPointTimeoutS = rules.AwaitingPointTimeoutS,
        };
    }
}

/// <summary>
/// The PTT state machine. The coordinate is snapshotted on key DOWN, commands never preview, and
/// with voice disabled the machine never enters <see cref="PttState.Capturing"/> at all.
/// </summary>
/// <remarks>
/// Pure and fully unit testable: no window, no microphone, no server, and <c>now</c> is always a
/// parameter.
/// </remarks>
public sealed class PttStateMachine : IDraftOwner
{
    /// <summary>Rendered when a draft times out, rather than letting it vanish.</summary>
    public const string NoPointMessage = "NO POINT - OPEN THE MAP";

    /// <summary>Rendered when a deployment hop aborts the pending draft.</summary>
    public const string DraftDiscardedMessage = "DRAFT DISCARDED";

    private static readonly IReadOnlyList<PttEffect> None = [];

    private readonly PttOptions _options;
    private DateTimeOffset? _pressedAt;
    private DateTimeOffset? _previewUntil;
    private DateTimeOffset? _messageUntil;
    private bool _speechDetected;
    private bool _holdDeclared;

    public PttStateMachine(PttOptions options, bool voiceEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        VoiceEnabled = voiceEnabled;
    }

    /// <summary>
    /// Voice off is a real mode, not a mute over a running recognizer: key down goes straight to
    /// the menu, the microphone is never opened, and no audio buffer is allocated.
    /// </summary>
    public bool VoiceEnabled { get; set; }

    public PttState State { get; private set; } = PttState.Idle;

    /// <summary>The draft waiting for a point, a preview, or nothing.</summary>
    public Draft? PendingDraft { get; private set; }

    /// <summary>
    /// The coordinate taken at key DOWN for the current or last press. Never resampled: not at key
    /// up, and not while navigating the menu.
    /// </summary>
    public MapPoint? SnapshotPoint { get; private set; }

    /// <summary>The transient overlay line, or null.</summary>
    public string? Message { get; private set; }

    public bool IsPressed => _pressedAt is not null;

    /// <summary>
    /// Key down. <paramref name="point"/> is what the coordinate sources answered at this instant,
    /// and it is the only coordinate this press will ever use.
    /// </summary>
    public IReadOnlyList<PttEffect> KeyDown(MapPoint? point, DateTimeOffset now)
    {
        _pressedAt = now;
        _speechDetected = false;
        _holdDeclared = false;
        _previewUntil = null;
        SnapshotPoint = point;

        var effects = new List<PttEffect>();
        if (VoiceEnabled)
        {
            State = PttState.Capturing;
            effects.Add(new PttEffect(PttEffectKind.StartAudioCapture));
        }
        else
        {
            State = PttState.Menu;
            effects.Add(new PttEffect(PttEffectKind.OpenMenu));
        }

        return effects;
    }

    /// <summary>Speech energy crossed the noise floor. Cancels the hesitation menu.</summary>
    public IReadOnlyList<PttEffect> SpeechDetected(DateTimeOffset now)
    {
        _ = now;
        if (State == PttState.Capturing)
        {
            _speechDetected = true;
        }

        return None;
    }

    /// <summary>Key up. Shorter than tap_max_ms with no speech is a tap; anything else is a hold.</summary>
    public IReadOnlyList<PttEffect> KeyUp(DateTimeOffset now)
    {
        if (_pressedAt is not { } pressed)
        {
            return None;
        }

        var held = now - pressed;
        _pressedAt = null;

        var effects = new List<PttEffect>();
        if (State == PttState.Capturing)
        {
            effects.Add(new PttEffect(PttEffectKind.StopAudioCapture));
        }

        var isTap = held.TotalMilliseconds < _options.TapMaxMs && !_speechDetected;
        return isTap ? Tap(effects, now) : Hold(effects, now);
    }

    /// <summary>
    /// The parse landed. A request previews then commits; a command executes immediately; a near
    /// tie opens a two-item menu; anything else shows the transcript with a '?' and sends nothing.
    /// </summary>
    /// <param name="result">What the parser made of the utterance.</param>
    /// <param name="draft">
    /// The draft the host built from the catalog for a <see cref="ParsedRequest"/>. Its first point
    /// is filled from the key-down snapshot when the parse carried no spoken grid.
    /// </param>
    /// <param name="now">Local clock.</param>
    public IReadOnlyList<PttEffect> IntentRecognized(ParseResult result, Draft? draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);

        var effects = new List<PttEffect>();
        switch (result)
        {
            case ParsedRequest:
                ArgumentNullException.ThrowIfNull(draft);
                return BeginDraft(draft, effects, now);

            case ParsedCommand command:
                // Commands never preview. 'accept 4' must be instantaneous.
                State = PttState.Idle;
                effects.Add(new PttEffect(PttEffectKind.ExecuteCommand) { Intent = command });
                return effects;

            case ParsedDisambiguation menu:
                State = PttState.Menu;
                effects.Add(new PttEffect(PttEffectKind.ShowDisambiguation) { Intent = menu });
                return effects;

            case ParsedUnrecognized unrecognized:
                State = PttState.Idle;
                Show(effects, $"? \"{unrecognized.Transcript}\"", now);
                return effects;

            case ParsedRejection rejection:
                State = PttState.Idle;
                if (!string.Equals(rejection.Reason, ParseReasons.EmptyTranscript, StringComparison.Ordinal))
                {
                    Show(effects, $"? \"{rejection.Transcript}\"", now);
                }

                return effects;

            default:
                State = PttState.Idle;
                return effects;
        }
    }

    /// <summary>
    /// Starts or continues a draft the host built. Fills point 0 from the key-down snapshot when
    /// the draft carries none.
    /// </summary>
    public IReadOnlyList<PttEffect> BeginDraft(Draft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return BeginDraft(draft, [], now);
    }

    /// <summary>Escape at any non-idle state returns to idle and discards.</summary>
    public IReadOnlyList<PttEffect> Escape(DateTimeOffset now) => Cancel(now, message: null);

    /// <summary>Losing the game window discards exactly as Escape does.</summary>
    public IReadOnlyList<PttEffect> FocusLost(DateTimeOffset now) => Cancel(now, message: null);

    /// <summary>
    /// Step 0 of the deployment hop. A second point tapped four seconds after the hop would
    /// otherwise commit a request whose first point was read off the old server's map.
    /// </summary>
    public bool AbortDraft(DateTimeOffset now)
    {
        if (PendingDraft is null)
        {
            return false;
        }

        PendingDraft = null;
        SnapshotPoint = null;
        State = PttState.Idle;
        _previewUntil = null;
        Message = DraftDiscardedMessage;
        _messageUntil = now.AddMilliseconds(_options.MessageHoldMs);
        return true;
    }

    /// <summary>Drives every timer: the hesitation menu, the preview, and the awaiting-point window.</summary>
    public IReadOnlyList<PttEffect> Tick(DateTimeOffset now)
    {
        var effects = new List<PttEffect>();

        if (Message is not null && _messageUntil is { } until && now >= until)
        {
            Message = null;
            _messageUntil = null;
            effects.Add(new PttEffect(PttEffectKind.ClearMessage));
        }

        if (_pressedAt is { } pressed)
        {
            var held = (now - pressed).TotalMilliseconds;

            if (!_holdDeclared && held >= _options.TapMaxMs)
            {
                _holdDeclared = true;

                // The press is a hold. A hold while a draft is pending discards it and starts a new
                // utterance; changing your mind mid-thought must not require Escape first.
                if (PendingDraft is not null)
                {
                    PendingDraft = null;
                    effects.Add(new PttEffect(PttEffectKind.DiscardDraft));
                }
            }

            if (State == PttState.Capturing && !_speechDetected && held >= _options.MenuHesitationMs)
            {
                State = PttState.Menu;
                effects.Add(new PttEffect(PttEffectKind.StopAudioCapture));
                effects.Add(new PttEffect(PttEffectKind.OpenMenu));
            }
        }

        if (State == PttState.Preview && _previewUntil is { } commitAt && now >= commitAt)
        {
            Commit(effects);
        }

        if (State == PttState.AwaitingPoint && PendingDraft is { } draft && draft.IsExpired(now))
        {
            PendingDraft = null;
            State = PttState.Idle;
            effects.Add(new PttEffect(PttEffectKind.DiscardDraft));
            Show(effects, NoPointMessage, now);
        }

        return effects.Count == 0 ? None : effects;
    }

    private List<PttEffect> BeginDraft(Draft draft, List<PttEffect> effects, DateTimeOffset now)
    {
        var pending = draft;
        if (pending.Points.Count == 0 && SnapshotPoint is not null)
        {
            pending = pending.WithPoint(SnapshotPoint);
        }

        pending = pending with { Deadline = now.AddSeconds(_options.AwaitingPointTimeoutS) };
        PendingDraft = pending;

        if (pending.IsComplete)
        {
            EnterPreview(effects, now);
        }
        else
        {
            State = PttState.AwaitingPoint;
        }

        return effects;
    }

    private List<PttEffect> Tap(List<PttEffect> effects, DateTimeOffset now)
    {
        if (State == PttState.Menu)
        {
            effects.Add(new PttEffect(PttEffectKind.CloseMenu));
        }

        if (PendingDraft is not { } draft)
        {
            // A tap with no pending draft does nothing.
            State = PttState.Idle;
            return effects;
        }

        if (draft.IsComplete)
        {
            // Tap to confirm a preview that is already whole.
            Commit(effects);
            return effects;
        }

        if (SnapshotPoint is null)
        {
            State = PttState.AwaitingPoint;
            return effects;
        }

        PendingDraft = draft.WithPoint(SnapshotPoint);
        if (PendingDraft.IsComplete)
        {
            EnterPreview(effects, now);
        }
        else
        {
            State = PttState.AwaitingPoint;
        }

        return effects;
    }

    private List<PttEffect> Hold(List<PttEffect> effects, DateTimeOffset now)
    {
        _ = now;

        if (PendingDraft is not null)
        {
            PendingDraft = null;
            effects.Add(new PttEffect(PttEffectKind.DiscardDraft));
        }

        if (State == PttState.Menu)
        {
            // The menu decides for itself whether a CONFIRM was reachable; the host asks it.
            effects.Add(new PttEffect(PttEffectKind.CloseMenu));
            State = PttState.Idle;
            return effects;
        }

        State = VoiceEnabled ? PttState.Recognizing : PttState.Idle;
        return effects;
    }

    private IReadOnlyList<PttEffect> Cancel(DateTimeOffset now, string? message)
    {
        if (State == PttState.Idle && PendingDraft is null)
        {
            return None;
        }

        var effects = new List<PttEffect>();
        if (State == PttState.Capturing)
        {
            effects.Add(new PttEffect(PttEffectKind.StopAudioCapture));
        }

        if (State == PttState.Menu)
        {
            effects.Add(new PttEffect(PttEffectKind.CloseMenu));
        }

        if (PendingDraft is not null)
        {
            PendingDraft = null;
            effects.Add(new PttEffect(PttEffectKind.DiscardDraft));
        }

        _pressedAt = null;
        _previewUntil = null;
        State = PttState.Idle;

        if (message is not null)
        {
            Show(effects, message, now);
        }

        return effects;
    }

    private void EnterPreview(List<PttEffect> effects, DateTimeOffset now)
    {
        State = PttState.Preview;
        var spokenGrid = PendingDraft?.Points.Any(p =>
            string.Equals(p.Source, "spoken_grid", StringComparison.Ordinal)) == true;
        var hold = spokenGrid ? _options.SpokenGridPreviewMs : _options.PreviewHoldMs;
        _previewUntil = now.AddMilliseconds(hold);
        effects.Add(new PttEffect(PttEffectKind.ShowPreview) { Draft = PendingDraft });
    }

    private void Commit(List<PttEffect> effects)
    {
        var draft = PendingDraft;
        PendingDraft = null;
        _previewUntil = null;
        State = PttState.Idle;
        effects.Add(new PttEffect(PttEffectKind.CommitRequest) { Draft = draft });
    }

    private void Show(List<PttEffect> effects, string message, DateTimeOffset now)
    {
        Message = message;
        _messageUntil = now.AddMilliseconds(_options.MessageHoldMs);
        effects.Add(new PttEffect(PttEffectKind.ShowMessage) { Message = message });
    }
}
