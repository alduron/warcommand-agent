using System.Runtime.CompilerServices;

namespace WarCommand.Agent.Tests.Architecture;

/// <summary>
/// Nothing that reads input, audio or the screen is constructed before an account exists.
/// See Convention_WarCommandAgentArmsNothingWithoutAnAccount and 10-agent-spec.md step 6.
/// </summary>
/// <remarks>
/// A source test rather than a behavioural one: the composition root is a WPF Application whose
/// startup cannot be driven from a test, and the ordering inside it is exactly what shipped wrong.
/// </remarks>
public class StartupArmingTests
{
    /// <summary>Every construction that arms a surface capable of accepting or reading input.</summary>
    private static readonly string[] ArmingConstructions =
    [
        "InputComposition.Start",
        "new Composition.VoiceDriver",
        "BuildMenu(",
        "MapReadoutCoordinateSource",
        "StartBoardTick()",
    ];

    [Fact]
    public void StartOverlay_arms_nothing()
    {
        var body = MethodBody(AppSource(), "private void StartOverlay(");

        foreach (var construction in ArmingConstructions)
        {
            Assert.DoesNotContain(construction, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArmForAccount_is_where_every_arming_construction_lives()
    {
        var body = MethodBody(AppSource(), "private void ArmForAccount(");

        foreach (var construction in ArmingConstructions)
        {
            Assert.Contains(construction, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArmForAccount_refuses_a_guest_before_it_builds_anything()
    {
        var body = MethodBody(AppSource(), "private void ArmForAccount(");

        var guard = body.IndexOf("_hasProviderAccount", StringComparison.Ordinal);
        Assert.True(guard >= 0, "The provider check is gone: a cold-start guest would arm.");

        // Every arming construction must sit AFTER the refusal, or the refusal is decorative.
        foreach (var construction in ArmingConstructions)
        {
            var at = body.IndexOf(construction, StringComparison.Ordinal);
            Assert.True(at > guard, $"{construction} is built before the provider check.");
        }
    }

    [Fact]
    public void A_guest_is_never_treated_as_a_provider_account()
    {
        var source = AppSource();

        // The flag is set in exactly one place, from the wire field, and nowhere else.
        Assert.Contains("_hasProviderAccount = me.User.AuthProvider", source, StringComparison.Ordinal);
        Assert.Equal(1, source.Split("_hasProviderAccount =").Length - 1);
    }

    [Fact]
    public void The_startup_path_places_the_overlay_and_never_arms_it()
    {
        var source = AppSource();

        Assert.Contains("StartOverlay(presenter, log);", source, StringComparison.Ordinal);

        // Arming is reached from authentication and from a relink, and from nowhere else.
        var armCalls = source.Split("ArmForAccount(presenter, log);").Length - 1;
        Assert.Equal(2, armCalls);
    }

    private static string AppSource() => File.ReadAllText(AppSourcePath());

    /// <summary>The composition root, located from this file rather than from a build output.</summary>
    private static string AppSourcePath([CallerFilePath] string here = "")
    {
        var tests = Path.GetDirectoryName(Path.GetDirectoryName(here))!;
        var repo = Path.GetDirectoryName(tests)!;
        return Path.Combine(repo, "WarCommand.Agent", "App.xaml.cs");
    }

    /// <summary>The text between a signature's opening brace and its matching close.</summary>
    private static string MethodBody(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{signature} is gone. The rule outlived the method name.");

        var open = source.IndexOf('{', at);
        Assert.True(open >= 0);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..i];
                }
            }
        }

        throw new InvalidOperationException($"{signature} has no matching brace.");
    }
}
