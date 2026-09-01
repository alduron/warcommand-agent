using WarCommand.Agent.Client.Realtime;

namespace WarCommand.Agent.Tests.Client;

/// <summary>
/// Backoff is full jitter on both legs, and the two draws are independent. Every deploy and every
/// Redis flush disconnects the whole fleet at once; jittering only the socket spreads the
/// reconnects and then lands every agent on Postgres at the same instant.
/// </summary>
public class ReconnectPolicyTests
{
    [Fact]
    public void Ceiling_doubles_from_the_base_and_stops_at_the_cap()
    {
        var policy = new ReconnectPolicy();

        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.Ceiling(0));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.Ceiling(1));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.Ceiling(4));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Ceiling(20));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Ceiling(1000));
    }

    [Fact]
    public void Both_legs_are_full_jitter_and_drawn_independently()
    {
        var draws = new Queue<double>([0.25, 0.75]);
        var policy = new ReconnectPolicy(jitter: draws.Dequeue);

        var socket = policy.NextSocketDelay(1);
        var revalidate = policy.NextRevalidateDelay(1);

        // Full jitter: uniform over the whole interval, not the ceiling itself.
        Assert.Equal(TimeSpan.FromMilliseconds(250), socket);
        Assert.Equal(TimeSpan.FromMilliseconds(750), revalidate);
        Assert.NotEqual(socket, revalidate);
    }

    [Fact]
    public void No_draw_exceeds_its_ceiling_and_the_spread_is_real()
    {
        var policy = new ReconnectPolicy();
        var socket = new List<TimeSpan>();
        var revalidate = new List<TimeSpan>();

        for (var i = 0; i < 200; i++)
        {
            socket.Add(policy.NextSocketDelay(5));
            revalidate.Add(policy.NextRevalidateDelay(5));
        }

        var ceiling = policy.Ceiling(5);
        Assert.All(socket, d => Assert.InRange(d, TimeSpan.Zero, ceiling));
        Assert.All(revalidate, d => Assert.InRange(d, TimeSpan.Zero, ceiling));

        // A fixed backoff would collapse both lists to one value.
        Assert.True(socket.Distinct().Count() > 100);
        Assert.True(revalidate.Distinct().Count() > 100);

        // The two legs never move together.
        Assert.True(socket.Where((value, index) => value == revalidate[index]).Count() < 5);
    }
}
