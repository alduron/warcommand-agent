using System.Text;
using WarCommand.Agent.Client.Storage;
using WarCommand.Agent.Client.Tokens;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// tokens.dat is the only file holding a credential. It round trips, it is encrypted at rest, and
/// nothing in it ever reaches a log line.
/// </summary>
public class TokenStoreTests
{
    private const string AgentToken = "wc_agt_5f2b9c11aa4e4d2f";
    private const string RefreshToken = "wc_rft_0a1b2c3d4e5f6071";

    [Fact]
    public void Tokens_round_trip_through_the_file()
    {
        using var temp = new TempDirectory();
        var paths = new AgentPaths(temp.Path);
        var log = new RecordingLog();

        var store = new TokenStore(paths, new XorProtector(), log);
        store.SaveDeviceRegistration(Guid.NewGuid(), "wc_dev_abcdef");
        store.SaveIssued(new AgentTokens
        {
            AgentToken = AgentToken,
            RefreshToken = RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var reopened = new TokenStore(paths, new XorProtector(), log);
        Assert.Equal(AgentToken, reopened.Current!.AgentToken);
        Assert.Equal(RefreshToken, reopened.Current.RefreshToken);
        Assert.Equal("wc_dev_abcdef", reopened.DeviceToken);
    }

    [Fact]
    public void No_token_reaches_a_log_line_or_a_ToString()
    {
        using var temp = new TempDirectory();
        var log = new RecordingLog();
        var store = new TokenStore(new AgentPaths(temp.Path), new XorProtector(), log);

        store.SaveDeviceRegistration(Guid.NewGuid(), "wc_dev_abcdef");
        var tokens = new AgentTokens
        {
            AgentToken = AgentToken,
            RefreshToken = RefreshToken,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        store.SaveIssued(tokens);

        var rotated = new AgentTokens { AgentToken = "wc_agt_new", RefreshToken = "wc_rft_new", UpdatedAt = DateTimeOffset.UtcNow };
        store.CompleteRotation(store.BeginRotation(), rotated);
        store.Clear("test");

        Assert.NotEmpty(log.Lines);
        foreach (var line in log.Lines)
        {
            Assert.DoesNotContain(AgentToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain(RefreshToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain("wc_dev_abcdef", line, StringComparison.Ordinal);
            Assert.DoesNotContain("wc_agt_new", line, StringComparison.Ordinal);
        }

        // A record's generated ToString would print every member, which is one interpolation away
        // from a token in a log file.
        Assert.DoesNotContain(AgentToken, tokens.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RefreshToken, tokens.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_on_disk_holds_no_plaintext_token()
    {
        using var temp = new TempDirectory();
        var paths = new AgentPaths(temp.Path);
        var store = new TokenStore(paths, DpapiTokenProtector.Instance, new RecordingLog());
        store.SaveIssued(new AgentTokens { AgentToken = AgentToken, RefreshToken = RefreshToken, UpdatedAt = DateTimeOffset.UtcNow });

        var bytes = File.ReadAllBytes(paths.TokensFile);
        Assert.DoesNotContain(Encoding.UTF8.GetString(bytes), AgentToken, StringComparison.Ordinal);

        // DPAPI CurrentUser: it reads back in this session and nowhere else.
        var reopened = new TokenStore(paths, DpapiTokenProtector.Instance, new RecordingLog());
        Assert.Equal(AgentToken, reopened.Current!.AgentToken);
    }

    [Fact]
    public void Presenting_a_refresh_token_that_already_rotated_is_a_theft_and_kills_the_chain()
    {
        using var temp = new TempDirectory();
        var paths = new AgentPaths(temp.Path);
        var store = new TokenStore(paths, new XorProtector(), new RecordingLog());
        store.SaveIssued(new AgentTokens { AgentToken = AgentToken, RefreshToken = RefreshToken, UpdatedAt = DateTimeOffset.UtcNow });

        var first = store.BeginRotation();
        store.CompleteRotation(first, new AgentTokens
        {
            AgentToken = "wc_agt_2",
            RefreshToken = "wc_rft_2",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Assert.True(store.WasRotated(first));
        Assert.False(store.WasRotated("wc_rft_2"));

        var detected = false;
        store.ReuseDetected += (_, _) => detected = true;

        Assert.Throws<TokenReuseDetectedException>(() => store.CompleteRotation(first, new AgentTokens
        {
            AgentToken = "wc_agt_3",
            RefreshToken = "wc_rft_3",
            UpdatedAt = DateTimeOffset.UtcNow,
        }));

        Assert.True(detected);
        Assert.Null(store.Current);
        Assert.False(File.Exists(paths.TokensFile));
    }

    [Fact]
    public void An_unpaired_device_has_no_refresh_token_to_present()
    {
        using var temp = new TempDirectory();
        var store = new TokenStore(new AgentPaths(temp.Path), new XorProtector(), new RecordingLog());
        Assert.Null(store.Current);
        Assert.Throws<InvalidOperationException>(() => store.BeginRotation());
    }

    [Fact]
    public void Install_id_is_128_random_bits_written_once()
    {
        using var temp = new TempDirectory();
        var paths = new AgentPaths(temp.Path);

        var first = InstallId.LoadOrCreate(paths);
        var second = InstallId.LoadOrCreate(paths);

        Assert.Equal(first, second);
        Assert.Equal(InstallId.HexLength, first.Length);
        Assert.True(InstallId.IsWellFormed(first));
        Assert.NotEqual(first, InstallId.Mint());
    }
}
