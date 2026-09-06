using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;
using WarCommand.Agent.Input.Hooks;
using Xunit;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// One dispatch per physical press, however long the key is held.
/// </summary>
/// <remarks>
/// Reported: role removals failing from the overlay's ROLES panel. The cause was here. Windows
/// delivers auto-repeat as a stream of ordinary WM_KEYDOWNs with nothing on them to say so, about
/// thirty a second once the repeat delay elapses, and every one reached the menu as another press.
/// Holding a digit on ROLES therefore fired a toggle POST per repeat: the role ended wherever the
/// last one left it, and the burst tripped the API rate limit, which the overlay reported as
/// ROLE REFUSED. Every digit on every panel had the same problem, and so did every nav key.
/// </remarks>
public class AutoRepeatIsNotASecondPressTests
{
    /// <summary>B under RightAlt, which is a bound chord in the fixture set.</summary>
    private const int RightAlt = 0xA5;
    private const int B = 0x42;

    [Fact]
    public void A_held_key_dispatches_once_however_many_downs_arrive()
    {
        var (hook, chords) = Build();

        Assert.Equal(HookVerdict.PassThrough, hook.Evaluate(RightAlt, KeyTransition.Down));
        Assert.Equal(HookVerdict.Swallow, hook.Evaluate(B, KeyTransition.Down));

        // The repeats. Thirty of them is one second of holding the key.
        for (var repeat = 0; repeat < 30; repeat++)
        {
            hook.Evaluate(B, KeyTransition.Down);
        }

        Assert.Equal(1, chords.Count);
    }

    /// <summary>
    /// A repeat of a swallowed press stays swallowed.
    /// </summary>
    /// <remarks>
    /// Suppressing the dispatch must not start leaking the key to the game halfway through a hold.
    /// The press was taken; every edge of it belongs to WarCommand until the release.
    /// </remarks>
    [Fact]
    public void A_repeat_of_a_swallowed_press_is_still_swallowed()
    {
        var (hook, _) = Build();

        hook.Evaluate(RightAlt, KeyTransition.Down);
        Assert.Equal(HookVerdict.Swallow, hook.Evaluate(B, KeyTransition.Down));
        Assert.Equal(HookVerdict.Swallow, hook.Evaluate(B, KeyTransition.Down));
        Assert.Equal(HookVerdict.Swallow, hook.Evaluate(B, KeyTransition.Down));
    }

    /// <summary>Releasing and pressing again is two presses, which is the point of the release.</summary>
    [Fact]
    public void A_release_makes_the_next_down_a_new_press()
    {
        var (hook, chords) = Build();

        hook.Evaluate(RightAlt, KeyTransition.Down);
        hook.Evaluate(B, KeyTransition.Down);
        hook.Evaluate(B, KeyTransition.Down);
        Assert.Equal(1, chords.Count);

        hook.Evaluate(B, KeyTransition.Up);
        hook.Evaluate(B, KeyTransition.Down);

        Assert.Equal(2, chords.Count);
    }

    /// <summary>
    /// The hook can stop seeing releases, so forgetting the held set must forget the pressed keys
    /// too, or the key that was down when it stopped looking is never pressable again.
    /// </summary>
    [Fact]
    public void Forgetting_the_modifiers_forgets_what_was_held()
    {
        var (hook, chords) = Build();

        hook.Evaluate(RightAlt, KeyTransition.Down);
        hook.Evaluate(B, KeyTransition.Down);
        Assert.Equal(1, chords.Count);

        hook.ForgetModifiers();

        hook.Evaluate(RightAlt, KeyTransition.Down);
        hook.Evaluate(B, KeyTransition.Down);

        Assert.Equal(2, chords.Count);
    }

    private static (LowLevelKeyboardHook Hook, CountingChords Chords) Build()
    {
        var bridge = new InputBridge(gameForeground: true, gameRunning: true);
        var chords = new CountingChords();
        bridge.Connect(null, null, chords, null);
        return (new LowLevelKeyboardHook(bridge), chords);
    }

    private sealed class CountingChords : IChordSink
    {
        internal int Count { get; private set; }

        public void Invoke(BindingAction action) => Count++;
    }
}
