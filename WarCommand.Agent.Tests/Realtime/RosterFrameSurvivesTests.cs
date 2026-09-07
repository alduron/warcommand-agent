using System.Text.Json;
using WarCommand.Agent.Core.Contracts;

namespace WarCommand.Agent.Tests.Realtime;

/// <summary>
/// The server's own deployment.roster shapes, parsed the way the socket parses them. A frame that
/// fails to deserialize is dropped in silence, and this frame is the only thing that moves the join
/// code and the headcount after a rotation.
/// </summary>
public class RosterFrameSurvivesTests
{
    private static Envelope Envelope(string payload) =>
        JsonSerializer.Deserialize<Envelope>(
            $$"""
              {"id":"7b1f3f9e-0c2e-4a5a-9a7d-2f5f6f0a1b2c","type":"deployment.roster","seq":4,
               "payload":{{payload}}}
              """,
            AgentJson.Options)!;

    [Fact]
    public void A_rotation_frame_parses()
    {
        var payload = Envelope(
            """
            {"deployment_id":"33333333-3333-3333-3333-333333333333","member_count":12,
             "invite_code":"440217","rotated_by_callsign":"Bear"}
            """).PayloadAs<DeploymentRosterPayload>();

        Assert.NotNull(payload);
        Assert.Equal("440217", payload!.InviteCode);
        Assert.Equal("Bear", payload.RotatedByCallsign);
    }

    /// <summary>
    /// roster_body always sends the key, and sends null whenever the deployment holds no code. A
    /// required non-nullable string is what drops a whole frame over a field nobody was reading.
    /// </summary>
    [Fact]
    public void A_headcount_frame_with_no_code_is_not_dropped()
    {
        var payload = Envelope(
            """
            {"deployment_id":"33333333-3333-3333-3333-333333333333","member_count":12,
             "invite_code":null,"rotated_by_callsign":null}
            """).PayloadAs<DeploymentRosterPayload>();

        Assert.NotNull(payload);
        Assert.Equal(12, payload!.MemberCount);
    }
}
