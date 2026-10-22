using Stringline.Editing.Validation;
using static Stringline.Editing.Tests.Draft;

namespace Stringline.Editing.Tests;

/// <summary>The rules' checks: names that resolve to no train or several, loops and locations not called at.</summary>
public sealed class RuleCheckTests
{
    [Fact]
    public void A_rule_must_name_one_train_on_each_side()
    {
        var result = Check(Doc(
            Simple("1A01"),
            Simple("1A02"),
            Simple("1A02"),
            Rule(1, "1A01", "9Z99"),
            Rule(1, "1A01", "1A02"),
            Rule(1, "1A01", "1A01"),
            Rule(99, "1A01", "1A01")));

        result.Of(IssueCodes.RuleUnresolved).Select(i => i.Message).Should().Equal(
            "Rule \"X appears after Y leaves\": Y names 9Z99, which is not in the timetable",
            "Rule \"X appears after Y leaves\": Y names 1A02, which is 2 trains");
        result.Of(IssueCodes.RuleUnknown).Should().ContainSingle().Which.Message.Should().Be("Unknown rule 99");
        result.Of(IssueCodes.RuleUnresolved).Should().OnlyContain(i => i.Headcode == "1A01" && i.Element!.Name.LocalName == "TimetableRule" && i.TrainElement != null);
        result.Of(IssueCodes.RuleCycle).Should().BeEmpty("a train waiting for itself is a dwell");
    }

    [Fact]
    public void Rules_that_wait_for_each_other_in_a_loop_are_cut_at_the_rule_that_closes_it()
    {
        var result = Check(Doc(
            Simple("1A01"),
            Simple("1A02"),
            Simple("1A03"),
            Rule(1, "1A01", "1A02"),
            Rule(4, "1A02", "1A03", "B"),
            Rule(3, "1A03", "1A01"),
            Rule(2, "1A03", "1A01", "B")));

        result.Of(IssueCodes.RuleCycle).Should().ContainSingle().Which.Message.Should()
            .Be("Rule \"X appears after Y arrives\" closes a loop: 1A03 waits for 1A01, which already waits for 1A03", "\"not if\" is no wait");
    }

    [Fact]
    public void A_rules_location_must_be_called_at_by_its_trains()
    {
        var result = Check(Doc(
            Simple("1A01"),
            Simple("1A02"),
            Train("1A03", Field("EntryPoint", "EUP"), Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:11"))),
            Rule(2, "1A01", "1A02", "C"),
            Rule(4, "1A01", "1A02", "C"),
            Rule(0, "1A01", "1A03", "EUP"),
            Rule(4, "1A01", "1A02", "B")));

        result.Of(IssueCodes.RuleLocationNotCalled).Select(i => i.Message).Should().Equal(
            "Rule \"X appears after Y arrives\": Y (1A02) does not call at C",
            "Rule \"X departs after Y arrives\": Y (1A02) does not call at C",
            "Rule \"X departs after Y arrives\": X (1A01) does not call at C");
    }
}
