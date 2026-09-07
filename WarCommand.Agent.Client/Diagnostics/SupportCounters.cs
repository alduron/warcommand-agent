using System.Globalization;

namespace WarCommand.Agent.Client.Diagnostics;

/// <summary>
/// What this session actually did, tallied. The first question about any report is whether the
/// thing has ever worked on that machine, and a log of failures alone cannot answer it.
/// </summary>
/// <remarks>
/// A line per event answers "what went wrong once". These answer "how often, out of how many
/// tries", which is the difference between a machine where the readout is unreadable and one where
/// the user pressed the key twice. Value free, like everything else that reaches an export: counts
/// and refusal words, never a coordinate and never an utterance.
/// <para>
/// Written from the hook thread, the decode task and the dispatcher, so every field moves under the
/// same lock.
/// </para>
/// </remarks>
public sealed class SupportCounters
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _refusals = new(StringComparer.Ordinal);

    private int _readsAccepted;
    private int _readsRefused;
    private int _holds;
    private int _holdsSilent;
    private int _holdsWithNothingHeard;
    private int _utterances;
    private int _utterancesUnmatched;

    /// <summary>A screen read that produced a point.</summary>
    public void ScreenReadAccepted()
    {
        lock (_gate)
        {
            _readsAccepted++;
        }
    }

    /// <summary>A screen read that refused, tallied by which refusal it was.</summary>
    public void ScreenReadRefused(string? refusal)
    {
        lock (_gate)
        {
            _readsRefused++;
            var word = string.IsNullOrWhiteSpace(refusal) ? "UNKNOWN" : refusal;
            _refusals[word] = _refusals.TryGetValue(word, out var seen) ? seen + 1 : 1;
        }
    }

    /// <summary>One push-to-talk hold, once it has ended.</summary>
    /// <param name="silent">The peak never crossed the noise floor.</param>
    /// <param name="utterances">How many the recognizer completed during the hold.</param>
    public void VoiceHold(bool silent, int utterances)
    {
        lock (_gate)
        {
            _holds++;

            if (silent)
            {
                _holdsSilent++;
            }

            if (utterances == 0)
            {
                _holdsWithNothingHeard++;
            }
        }
    }

    /// <summary>One completed utterance and whether the parser made anything of it.</summary>
    public void Utterance(bool matched)
    {
        lock (_gate)
        {
            _utterances++;

            if (!matched)
            {
                _utterancesUnmatched++;
            }
        }
    }

    /// <summary>The screen-read tally for the export, or null when nothing was ever read.</summary>
    public string? Reads()
    {
        lock (_gate)
        {
            if (_readsAccepted + _readsRefused == 0)
            {
                return null;
            }

            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"{_readsAccepted + _readsRefused} attempted, {_readsAccepted} read, {_readsRefused} refused");

            return _refusals.Count == 0
                ? text
                : text + " (" + string.Join(", ", _refusals
                    .OrderByDescending(r => r.Value)
                    .Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Key} x{r.Value}"))) + ")";
        }
    }

    /// <summary>The voice tally for the export, or null when nobody ever held the key.</summary>
    public string? Holds()
    {
        lock (_gate)
        {
            return _holds == 0
                ? null
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_holds} held, {_holdsSilent} silent, {_holdsWithNothingHeard} heard nothing, {_utterances} utterances, {_utterancesUnmatched} unmatched");
        }
    }
}
