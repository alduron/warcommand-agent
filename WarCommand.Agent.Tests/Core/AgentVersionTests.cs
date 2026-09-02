using WarCommand.Agent;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The version string the agent puts on the wire. It is not cosmetic: POST /v1/devices/register
/// caps agent_version at 32 characters, and a version over that is a 422 the agent reports as
/// "Startup failed" and never recovers from, so the device can never pair with anything.
/// </summary>
public class AgentVersionTests
{
    /// <summary>
    /// The shape the SDK actually stamps. IncludeSourceRevisionInInformationalVersion appends
    /// "+<40 char sha>" by default, which took a released 0.1.0 to 46 characters.
    /// </summary>
    [Fact]
    public void Build_metadata_is_stripped()
    {
        Assert.Equal(
            "0.1.0",
            App.CleanVersion("0.1.0+f4fa18d745506511073c1de4a011f5d782b44b13"));
    }

    [Fact]
    public void A_plain_version_is_untouched()
    {
        Assert.Equal("1.4.0", App.CleanVersion("1.4.0"));
    }

    /// <summary>A prerelease suffix is part of the version and stays. Only metadata goes.</summary>
    [Fact]
    public void A_prerelease_suffix_survives()
    {
        Assert.Equal("1.4.0-rc.1", App.CleanVersion("1.4.0-rc.1+abcdef0"));
    }

    [Theory]
    [InlineData("0.1.0+f4fa18d745506511073c1de4a011f5d782b44b13")]
    [InlineData("10.20.30-releasecandidate.12345678901234567890")]
    [InlineData("1.0.0")]
    public void Nothing_it_returns_can_be_refused_by_the_api(string informational)
    {
        Assert.InRange(App.CleanVersion(informational).Length, 1, 32);
    }
}
