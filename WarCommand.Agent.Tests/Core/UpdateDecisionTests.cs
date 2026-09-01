using WarCommand.Agent.Core.Updates;

namespace WarCommand.Agent.Tests.Core;

public class SemVersionTests
{
    [Theory]
    [InlineData("1.4.0", 1, 4, 0)]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    [InlineData("1.4.0-dev", 1, 4, 0)]
    [InlineData("1.4.0+c3b4c20", 1, 4, 0)]
    [InlineData("  1.4.0  ", 1, 4, 0)]
    public void Parses_a_triple_and_discards_any_suffix(string text, int major, int minor, int patch)
    {
        Assert.True(SemVersion.TryParse(text, out var version));
        Assert.Equal(new SemVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.4")]
    [InlineData("1.4.0.1")]
    [InlineData("latest")]
    [InlineData("v1.4.0")]
    [InlineData("-1.4.0")]
    [InlineData("1.x.0")]
    public void Refuses_anything_that_is_not_a_triple(string? text)
    {
        Assert.False(SemVersion.TryParse(text, out _));
    }

    [Fact]
    public void Orders_by_major_then_minor_then_patch()
    {
        Assert.True(new SemVersion(1, 0, 0) < new SemVersion(2, 0, 0));
        Assert.True(new SemVersion(1, 9, 9) < new SemVersion(2, 0, 0));
        Assert.True(new SemVersion(1, 2, 0) < new SemVersion(1, 10, 0));
        Assert.True(new SemVersion(1, 2, 3) > new SemVersion(1, 2, 2));
        Assert.Equal(new SemVersion(1, 2, 3), new SemVersion(1, 2, 3));
    }

    [Fact]
    public void A_local_build_loses_to_every_release()
    {
        Assert.True(SemVersion.Zero < new SemVersion(0, 0, 1));
    }
}

public class UpdateDecisionTests
{
    private const string Digest = "3f9a1c2b4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8";
    private static readonly SemVersion Running = new(1, 0, 0);

    private static PublishedRelease Release(
        string version = "1.1.0", string? url = "https://example.test/setup.exe", string? sha = Digest, string? notes = null)
        => new() { Version = version, Url = url, Sha256 = sha, Notes = notes };

    [Fact]
    public void A_newer_release_is_offered_when_the_game_is_closed()
    {
        var result = UpdateDecision.Evaluate(Running, Release(), gameIsRunning: false, out var offer);
        Assert.Equal(UpdateAvailability.Ready, result);
        Assert.NotNull(offer);
        Assert.Equal(new SemVersion(1, 1, 0), offer!.Version);
        Assert.Equal(Digest, offer.Sha256);
    }

    [Fact]
    public void A_newer_release_is_offered_but_held_while_the_game_runs()
    {
        var result = UpdateDecision.Evaluate(Running, Release(), gameIsRunning: true, out var offer);
        Assert.Equal(UpdateAvailability.WaitingForGameToClose, result);
        Assert.NotNull(offer);
    }

    [Fact]
    public void Nothing_published_is_not_an_update()
    {
        Assert.Equal(UpdateAvailability.None, UpdateDecision.Evaluate(Running, null, false, out var offer));
        Assert.Null(offer);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.9")]
    [InlineData("0.0.1")]
    public void The_same_version_or_older_is_never_offered(string version)
    {
        Assert.Equal(UpdateAvailability.None,
            UpdateDecision.Evaluate(Running, Release(version: version), false, out var offer));
        Assert.Null(offer);
    }

    [Fact]
    public void An_unparseable_version_is_not_an_update()
    {
        Assert.Equal(UpdateAvailability.None,
            UpdateDecision.Evaluate(Running, Release(version: "latest"), false, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example.test/setup.exe")]
    [InlineData("ftp://example.test/setup.exe")]
    [InlineData("file:///C:/setup.exe")]
    [InlineData("not a url")]
    public void An_installer_url_that_is_not_https_is_refused(string? url)
    {
        Assert.Equal(UpdateAvailability.None,
            UpdateDecision.Evaluate(Running, Release(url: url), false, out var offer));
        Assert.Null(offer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("3F9A1C2B4D5E6F708192A3B4C5D6E7F8091A2B3C4D5E6F708192A3B4C5D6E7F8")]
    [InlineData("3f9a1c2b4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f")]
    [InlineData("3f9a1c2b4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7fzz")]
    public void A_release_we_cannot_verify_is_refused(string? sha)
    {
        Assert.Equal(UpdateAvailability.None,
            UpdateDecision.Evaluate(Running, Release(sha: sha), false, out var offer));
        Assert.Null(offer);
    }

    [Fact]
    public void Blank_notes_are_dropped_rather_than_rendered_empty()
    {
        UpdateDecision.Evaluate(Running, Release(notes: "   "), false, out var offer);
        Assert.Null(offer!.Notes);
    }

    [Fact]
    public void Notes_survive_when_present()
    {
        UpdateDecision.Evaluate(Running, Release(notes: "Panic key fix."), false, out var offer);
        Assert.Equal("Panic key fix.", offer!.Notes);
    }
}
