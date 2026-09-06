using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Fire;
using WarCommand.Agent.Core.Model;
using Xunit;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// The bearing the game's compass reads, which is the grid bearing turned by whatever the map
/// disagrees with it by.
/// </summary>
/// <remarks>
/// Zero offset was an assumption with nothing behind it. It is invisible in testing because every
/// synthetic case is measured with the same atan2 that produced it, so the only thing that catches
/// a rotation is somebody aiming where they were told and missing.
/// </remarks>
public class AzimuthOffsetTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static MapPoint At(decimal x, decimal y) => new(x, y, "map_readout", null, null);

    private static decimal BearingWith(decimal offset, MapPoint gun, MapPoint target)
    {
        var ballistics = BundledContracts.Ballistics().Current;
        var turned = ballistics with
        {
            MapGeometry = ballistics.MapGeometry with { AzimuthOffsetDegrees = offset },
        };

        return FireSolutionCalculator.Compute(
            new GunPosition(turned.Weapons[0].Id, gun, T0),
            target,
            turned.Weapons[0],
            turned,
            BundledContracts.GameProfile().Current,
            null,
            T0).AzimuthDegrees;
    }

    /// <summary>The shipped value is zero, and zero must change nothing.</summary>
    [Fact]
    public void The_offset_ships_at_zero()
    {
        Assert.Equal(0m, BundledContracts.Ballistics().Current.MapGeometry.AzimuthOffsetDegrees);
        Assert.Equal(0m, BearingWith(0m, At(50m, 50m), At(50m, 60m)));
        Assert.Equal(90m, BearingWith(0m, At(50m, 50m), At(60m, 50m)));
    }

    /// <summary>
    /// The reported case: the tool said 2 and the gun had to be laid on 357.
    /// </summary>
    /// <remarks>
    /// If that is a compass rotation, this is the whole fix and it is a contract edit. The test
    /// pins the mechanism, not the value: the value stays zero until somebody measures it.
    /// </remarks>
    [Fact]
    public void A_measured_rotation_moves_every_bearing_by_the_same_amount()
    {
        // A shot 1.33 units east of due north over 15 units reads 2 with no offset.
        var gun = At(50m, 50m);
        var target = At(50.533m, 65.25m);

        Assert.Equal(2m, BearingWith(0m, gun, target));
        Assert.Equal(357m, BearingWith(-5m, gun, target));
    }

    /// <summary>An offset that takes the bearing under zero or over 360 wraps rather than clipping.</summary>
    [Theory]
    [InlineData(-5, 355)]
    [InlineData(5, 5)]
    [InlineData(-370, 350)]
    [InlineData(370, 10)]
    public void The_bearing_wraps(int offset, int expected)
    {
        Assert.Equal(expected, BearingWith(offset, At(50m, 50m), At(50m, 60m)));
    }

    /// <summary>
    /// A rotation is the same everywhere. A coordinate error is not, and that is how to tell them
    /// apart with one shot pointed east.
    /// </summary>
    [Fact]
    public void A_rotation_is_constant_where_a_coordinate_error_is_not()
    {
        var gun = At(50m, 50m);

        // Turned five degrees: every bearing moves five, whatever direction it points.
        Assert.Equal(355m, BearingWith(-5m, gun, At(50m, 65m)));
        Assert.Equal(85m, BearingWith(-5m, gun, At(65m, 50m)));
        Assert.Equal(175m, BearingWith(-5m, gun, At(50m, 35m)));

        // The same five degrees at north from an x error instead: due east is untouched by it.
        Assert.Equal(5m, BearingWith(0m, gun, At(51.31m, 65m)));
        Assert.Equal(90m, BearingWith(0m, gun, At(66.31m, 50m)));
    }
}
