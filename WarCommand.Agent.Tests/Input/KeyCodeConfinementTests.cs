using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WarCommand.Agent.Input;
using WarCommand.Agent.Input.Bindings;

namespace WarCommand.Agent.Tests.Input;

/// <summary>
/// Never log a key code, at any log level, in any build. A keylogger with a bug is indistinguishable
/// from a keylogger, so the rule gets a mechanism rather than a code review comment. These tests
/// assert the mechanism, not the current call sites.
/// </summary>
public class KeyCodeConfinementTests
{
    /// <summary>Identifiers that hold a raw code somewhere in the assembly.</summary>
    private static readonly string[] RawCodeIdentifiers =
        ["virtualKey", "vkCode", "scanCode", "keyCode", "mouseData", "nCode", "wParam", "lParam", "code", "_code", "button"];

    /// <summary>Anything that turns a value into text or hands it to an output.</summary>
    private static readonly string[] TextSinks =
        ["$\"", "string.Format", "String.Format", "AppendFormat", "Console.", "Trace.", "Debug.Write", "File.Append", "StreamWriter"];

    [Fact]
    public void No_virtual_key_code_has_a_string_form_outside_the_fixed_label_set()
    {
        var labels = BindingKey.Labels.ToHashSet(StringComparer.Ordinal);

        for (var code = 0; code <= ushort.MaxValue; code++)
        {
            if (!BindingKey.TryFromVirtualKey(code, out var key))
            {
                continue;
            }

            Assert.Contains(key.Label, labels);
            Assert.Contains(key.ToString(), labels);
        }
    }

    [Fact]
    public void An_unlabelled_code_is_not_representable_at_all()
    {
        // 0xFF is a valid virtual key and carries no label, so it cannot become a binding and there
        // is nothing to print.
        Assert.False(BindingKey.TryFromVirtualKey(0xFF, out _));
        Assert.False(BindingKey.TryFromVirtualKey(0x01, out _));
        Assert.Equal("(unbound)", BindingKey.Unbound.ToString());
    }

    [Fact]
    public void BindingKey_exposes_no_numeric_reading_of_the_code()
    {
        var type = typeof(BindingKey);

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.True(field.IsPrivate, $"{field.Name} is not private");
        }

        var leaks = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.IsPublic || m.IsAssembly || m.IsFamily)
            .Where(m => IsNumeric(m.ReturnType) && m.Name != nameof(BindingKey.GetHashCode))
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(leaks);

        // The hash is scrambled, so it is not a second reading of the code either.
        Assert.True(BindingKey.TryFromVirtualKey(0x41, out var a));
        Assert.NotEqual(0x41, a.GetHashCode());
    }

    [Fact]
    public void The_log_channel_cannot_carry_a_key_code()
    {
        foreach (var method in typeof(IInputLog).GetMethods())
        {
            foreach (var parameter in method.GetParameters())
            {
                Assert.True(
                    parameter.ParameterType.IsEnum,
                    $"IInputLog.{method.Name} takes {parameter.ParameterType.Name}, which a key code could ride in on");
            }
        }
    }

    [Fact]
    public void No_source_line_formats_a_raw_code()
    {
        var violations = new List<string>();

        foreach (var file in InputSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TextSinks.Any(sink => line.Contains(sink, StringComparison.Ordinal)))
                {
                    continue;
                }

                foreach (var identifier in RawCodeIdentifiers)
                {
                    if (Regex.IsMatch(line, $@"\b{Regex.Escape(identifier)}\b"))
                    {
                        violations.Add($"{Path.GetFileName(file)}:{i + 1} formats '{identifier}'");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "A key code must never reach a string.\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Every_hook_is_installed_with_a_zero_module_handle()
    {
        var installs = 0;

        foreach (var file in InputSourceFiles())
        {
            foreach (Match call in Regex.Matches(File.ReadAllText(file), @"NativeMethods\.SetWindowsHookEx\((?<args>[^)]*)\)"))
            {
                installs++;
                var args = call.Groups["args"].Value;
                Assert.Contains("IntPtr.Zero", args, StringComparison.Ordinal);
                Assert.DoesNotContain("GetModuleHandle", args, StringComparison.Ordinal);
            }
        }

        // A non-zero module handle is a hook into a foreign module. Both hooks, keyboard and mouse.
        Assert.Equal(2, installs);
    }

    [Fact]
    public void The_input_assembly_holds_no_free_form_logging_call()
    {
        var banned = new[] { @"\bConsole\.", @"\bTrace\.", @"\bDebug\.Write", @"\bILogger\b", @"\bLogInformation\b", @"\bLogDebug\b" };
        var violations = new List<string>();

        foreach (var file in InputSourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in banned)
            {
                if (Regex.IsMatch(text, pattern))
                {
                    violations.Add($"{Path.GetFileName(file)} matches {pattern}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "IInputLog is the only channel out of this assembly, and it takes an enum.\n"
            + string.Join("\n", violations));
    }

    private static bool IsNumeric(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);

    private static IEnumerable<string> InputSourceFiles()
    {
        var root = Path.Combine(SolutionRoot(), "WarCommand.Agent.Input");
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string SolutionRoot([CallerFilePath] string sourcePath = "") =>
        WalkUp(AppContext.BaseDirectory)
        ?? WalkUp(Path.GetDirectoryName(sourcePath))
        ?? throw new InvalidOperationException("solution root not found");

    private static string? WalkUp(string? from)
    {
        var dir = string.IsNullOrEmpty(from) ? null : new DirectoryInfo(from);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WarCommand.Agent.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}
