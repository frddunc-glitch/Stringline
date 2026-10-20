using System.Text.Json;

namespace Stringline.Gateway.Tests;

public class GatewayCommandsTests
{
    public static TheoryData<GatewayCommand, string, string> Commands => new()
    {
        { GatewayCommands.IdRequest(), GatewayTopics.SimSig, """{"idrequest":{}}""" },
        { GatewayCommands.Snapshot(), GatewayTopics.TrainDescriber, """{"snapshot":{}}""" },
        { GatewayCommands.TimetableSummary(), GatewayTopics.SimSig, """{"ttsummary":{}}""" },
        { GatewayCommands.TdStep("0243", "0235", "2R02"), GatewayTopics.TrainDescriber, """{"ca_msg":{"from":"0243","to":"0235","descr":"2R02"}}""" },
        { GatewayCommands.TdCancel("0130"), GatewayTopics.TrainDescriber, """{"cb_msg":{"from":"0130"}}""" },
        { GatewayCommands.TdInterpose("0149", "1A30"), GatewayTopics.TrainDescriber, """{"cc_msg":{"to":"0149","descr":"1A30"}}""" },
        { GatewayCommands.RouteRequest("RHJ1AM"), GatewayTopics.TrainDescriber, """{"routerequest":{"route":"RHJ1AM"}}""" },
        { GatewayCommands.RouteRequest("SHJ3", "SHJ5"), GatewayTopics.TrainDescriber, """{"routerequest":{"fromSignal":"SHJ3","toSignal":"SHJ5"}}""" },
        { GatewayCommands.ReplaceSignal("SHJ10"), GatewayTopics.TrainDescriber, """{"bpull":{"signal":"SHJ10"}}""" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Builds_exact_bodies(GatewayCommand command, string topic, string body)
    {
        command.Topic.Should().Be(topic);
        command.BodyText.Should().Be(body);
        command.ToString().Should().Be($"{topic} {body}");
    }

    [Fact]
    public void Characters_that_need_escaping_still_give_valid_JSON()
    {
        var command = GatewayCommands.TdInterpose("A\"B", "1\\A");

        var reader = new Utf8JsonReader(command.Body.Span);
        while (reader.Read())
        {
        }

        command.BodyText.Should().Contain("\\u0022").And.Contain("\\\\");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("A B")]
    [InlineData("A\nB")]
    [InlineData("A\u0001")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    public void Rejects_invalid_identifiers(string id)
    {
        FluentActions.Invoking(() => GatewayCommands.RouteRequest(id)).Should().Throw<ArgumentException>().WithParameterName("routeId");
        FluentActions.Invoking(() => GatewayCommands.TdStep("A", id, "1A01")).Should().Throw<ArgumentException>().WithParameterName("toBerth");
        GatewayCommands.IsValidId(id).Should().BeFalse();
    }

    [Theory]
    [InlineData("RHJ1AM")]
    [InlineData("SVC92")]
    [InlineData("1A23")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    public void Accepts_short_codes(string id)
    {
        GatewayCommands.IsValidId(id).Should().BeTrue();
        FluentActions.Invoking(() => GatewayCommands.RouteRequest(id)).Should().NotThrow();
    }

    [Fact]
    public void Rejects_null_identifiers()
    {
        FluentActions.Invoking(() => GatewayCommands.ReplaceSignal(null!)).Should().Throw<ArgumentNullException>();
        GatewayCommands.IsValidId(null).Should().BeFalse();
    }
}
