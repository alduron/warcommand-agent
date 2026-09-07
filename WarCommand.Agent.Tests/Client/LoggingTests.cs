using System.IO.Compression;
using WarCommand.Agent.Client.Diagnostics;
using WarCommand.Agent.Client.Storage;
using Xunit;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// The shipped sink. A log nobody can send is the same as no log, so the caps are the feature.
/// </summary>
public class RollingFileLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wc-log-" + Guid.NewGuid().ToString("N"));

    private AgentPaths Paths => new(_root);

    [Fact]
    public void A_fault_is_written_and_an_info_line_is_not()
    {
        var log = new RollingFileLog(Paths);

        log.Info("routine");
        log.Warn("something odd");
        log.Error("something broke", new InvalidOperationException("the cause"));

        var text = AllText();
        Assert.DoesNotContain("routine", text, StringComparison.Ordinal);
        Assert.Contains("something odd", text, StringComparison.Ordinal);
        Assert.Contains("something broke", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("the cause", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unobserved task exception reaches the log as an AggregateException the runtime never
    /// threw, so its own stack is null. Logging only the outer stack put a cross-thread WPF
    /// failure in the file every day with nothing naming the call site.
    /// </summary>
    [Fact]
    public void An_aggregate_with_no_stack_of_its_own_still_logs_the_causes()
    {
        var log = new RollingFileLog(Paths);

        Exception cause;
        try
        {
            throw new InvalidOperationException("the calling thread cannot access this object");
        }
        catch (InvalidOperationException ex)
        {
            cause = ex;
        }

        log.Error("Unobserved task exception.", new AggregateException(cause));

        var text = AllText();
        Assert.Contains("the calling thread cannot access this object", text, StringComparison.Ordinal);
        Assert.Contains(nameof(An_aggregate_with_no_stack_of_its_own_still_logs_the_causes), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Verbose_is_read_per_line_so_the_switch_needs_no_restart()
    {
        var verbose = false;
        var log = new RollingFileLog(Paths, () => verbose);

        log.Info("before");
        verbose = true;
        log.Info("after");

        var text = AllText();
        Assert.DoesNotContain("before", text, StringComparison.Ordinal);
        Assert.Contains("after", text, StringComparison.Ordinal);
    }

    [Fact]
    public void One_file_rolls_at_its_ceiling()
    {
        var log = new RollingFileLog(Paths, maxFileBytes: 512, maxTotalBytes: 1024 * 1024);

        for (var i = 0; i < 40; i++)
        {
            log.Warn(new string('x', 60));
        }

        Assert.True(log.Files().Count > 1, "a run past the file ceiling has to roll");
    }

    /// <summary>The cap is what keeps a bundle attachable. 10 MB of logs is 10 MB nobody sends.</summary>
    [Fact]
    public void The_directory_never_grows_past_its_total()
    {
        var log = new RollingFileLog(Paths, maxFileBytes: 512, maxTotalBytes: 2048);

        for (var i = 0; i < 400; i++)
        {
            log.Warn(new string('x', 60));
        }

        // One file over is the live one, which is never pruned under itself.
        Assert.True(
            log.TotalBytes() <= 2048 + 512,
            $"the log directory grew to {log.TotalBytes()} bytes past a 2048 cap");
    }

    [Fact]
    public void A_file_older_than_the_retention_is_deleted()
    {
        var old = Path.Combine(Paths.LogDirectory, RollingFileLog.Prefix + "20200101-00.log");
        Directory.CreateDirectory(Paths.LogDirectory);
        File.WriteAllText(old, "ancient");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        _ = new RollingFileLog(Paths, retentionDays: 7);

        Assert.False(File.Exists(old), "a file past the retention window has to go");
    }

    private string AllText() =>
        string.Concat(Directory.GetFiles(Paths.LogDirectory).Select(File.ReadAllText));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>
/// The export. Everything in it is about to be handed to other people, so what it may contain is
/// the test.
/// </summary>
public class LogBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wc-bundle-" + Guid.NewGuid().ToString("N"));

    private AgentPaths Paths => new(_root);

    private static LogBundleSummary Summary => new()
    {
        AgentVersion = "0.3.2",
        WindowsVersion = "Microsoft Windows NT 10.0.22631.0",
        Backend = "Production",
        OverlayMode = "MirrorGame",
        ScreenCaptureEnabled = true,
        GameRunning = true,
        Connection = "Online",
        Displays = ["1920x1080 primary", "2560x1440"],
        LastReadRefusal = "AMBIGUOUS READ",
    };

    [Fact]
    public void The_bundle_holds_the_logs_and_a_summary()
    {
        var log = new RollingFileLog(Paths);
        log.Error("something broke", new InvalidOperationException("the cause"));

        var zipPath = LogBundle.Write(Paths, Destination, Summary);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Contains(zip.Entries, e => e.FullName == "summary.txt");
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith(RollingFileLog.Prefix, StringComparison.Ordinal));
    }

    /// <summary>The live file is open for append in this process. CreateEntryFromFile throws on it.</summary>
    [Fact]
    public void Exporting_while_the_log_is_open_works()
    {
        var log = new RollingFileLog(Paths);
        log.Warn("first");

        var zipPath = LogBundle.Write(Paths, Destination, Summary);
        log.Warn("second");

        Assert.True(File.Exists(zipPath));
    }

    [Fact]
    public void The_summary_names_what_a_fault_needs_and_nothing_else()
    {
        var zipPath = LogBundle.Write(Paths, Destination, Summary);

        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry("summary.txt")!.Open());
        var text = reader.ReadToEnd();

        Assert.Contains("0.3.2", text, StringComparison.Ordinal);
        Assert.Contains("Production", text, StringComparison.Ordinal);
        Assert.Contains("MirrorGame", text, StringComparison.Ordinal);
        Assert.Contains("2560x1440", text, StringComparison.Ordinal);
        Assert.Contains("AMBIGUOUS READ", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Binding rule 4 and 12-security.md rule 3: no key code reaches a log at any level, in any
    /// build. The summary is a record with no field that could carry one, and this is the guard on
    /// somebody adding one.
    /// </summary>
    [Fact]
    public void The_summary_type_has_no_field_for_a_secret_or_a_key()
    {
        var forbidden = new[] { "token", "key", "chord", "binding", "callsign", "coord", "password", "secret" };

        foreach (var property in typeof(LogBundleSummary).GetProperties())
        {
            foreach (var word in forbidden)
            {
                Assert.False(
                    property.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"LogBundleSummary.{property.Name} would put a {word} in a file people pass around");
            }
        }
    }

    private string Destination => Path.Combine(_root, "out");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
