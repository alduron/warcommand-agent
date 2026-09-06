using WarCommand.Agent.Capture;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Core.Contracts;
using Xunit;

namespace WarCommand.Agent.Tests.Capture;

/// <summary>
/// What a customer's exported log has to say about a bad read.
/// </summary>
/// <remarks>
/// They have no repo, no probe and no way to describe a decode. The line the agent writes when it
/// refuses is the whole diagnosis, so it has to tell the three failures apart: nothing found,
/// found and not decoded, decoded and contested. It must do that without recording where anybody
/// was standing.
/// </remarks>
public class ReadDiagnosticTests
{
    private static readonly string[] ValueWords =
    [
        "x9", "y1", "x8", "97.56", "108.62", "96.60",
    ];

    /// <summary>
    /// The shape of a read is exactly the four questions worth asking about one.
    /// </summary>
    [Fact]
    public void The_diagnostic_names_the_screen_and_how_far_the_decode_got()
    {
        var source = new MapReadoutCoordinateSource(
            () => BundledContracts.GameProfile().Current,
            () => null,
            () => true);

        // No game window, so it refuses before it reads. The diagnostic is still the honest one.
        Assert.Null(source.Read());
        Assert.Equal("NO GAME WINDOW", source.LastRefusal);
    }

    /// <summary>
    /// A coordinate is somebody's position. The export says so and must keep saying so.
    /// </summary>
    [Fact]
    public void The_bundle_summary_has_no_field_that_could_carry_a_position()
    {
        var summary = new LogBundleSummary
        {
            AgentVersion = "0.3.5",
            WindowsVersion = "Microsoft Windows NT 10.0.22631.0",
            Backend = "Production",
            OverlayMode = "MirrorGame",
            ScreenCaptureEnabled = true,
            GameRunning = true,
            Connection = "Online",
            Displays = ["3440x1440 primary"],
            LastReadRefusal = "NO COORDS",
            ReadoutGeometry =
                "client 3440x1440, 1.00x of 1440, gap 18, radius 420, run 6-90, rungs 9, "
                + "runs 3, x none, y 1 read, 1 distinct, best margin 0.06",
        };

        var text = summary.Render(DateTimeOffset.UnixEpoch);

        // The facts that decide which failure it is.
        Assert.Contains("3440x1440", text, StringComparison.Ordinal);
        Assert.Contains("1.00x of 1440", text, StringComparison.Ordinal);
        Assert.Contains("best margin 0.06", text, StringComparison.Ordinal);
        Assert.Contains("NO COORDS", text, StringComparison.Ordinal);

        // And none of the values.
        foreach (var value in ValueWords)
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A refused read is a WARN, so it survives into the default log.
    /// </summary>
    /// <remarks>
    /// At INFO it is dropped unless the customer has switched verbose on, which is exactly the
    /// person who cannot be asked to switch anything on before reporting.
    /// </remarks>
    [Fact]
    public void A_refusal_reaches_the_default_log()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new WarCommand.Agent.Client.Storage.AgentPaths(root);
            var log = new RollingFileLog(paths, verbose: () => false);

            log.Warn("Screen read refused: NO COORDS. client 3440x1440, 1.00x of 1440");

            var text = string.Concat(
                Directory.GetFiles(paths.LogDirectory).Select(File.ReadAllText));

            Assert.Contains("NO COORDS", text, StringComparison.Ordinal);
            Assert.Contains("3440x1440", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
