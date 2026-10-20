using Stringline.Core;
using Stringline.Network.Profiles;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>The moves assist (S19) remembers routes by.</summary>
public sealed class AssistMovesTests
{
    private static readonly Tiploc A = new("A");
    private static readonly Tiploc B = new("B");
    private static readonly Tiploc C = new("C");

    private static WorkingPlan Plan() => WorkingPlan.FromTimetable(Of(
        Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06") with { Platform = " 2 ", Line = "dm" }, Call("C", "08:12", null)])));

    [Fact]
    public void A_call_comes_from_the_location_before_and_heads_for_the_one_after()
    {
        var plan = Plan().Trains[0];

        AssistMoves.Of(plan, 1).Should().Be(new AssistMove(B, "2", "DM", A, C));
        AssistMoves.Of(plan, 0).Should().Be(new AssistMove(A, Towards: B));
        AssistMoves.Of(plan, 2).Should().Be(new AssistMove(C, From: B));
    }

    [Fact]
    public void A_train_terminated_short_heads_nowhere()
    {
        var (plan, result) = Plan().Apply(new TerminateAt(new TrainId("2S01"), 1));
        result.Applied.Should().BeTrue(result.Error);

        AssistMoves.Of(plan.Trains[0], 1).Towards.Should().BeNull();
        FluentActions.Invoking(() => AssistMoves.Of(plan.Trains[0], 2)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_profile_remembers_one_route_per_move()
    {
        var profile = new StringlineProfile { AreaId = "test" };
        var move = AssistMoves.Of(Plan().Trains[0], 1);
        var other = move with { Platform = "3" };

        profile = profile.WithAssistRoute(new AssistRoute(move, "RB2A")).WithAssistRoute(new AssistRoute(other, FromSignal: "SB3", ToSignal: "SC1"));
        profile = profile.WithAssistRoute(new AssistRoute(move, "RB2B"));

        profile.AssistRoutes.Should().HaveCount(2);
        profile.FindAssistRoute(AssistMove.Of(B, "2 ", "DM", A, C))!.Route.Should().Be("RB2B");
        profile.FindAssistRoute(other)!.ToString().Should().Be("SB3 to SC1");
        profile.FindAssistRoute(move with { Towards = A }).Should().BeNull();
    }
}
