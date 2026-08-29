using System.Text.RegularExpressions;

namespace WarCommand.Agent.Tests;

/// <summary>
/// Fails the build if anything in the solution reaches into the game process.
/// Rule 1 in ../CLAUDE.md. A comment is not enforcement.
/// </summary>
public class ArchitectureTests
{
    private static readonly (string Pattern, string Why)[] Forbidden =
    [
        (@"\bReadProcessMemory\b",   "process memory read"),
        (@"\bWriteProcessMemory\b",  "process memory write"),
        (@"\bVirtualAllocEx\b",      "remote allocation"),
        (@"\bCreateRemoteThread\b",  "remote thread injection"),
        (@"\bNtCreateThreadEx\b",    "remote thread injection"),
        (@"\bSendInput\b",           "input synthesis"),
        (@"\bkeybd_event\b",         "input synthesis"),
        (@"\bmouse_event\b",         "input synthesis"),
        (@"\bSetWindowsHookEx\w*\s*\([^)]*hMod\s*:\s*(?!IntPtr\.Zero)", "hook into a foreign module"),
        (@"\bIDXGISwapChain\b",      "Present hook"),
        (@"\bOverwolf\b",            "injecting overlay SDK"),
    ];

    private static readonly string[] AllowedInThisFile = ["ArchitectureTests.cs"];

    [Fact]
    public void No_source_file_reaches_into_the_game_process()
    {
        var root = SolutionRoot();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (AllowedInThisFile.Contains(Path.GetFileName(file))) continue;

            var text = File.ReadAllText(file);
            foreach (var (pattern, why) in Forbidden)
            {
                if (Regex.IsMatch(text, pattern))
                {
                    violations.Add($"{Path.GetRelativePath(root, file)}: {why} ({pattern})");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Wardogs ships kernel-level EAC. Nothing may enter the game process.\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void Core_has_no_platform_dependency()
    {
        var core = Path.Combine(SolutionRoot(), "WarCommand.Agent.Core");
        var banned = new[] { "System.Windows", "System.Net.Http", "Windows.Graphics", "System.Runtime.InteropServices" };
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (var ns in banned)
            {
                if (Regex.IsMatch(File.ReadAllText(file), $@"^\s*using\s+{Regex.Escape(ns)}", RegexOptions.Multiline))
                {
                    violations.Add($"{Path.GetFileName(file)} imports {ns}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Core holds the board, slot allocator, PTT state machine and parser, and must stay testable "
            + "with no window, no microphone and no server.\n" + string.Join("\n", violations));
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WarCommand.Agent.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }
}
