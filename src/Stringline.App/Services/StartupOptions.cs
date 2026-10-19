using System.Globalization;
using Stringline.Core;

namespace Stringline.App.Services;

/// <summary>
/// Command-line options, for development and shortcuts:
/// <c>Stringline.exe [--wtt t.wtt] [--replay x.sslog [--speed 8|max] [--start 07:30]] [--resume s.stringline | --review s.stringline]</c>.
/// </summary>
internal sealed record StartupOptions(string? Timetable, string? Replay, double Speed, SimTime? Start, string? Resume = null, string? Review = null)
{
    public static StartupOptions None { get; } = new(null, null, 8, null);

    /// <returns>The options, or an error message for the user.</returns>
    public static (StartupOptions Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = None;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (i + 1 >= args.Count)
            {
                return (None, $"{name} needs a value.");
            }

            var value = args[++i];
            switch (name.ToLowerInvariant())
            {
                case "--wtt":
                    options = options with { Timetable = value };
                    break;
                case "--replay":
                    options = options with { Replay = value };
                    break;
                case "--resume":
                    options = options with { Resume = value };
                    break;
                case "--review":
                    options = options with { Review = value };
                    break;
                case "--speed" when value.Equals("max", StringComparison.OrdinalIgnoreCase):
                    options = options with { Speed = double.PositiveInfinity };
                    break;
                case "--speed" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0:
                    options = options with { Speed = speed };
                    break;
                case "--start" when SimTime.TryParse(value, out var start):
                    options = options with { Start = start };
                    break;
                default:
                    return (None, $"Unknown option or bad value: {name} {value}. Usage: Stringline [--wtt t.wtt] [--replay x.sslog [--speed 8|max] [--start 07:30]] [--resume s.stringline | --review s.stringline]");
            }
        }

        return (options, null);
    }
}
