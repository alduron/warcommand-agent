using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// Reader for warcommand-api/tests/unit/fixtures/utterances.yaml, the parse spec both suites read.
/// Per Convention_WarCommandUtteranceFixtureIsSharedByBothSuites the file is never copied into this
/// repo: a second copy diverges on the first edit and the Python side does not notice.
/// </summary>
internal static class UtteranceFixture
{
    private static readonly Lazy<IReadOnlyList<UtteranceCase>> LazyCases = new(Load);

    /// <summary>Every row, in file order.</summary>
    public static IReadOnlyList<UtteranceCase> Cases => LazyCases.Value;

    /// <summary>One xUnit theory row per fixture row.</summary>
    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var id in Cases.Select(c => c.Id))
        {
            data.Add(id);
        }

        return data;
    }

    public static UtteranceCase Case(string id) =>
        Cases.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"utterances.yaml has no case '{id}'");

    private static List<UtteranceCase> Load()
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var document = deserializer.Deserialize<UtteranceDocument>(ContractFixtures.UtterancesYaml);
        if (document?.Cases is not { Count: > 0 })
        {
            throw new InvalidOperationException("utterances.yaml carries no cases");
        }

        return document.Cases;
    }
}

internal sealed class UtteranceDocument
{
    public List<UtteranceCase> Cases { get; set; } = [];
}

/// <summary>One fixture row. <c>Id</c> is stable and is what a failure message cites.</summary>
internal sealed class UtteranceCase
{
    public string Id { get; set; } = string.Empty;

    public string Said { get; set; } = string.Empty;

    public string? Source { get; set; }

    public string? Note { get; set; }

    public UtteranceExpectation Expect { get; set; } = new();

    public override string ToString() => Id;
}

/// <summary>The expected parse. Absent keys are null and are asserted as null.</summary>
internal sealed class UtteranceExpectation
{
    public string Mode { get; set; } = string.Empty;

    public string? Type { get; set; }

    public List<UtterancePoint> Points { get; set; } = [];

    public UtteranceAwaitingPoint? AwaitingPoint { get; set; }

    public List<string> Modifiers { get; set; } = [];

    public string? Priority { get; set; }

    public string? SupplyKind { get; set; }

    public string? StructureKind { get; set; }

    public UtteranceQuantity? Quantity { get; set; }

    public string? Verb { get; set; }

    public string? SlotRef { get; set; }

    public string? Direction { get; set; }

    public int? Metres { get; set; }

    public string? Role { get; set; }

    public string? InviteCode { get; set; }

    public string? Action { get; set; }

    public string? Prompt { get; set; }

    public List<UtteranceOption> Options { get; set; } = [];

    public string? Reason { get; set; }
}

internal sealed class UtterancePoint
{
    public int Index { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>cursor | spoken_grid | map_tap | menu_grid.</summary>
    public string Source { get; set; } = string.Empty;

    public decimal? X { get; set; }

    public decimal? Y { get; set; }
}

internal sealed class UtteranceAwaitingPoint
{
    public int Index { get; set; }

    public string Label { get; set; } = string.Empty;
}

internal sealed class UtteranceQuantity
{
    public string Unit { get; set; } = string.Empty;

    public int Value { get; set; }
}

internal sealed class UtteranceOption
{
    public string Type { get; set; } = string.Empty;
}
