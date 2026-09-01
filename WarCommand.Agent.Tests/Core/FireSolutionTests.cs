using WarCommand.Agent.Core.Contracts;
using WarCommand.Agent.Core.Fire;
using WarCommand.Agent.Core.Model;

namespace WarCommand.Agent.Tests.Core;

/// <summary>
/// A bracket has two halves with different dependencies. Geometry always renders; elevation is
/// withheld while the table is a placeholder, which is what both weapons ship as today.
/// </summary>
public class FireSolutionTests
{
    private static readonly DateTimeOffset T0 = Rows.Epoch;

    private static Ballistics Ballistics => ContractFixtures.Ballistics;

    private static GameProfile Profile => ContractFixtures.Profile;

    private static MapPoint Point(decimal x, decimal y) => new(x, y, "map_readout", null, 0.9m);

    private static GunPosition Gun(decimal x, decimal y, DateTimeOffset? setAt = null) =>
        new("l81_mortar", Point(x, y), setAt ?? T0);

    private static WeaponDef Mortar => Ballistics.Weapon("l81_mortar")!;

    private static WeaponDef Sph2 => Ballistics.Weapon("sph2")!;

    private static FireSolution Compute(
        MapPoint target,
        WeaponDef? weapon = null,
        GunPosition? gun = null,
        string? map = "bakurani",
        DateTimeOffset? now = null,
        GameProfile? profile = null,
        Ballistics? ballistics = null,
        string? arc = null) =>
        FireSolutionCalculator.Compute(
            gun ?? Gun(10m, 10m),
            target,
            weapon ?? Mortar,
            ballistics ?? Ballistics,
            profile ?? Profile,
            map,
            now ?? T0,
            arc);

    [Fact]
    public void Both_shipped_weapons_still_carry_a_placeholder_table()
    {
        Assert.False(Mortar.HasMeasuredTable);
        Assert.False(Sph2.HasMeasuredTable);
    }

    [Fact]
    public void Elevation_is_blocked_while_the_table_is_a_placeholder()
    {
        // 3 units at 100 m per unit is 300 m, inside the L81's 120-684 m.
        var solution = Compute(Point(10m, 13m));

        Assert.Equal(FireSolutionStatus.NoFiringTable, solution.Status);
        Assert.Equal(FireSolution.NoFiringTableMessage, solution.Message);
        Assert.Null(solution.ElevationMils);
        Assert.Null(solution.TimeOfFlightS);
        Assert.False(solution.HasElevation);
    }

    [Fact]
    public void Geometry_still_renders_when_elevation_is_blocked()
    {
        var solution = Compute(Point(10m, 13m));

        Assert.Equal(0m, solution.AzimuthDegrees);
        Assert.Equal(3.00m, solution.RangeUnits);
        Assert.Equal(300.0m, solution.RangeMeters);
    }

    [Theory]
    [InlineData(10, 20, 0)]    // due north
    [InlineData(20, 10, 90)]   // due east
    [InlineData(10, 0, 180)]   // due south
    [InlineData(0, 10, 270)]   // due west
    public void Azimuth_is_atan2_on_two_deltas_from_true_north(int x, int y, int expected)
    {
        var solution = Compute(Point(x, y));

        Assert.Equal(expected, solution.AzimuthDegrees);
    }

    [Fact]
    public void Range_is_hypot_times_the_map_scale()
    {
        var solution = Compute(Point(13m, 14m), gun: Gun(10m, 10m));

        Assert.Equal(5.00m, solution.RangeUnits);
        Assert.Equal(500.0m, solution.RangeMeters);
    }

    [Fact]
    public void Out_of_range_refuses_rather_than_extrapolating()
    {
        var tooFar = Compute(Point(10m, 30m));   // 2000 m, past the L81's 684 m
        var tooClose = Compute(Point(10m, 10.5m)); // 50 m, below its 120 m

        Assert.Equal(FireSolutionStatus.OutOfRange, tooFar.Status);
        Assert.Equal(Ballistics.SolutionRules.RefuseMessage, tooFar.Message);
        Assert.Null(tooFar.ElevationMils);
        Assert.Equal(FireSolutionStatus.OutOfRange, tooClose.Status);
        Assert.Equal(Mortar.MinRangeM, tooFar.MinRangeM);
        Assert.Equal(Mortar.MaxRangeM, tooFar.MaxRangeM);
    }

    [Fact]
    public void Out_of_range_still_renders_the_geometry()
    {
        var solution = Compute(Point(10m, 30m));

        Assert.Equal(0m, solution.AzimuthDegrees);
        Assert.Equal(20.00m, solution.RangeUnits);
    }

    [Fact]
    public void A_measured_table_interpolates_linearly_between_adjacent_rows()
    {
        var weapon = Measured();
        // 250 m sits halfway between the 200 m and 300 m rows.
        var solution = Compute(Point(10m, 12.5m), weapon, ballistics: WithWeapon(weapon));

        Assert.Equal(FireSolutionStatus.Ok, solution.Status);
        Assert.Equal(700, solution.ElevationMils);
        Assert.Equal(14.0m, solution.TimeOfFlightS);
        Assert.Null(solution.Message);
    }

    [Fact]
    public void A_measured_table_never_extrapolates_past_its_last_row()
    {
        var weapon = Measured() with { MaxRangeM = 500 };
        // 450 m is inside the stated envelope but past the last table row at 400 m.
        var solution = Compute(Point(10m, 14.5m), weapon, ballistics: WithWeapon(weapon));

        Assert.Equal(FireSolutionStatus.NoFiringTable, solution.Status);
        Assert.Null(solution.ElevationMils);
    }

    [Fact]
    public void The_low_arc_is_used_only_where_it_exists()
    {
        var weapon = Measured() with
        {
            Arcs = ["high", "low"],
            LowArcMinRangeM = 300,
            Table =
            [
                new BallisticsRow { RangeM = 200, HighMil = 780, TofS = 13.0m },
                new BallisticsRow { RangeM = 300, HighMil = 620, TofS = 15.0m, LowMil = 300 },
                new BallisticsRow { RangeM = 400, HighMil = 500, TofS = 16.0m, LowMil = 360 },
            ],
        };

        var low = Compute(Point(10m, 13.5m), weapon, ballistics: WithWeapon(weapon), arc: "low");
        var tooCloseForLow = Compute(Point(10m, 12.5m), weapon, ballistics: WithWeapon(weapon), arc: "low");

        Assert.Equal("low", low.Arc);
        Assert.Equal(330, low.ElevationMils);
        Assert.Equal("high", tooCloseForLow.Arc);
    }

    [Fact]
    public void Every_bracket_carries_the_spotter_hint_and_is_never_called_a_solution()
    {
        var solution = Compute(Point(10m, 13m));

        Assert.Equal("ADJUST FROM SPOTTER", solution.SpotterHint);
        Assert.Equal("BRACKET", solution.Label);
        Assert.Equal(Ballistics.SolutionRules.Presentation.SpotterHint, solution.SpotterHint);
    }

    [Fact]
    public void A_gun_position_goes_stale_at_five_minutes()
    {
        var fresh = Compute(Point(10m, 13m), now: T0.AddMinutes(4));
        var stale = Compute(Point(10m, 13m), now: T0.AddMinutes(5));

        Assert.False(fresh.GunPositionStale);
        Assert.True(stale.GunPositionStale);
    }

    [Fact]
    public void An_unknown_map_refuses_when_the_profile_carries_a_divergent_per_map_scale()
    {
        var divergent = Profile with
        {
            Maps = [.. Profile.Maps, new MapDef { Id = "third", Display = "Third", UnitsToMeters = 50m }],
        };

        var solution = Compute(Point(10m, 13m), map: null, profile: divergent);

        Assert.Equal(FireSolutionStatus.MapUnknown, solution.Status);
        Assert.Equal(FireSolution.MapUnknownMessage, solution.Message);
        Assert.Null(solution.RangeMeters);
        Assert.True(solution.RangeInMapUnits);
        Assert.Equal(3.00m, solution.RangeUnits);
        Assert.Equal(0m, solution.AzimuthDegrees);
    }

    [Fact]
    public void A_profile_whose_maps_all_share_the_default_is_unaffected_by_an_unknown_map()
    {
        Assert.True(Profile.AllMapsShareDefaultScale);

        var solution = Compute(Point(10m, 13m), map: null);

        Assert.NotEqual(FireSolutionStatus.MapUnknown, solution.Status);
        Assert.Equal(300.0m, solution.RangeMeters);
    }

    [Fact]
    public void The_maps_own_scale_wins_over_the_default()
    {
        var profile = Profile with
        {
            Maps = [new MapDef { Id = "half", Display = "Half", UnitsToMeters = 50m }],
        };

        var solution = Compute(Point(10m, 13m), map: "half", profile: profile);

        Assert.Equal(150.0m, solution.RangeMeters);
    }

    [Fact]
    public void A_leg_gives_bearing_and_distance_from_the_same_atan2_and_hypot()
    {
        var leg = FireSolutionCalculator.Leg(Point(10m, 10m), Point(13m, 14m), 100m);

        Assert.Equal(37m, leg.BearingDegrees);
        Assert.Equal(5.00m, leg.DistanceUnits);
        Assert.Equal(500.0m, leg.DistanceMeters);
        Assert.False(leg.DistanceInMapUnits);
    }

    [Fact]
    public void A_leg_with_no_scale_reads_in_map_units()
    {
        var leg = FireSolutionCalculator.Leg(Point(10m, 10m), Point(13m, 14m), unitsToMeters: null);

        Assert.Null(leg.DistanceMeters);
        Assert.True(leg.DistanceInMapUnits);
    }

    private static WeaponDef Measured() => new()
    {
        Id = "test_gun",
        Display = "Test Gun",
        Role = "mortar",
        MinRangeM = 100,
        MaxRangeM = 400,
        Arcs = ["high"],
        ElevationMils = new ElevationEnvelope { Min = 100, Max = 900 },
        TableConfidence = "measured",
        Table =
        [
            new BallisticsRow { RangeM = 200, HighMil = 780, TofS = 13.0m },
            new BallisticsRow { RangeM = 300, HighMil = 620, TofS = 15.0m },
            new BallisticsRow { RangeM = 400, HighMil = 500, TofS = 16.0m },
        ],
    };

    private static Ballistics WithWeapon(WeaponDef weapon) => Ballistics with { Weapons = [weapon] };
}
