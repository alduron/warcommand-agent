using System.Globalization;
using WarCommand.Agent.Client.Storage;

namespace WarCommand.Agent.Client.Diagnostics;

/// <summary>
/// The shipped log sink: one file per day under <see cref="AgentPaths.LogDirectory"/>, rolled at a
/// size, pruned by age and by total bytes. Never handed a token, a ticket, a device token, a pairing
/// code or a key code, per <see cref="IClientLog"/>'s own contract.
/// </summary>
/// <remarks>
/// 10-agent-spec.md asks for rolling, 7 days, 10 MB, and until now the only sink was a dev-only
/// appender that grew without limit. A log nobody can send is the same as no log: the whole point is
/// that a squad of people can hand one over after something went wrong, so it has to stay small
/// enough to attach and old enough to still hold the incident.
/// <para>
/// Info is dropped unless the caller asks for verbose. Warnings and errors are always kept, so the
/// default file is what went wrong and nothing else.
/// </para>
/// </remarks>
public sealed class RollingFileLog : IClientLog
{
    /// <summary>Files are named so an ordinal sort is a chronological one.</summary>
    internal const string Prefix = "agent-";

    private readonly string _directory;
    private readonly Func<bool> _verbose;
    private readonly long _maxFileBytes;
    private readonly long _maxTotalBytes;
    private readonly int _retentionDays;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private string? _current;

    /// <param name="paths">Where the agent keeps its files.</param>
    /// <param name="verbose">Read per write, so the setting takes effect without a restart.</param>
    /// <param name="maxFileBytes">One file's ceiling. Smaller than the total, or a day fills it.</param>
    /// <param name="maxTotalBytes">The whole directory's ceiling.</param>
    /// <param name="retentionDays">Files older than this are deleted whatever the total is.</param>
    /// <param name="clock">Injected for tests. Real callers leave it.</param>
    public RollingFileLog(
        AgentPaths paths,
        Func<bool>? verbose = null,
        long maxFileBytes = 2L * 1024 * 1024,
        long maxTotalBytes = 10L * 1024 * 1024,
        int retentionDays = 7,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _directory = paths.LogDirectory;
        _verbose = verbose ?? (() => false);
        _maxFileBytes = maxFileBytes;
        _maxTotalBytes = maxTotalBytes;
        _retentionDays = retentionDays;
        _clock = clock ?? (() => DateTimeOffset.Now);

        Directory.CreateDirectory(_directory);
        Prune();
    }

    /// <summary>Kept only when verbose is on.</summary>
    public void Info(string message)
    {
        if (_verbose())
        {
            Write("INFO", message);
        }
    }

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? error = null) =>
        Write("ERROR", error is null ? message : $"{message} :: {Describe(error)}");

    /// <summary>The log files, oldest first. The export bundles exactly these.</summary>
    public IReadOnlyList<string> Files() => Existing();

    /// <summary>Total bytes on disk, for the settings window to name.</summary>
    public long TotalBytes()
    {
        long total = 0;
        foreach (var file in Existing())
        {
            total += Length(file);
        }

        return total;
    }

    /// <summary>
    /// Type, message and stack, innermost cause included. The stack is the reason a log is worth
    /// collecting at all; a message alone rarely says which of four call sites threw.
    /// </summary>
    private static string Describe(Exception error)
    {
        var text = $"{error.GetType().Name}: {error.Message}";

        if (error.InnerException is { } inner)
        {
            text += $" <- {inner.GetType().Name}: {inner.Message}";
        }

        // The stack of whichever exception in the chain has one, innermost first. An unobserved
        // task exception arrives as an AggregateException the runtime never threw, so its own
        // stack is null: taking only the outer one logged a cross-thread WPF failure every day
        // for a week with nothing naming the call site, and it could not be found from the file.
        return Stack(error) is { } stack ? text + Environment.NewLine + stack : text;
    }

    /// <summary>The innermost stack in the chain, or null when nothing in it carries one.</summary>
    private static string? Stack(Exception error)
    {
        string? found = null;

        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (var one in aggregate.InnerExceptions)
                {
                    found = Stack(one) ?? found;
                }
            }

            if (current.StackTrace is { Length: > 0 } stack)
            {
                found = stack;
            }
        }

        return found;
    }

    private void Write(string level, string message)
    {
        var at = _clock();
        var line = FormattableString.Invariant(
            $"{at.ToString("yyyy-MM-dd HH:mm:ss.fff K", CultureInfo.InvariantCulture)} [{level}] {message}");

        lock (_gate)
        {
            try
            {
                var path = CurrentFile(at, line.Length);
                File.AppendAllLines(path, [line]);
            }
            catch (IOException)
            {
                // A log that throws is worse than a log that misses a line: this is called from
                // catch blocks, and from the hook thread, and nothing above it can do anything
                // useful with a second failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        System.Diagnostics.Debug.WriteLine(line);
    }

    /// <summary>Today's file, rolled to the next sequence when this line would overflow it.</summary>
    private string CurrentFile(DateTimeOffset at, int lineLength)
    {
        var stamp = at.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        if (_current is { } open
            && Path.GetFileName(open).StartsWith(Prefix + stamp, StringComparison.Ordinal)
            && Length(open) + lineLength < _maxFileBytes)
        {
            return open;
        }

        for (var sequence = 0; ; sequence++)
        {
            var candidate = Path.Combine(
                _directory,
                FormattableString.Invariant($"{Prefix}{stamp}-{sequence:00}.log"));

            if (Length(candidate) + lineLength < _maxFileBytes)
            {
                if (!string.Equals(candidate, _current, StringComparison.Ordinal))
                {
                    _current = candidate;
                    Prune();
                }

                return candidate;
            }
        }
    }

    /// <summary>Age first, then total size, oldest first. Never touches the file being written.</summary>
    private void Prune()
    {
        var cutoff = _clock().AddDays(-_retentionDays);
        var files = Existing();
        var live = new List<string>();

        foreach (var file in files)
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime
                && !string.Equals(file, _current, StringComparison.Ordinal))
            {
                Delete(file);
                continue;
            }

            live.Add(file);
        }

        long total = 0;
        foreach (var file in live)
        {
            total += Length(file);
        }

        foreach (var file in live)
        {
            if (total <= _maxTotalBytes)
            {
                return;
            }

            if (string.Equals(file, _current, StringComparison.Ordinal))
            {
                continue;
            }

            total -= Length(file);
            Delete(file);
        }
    }

    private List<string> Existing()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var files = new List<string>(Directory.GetFiles(_directory, Prefix + "*.log"));
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static long Length(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
