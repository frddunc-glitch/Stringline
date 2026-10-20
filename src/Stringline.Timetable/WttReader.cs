using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Unicode;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Timetable;

/// <summary>
/// Reads a SimSig <c>.wtt</c> (a zip holding <c>SavedTimetable.xml</c>, or that XML on its own) into a
/// <see cref="WorkingTimetable"/>. Tolerant: a bad value, timing point or train becomes an <see cref="ImportWarning"/>
/// and the rest of the file is still read; damaged XML keeps every train read before the damage. Only a file that is
/// not a timetable at all throws (<see cref="WttFormatException"/>). Field meanings are in <c>docs/04-…md</c> §1.
/// </summary>
/// <remarks>Thread-safe; each <c>Read</c> is independent.</remarks>
public sealed partial class WttReader
{
    private const string TimetableEntry = "SavedTimetable.xml";

    /// <summary>SimSig allowances are stored in half minutes (checked against run times in 284 timetables).</summary>
    private const int AllowanceUnitSeconds = 30;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    private readonly ILogger _logger;

    public WttReader(ILogger<WttReader>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public WttImportResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        return Read(stream, Path.GetFileName(path));
    }

    /// <param name="sourceName">File name for log messages.</param>
    public WttImportResult Read(Stream stream, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var xml = ExtractXml(stream);
        var import = new Import(_logger);
        using (var reader = CreateXmlReader(xml, import))
        {
            import.ReadDocument(reader);
        }

        var result = import.Finish();
        LogImported(sourceName ?? "timetable", result.Timetable.AreaId, result.Timetable.Trains.Count, result.SkippedTrains, result.Warnings.Count);
        return result;
    }

    private static byte[] ExtractXml(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.AsSpan().StartsWith("PK\u0003\u0004"u8))
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, TimetableEntry, StringComparison.OrdinalIgnoreCase))
                    ?? throw new WttFormatException($"The file is a zip but has no {TimetableEntry}, so it is not a SimSig timetable.");
                using var entryStream = entry.Open();
                using var xml = new MemoryStream();
                entryStream.CopyTo(xml);
                return xml.ToArray();
            }
            catch (InvalidDataException ex)
            {
                throw new WttFormatException("The file looks like a zip but cannot be opened: " + ex.Message, ex);
            }
        }

        var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        if (start >= bytes.Length || bytes[start] != (byte)'<')
        {
            throw new WttFormatException("The file is neither a .wtt (zip) nor timetable XML.");
        }

        return bytes;
    }

    /// <summary>
    /// SimSig writes UTF-8 without a declaration. A hand-edited file may be in a Windows code page instead; read that
    /// as Latin-1 rather than fail, which keeps every ASCII character (all codes, times and numbers) intact.
    /// </summary>
    private static XmlReader CreateXmlReader(byte[] xml, Import import)
    {
        var head = Encoding.ASCII.GetString(xml, 0, Math.Min(xml.Length, 200));
        var declaresEncoding = head.StartsWith("<?xml", StringComparison.Ordinal) && head.Contains("encoding", StringComparison.OrdinalIgnoreCase);
        if (!declaresEncoding && !Utf8.IsValid(xml))
        {
            import.Warn(ImportSeverity.Info, "The XML is not valid UTF-8; read it as Latin-1, so some accented characters in descriptions may be wrong.");
            return XmlReader.Create(new StreamReader(new MemoryStream(xml), Encoding.Latin1), XmlSettings);
        }

        return XmlReader.Create(new MemoryStream(xml), XmlSettings);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Source} ({AreaId}): {Trains} trains, {Skipped} skipped, {Warnings} warnings")]
    private partial void LogImported(string source, string areaId, int trains, int skipped, int warnings);

    /// <summary>The state of one import: header, drafts and warnings, finished into a <see cref="WttImportResult"/>.</summary>
    private sealed partial class Import(ILogger logger)
    {
        private readonly List<ImportWarning> _warnings = [];
        private readonly Dictionary<string, int> _unknown = new(StringComparer.Ordinal);
        private readonly List<TrainCategory> _categories = [];
        private readonly List<Train> _trains = [];
        private readonly List<RawRule> _rules = [];
        private readonly List<SeedGroup> _seedGroups = [];
        private readonly Dictionary<Train, string> _rawDescriptions = new(ReferenceEqualityComparer.Instance);
        private string _areaId = string.Empty;
        private string _simSigVersion = string.Empty;
        private string _name = string.Empty;
        private string _description = string.Empty;
        private string? _template;
        private string? _vMajor;
        private string? _vMinor;
        private string? _vBuild;
        private SimTime? _start;
        private SimTime? _finish;
        private int _trainsSeen;
        private int _skipped;

        public void Warn(ImportSeverity severity, string message, string? train = null)
        {
            var warning = new ImportWarning(severity, message, train);
            _warnings.Add(warning);
            LogWarning(warning);
        }

        public void ReadDocument(XmlReader reader)
        {
            try
            {
                reader.MoveToContent();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "SimSigTimetable")
                {
                    throw new WttFormatException($"The root element is <{reader.LocalName}>, not <SimSigTimetable>, so this is not a SimSig timetable.");
                }

                _areaId = reader.GetAttribute("ID")?.Trim() ?? string.Empty;
                _simSigVersion = reader.GetAttribute("Version")?.Trim() ?? string.Empty;
                if (_areaId.Length == 0)
                {
                    Warn(ImportSeverity.Warning, "The timetable has no sim ID (root ID attribute), so it cannot be tied to a profile.");
                }

                if (reader.IsEmptyElement)
                {
                    return;
                }

                reader.Read();
                while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        reader.Read();
                        continue;
                    }

                    ReadHeaderElement(reader);
                }
            }
            catch (XmlException ex)
            {
                if (_trainsSeen == 0 && _areaId.Length == 0)
                {
                    throw new WttFormatException("The timetable XML cannot be read: " + ex.Message, ex);
                }

                Warn(ImportSeverity.Error, $"The XML is damaged at line {ex.LineNumber}: {ex.Message} The {_trains.Count} trains read before that point were kept; anything after it is missing.");
            }
        }

        private void ReadHeaderElement(XmlReader reader)
        {
            switch (reader.LocalName)
            {
                case "Name": _name = ReadText(reader); break;
                case "Description": _description = ReadText(reader); break;
                case "StartTime": _start = ParseTime(ReadText(reader), "StartTime", null); break;
                case "FinishTime": _finish = ParseTime(ReadText(reader), "FinishTime", null); break;
                case "VMajor": _vMajor = ReadText(reader); break;
                case "VMinor": _vMinor = ReadText(reader); break;
                case "VBuild": _vBuild = ReadText(reader); break;
                case "TrainDescriptionTemplate": _template = ReadText(reader); break;
                case "TrainCategories": ReadCategories((XElement)XNode.ReadFrom(reader)); break;
                case "TimetableRules": ReadRules((XElement)XNode.ReadFrom(reader)); break;
                case "SeedGroups": ReadSeedGroups((XElement)XNode.ReadFrom(reader)); break;
                case "Timetables": ReadTimetables(reader); break;
                default:
                    if (!Known.IgnoredHeader.Contains(reader.LocalName))
                    {
                        CountUnknown(reader.LocalName);
                    }

                    reader.Skip();
                    break;
            }
        }

        private static string ReadText(XmlReader reader) => reader.ReadElementContentAsString().Trim();

        private void ReadTimetables(XmlReader reader)
        {
            if (reader.IsEmptyElement)
            {
                reader.Read();
                return;
            }

            reader.Read();
            while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                }
                else if (reader.LocalName == "Timetable")
                {
                    _trainsSeen++;
                    ReadTrain((XElement)XNode.ReadFrom(reader));
                }
                else
                {
                    CountUnknown("Timetables/" + reader.LocalName);
                    reader.Skip();
                }
            }

            reader.Read();
        }

        private void ReadCategories(XElement element)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName != "TrainCategory")
                {
                    CountUnknown("TrainCategories/" + child.Name.LocalName);
                    continue;
                }

                var id = ((string?)child.Attribute("ID"))?.Trim();
                if (string.IsNullOrEmpty(id))
                {
                    Warn(ImportSeverity.Warning, "A train category has no ID and was skipped.");
                    continue;
                }

                if (_categories.Any(c => c.Id == id))
                {
                    Warn(ImportSeverity.Info, $"Category {id} is defined twice; the first definition is used.");
                    continue;
                }

                CheckElements(child, "TrainCategory", Known.Category);
                var dwell = child.Element("DwellTimes");
                if (dwell is not null)
                {
                    CheckElements(dwell, "TrainCategory/DwellTimes", Known.Dwell);
                }

                var context = "category " + id;
                _categories.Add(new TrainCategory(
                    id,
                    Text(child, "Description") ?? string.Empty,
                    ParseBool(Text(child, "IsFreight"), "IsFreight", context) ?? false,
                    ParseInt(Text(child, "MaxSpeed"), "MaxSpeed", context),
                    ParseInt(Text(child, "TrainLength"), "TrainLength", context),
                    ParseInt(Text(child, "AccelBrakeIndex"), "AccelBrakeIndex", context),
                    ParseInt(Text(child, "SpeedClass"), "SpeedClass", context),
                    Text(child, "Electrification"),
                    dwell is null ? DwellTimes.None : ReadDwell(dwell, context)));
            }
        }

        private DwellTimes ReadDwell(XElement e, string context) => new(
            ParseInt(Text(e, "RedSignalMoveOff"), "RedSignalMoveOff", context),
            ParseInt(Text(e, "StationForward"), "StationForward", context),
            ParseInt(Text(e, "StationReverse"), "StationReverse", context),
            ParseInt(Text(e, "TerminateForward"), "TerminateForward", context),
            ParseInt(Text(e, "TerminateReverse"), "TerminateReverse", context),
            ParseInt(Text(e, "Join"), "Join", context),
            ParseInt(Text(e, "Divide"), "Divide", context),
            ParseInt(Text(e, "CrewChange"), "CrewChange", context));

        private void ReadSeedGroups(XElement element)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName != "SeedGroup")
                {
                    CountUnknown("SeedGroups/" + child.Name.LocalName);
                    continue;
                }

                if (!child.HasElements)
                {
                    continue; // an empty placeholder: nothing to read
                }

                CheckElements(child, "SeedGroup", Known.SeedGroup);
                var id = Text(child, "ID");
                var title = Text(child, "Title") ?? string.Empty;
                if (id is null || ParseTime(Text(child, "StartTime"), "StartTime", "seed group " + title) is not { } start)
                {
                    Warn(ImportSeverity.Warning, $"Seed group '{title}' has no ID or start time and was skipped.");
                    continue;
                }

                if (_seedGroups.Any(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    Warn(ImportSeverity.Info, $"Seed group {id} is defined twice; the first definition is used.");
                    continue;
                }

                _seedGroups.Add(new SeedGroup(id, start, title));
            }
        }

        private void ReadRules(XElement element)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName != "TimetableRule")
                {
                    CountUnknown("TimetableRules/" + child.Name.LocalName);
                    continue;
                }

                CheckElements(child, "TimetableRule", Known.Rule);
                const string context = "timetable rule";
                if (ParseInt(Text(child, "Rule"), "Rule", context) is not { } code)
                {
                    Warn(ImportSeverity.Info, "A timetable rule has no rule number and was skipped.");
                    continue;
                }

                Tiploc? location = Tiploc.TryCreate(Text(child, "Location"), out var t) ? t : null;
                _rules.Add(new RawRule(
                    code,
                    Text(child, "TrainUID"),
                    Text(child, "Train"),
                    Text(child, "Train2UID"),
                    Text(child, "Train2"),
                    ParseInt(Text(child, "Time"), "Time", context),
                    location));
            }
        }

        private void ReadTrain(XElement e)
        {
            var idText = Text(e, "ID");
            if (!Headcode.TryCreate(idText, out var headcode))
            {
                _skipped++;
                Warn(ImportSeverity.Warning, $"Train {_trainsSeen} in the file has no usable headcode (ID '{idText}') and was skipped.");
                return;
            }

            var name = headcode.Value;
            CheckElements(e, "Timetable", Known.Train);
            var points = new List<TimingPoint>();
            var trips = e.Element("Trips");
            foreach (var trip in trips?.Elements() ?? [])
            {
                if (trip.Name.LocalName != "Trip")
                {
                    CountUnknown("Trips/" + trip.Name.LocalName);
                    continue;
                }

                if (ReadPoint(trip, points.Count, name) is { } point)
                {
                    points.Add(point);
                }
            }

            if (points.Count == 0)
            {
                _skipped++;
                Warn(ImportSeverity.Warning, "Has no usable timing points and was skipped.", name);
                return;
            }

            CheckTimeOrder(points, name);
            var entryPoint = Tiploc.TryCreate(Text(e, "EntryPoint"), out var entry) ? entry : (Tiploc?)null;
            var seedPoint = Tiploc.TryCreate(Text(e, "SeedPoint"), out var seed) ? seed : (Tiploc?)null;
            var train = new Train
            {
                Id = new TrainId("?"),
                Headcode = headcode,
                Uid = Text(e, "UID"),
                CategoryId = Text(e, "Category"),
                Start = entryPoint is not null ? TrainStart.EntryPoint : seedPoint is not null ? TrainStart.Seeded : TrainStart.AtFirstLocation,
                EntryPoint = entryPoint,
                EntryTime = ParseTime(Text(e, "DepartTime"), "DepartTime", name),
                SeedPoint = seedPoint,
                SeedGroup = seedPoint is null ? null : Text(e, "SeedGroup"),
                Started = ParseBool(Text(e, "Started"), "Started", name) ?? false,
                MaxSpeedMph = ParseInt(Text(e, "MaxSpeed"), "MaxSpeed", name),
                LengthMetres = ParseInt(Text(e, "TrainLength"), "TrainLength", name),
                IsFreight = ParseBool(Text(e, "IsFreight"), "IsFreight", name) ?? false,
                AsRequired = ParseBool(Text(e, "AsRequired"), "AsRequired", name) ?? false,
                OriginName = Text(e, "OriginName"),
                DestinationName = Text(e, "DestinationName"),
                OriginTime = ParseTime(Text(e, "OriginTime"), "OriginTime", name),
                DestinationTime = ParseTime(Text(e, "DestinationTime"), "DestinationTime", name),
                OperatorCode = Text(e, "OperatorCode"),
                Notes = Text(e, "Notes"),
                Dwell = ReadDwell(e, name),
                Points = points,
            };
            _rawDescriptions[train] = Text(e, "Description") ?? string.Empty;
            _trains.Add(train);
        }

        private TimingPoint? ReadPoint(XElement trip, int index, string train)
        {
            CheckElements(trip, "Trip", Known.Trip);
            var locationText = Text(trip, "Location");
            if (!Tiploc.TryCreate(locationText, out var location))
            {
                Warn(ImportSeverity.Warning, $"Timing point {index + 1} has no usable location ('{locationText}') and was skipped.", train);
                return null;
            }

            var context = $"{train} at {location}";
            var isPass = ParseBool(Text(trip, "IsPassTime"), "IsPassTime", context) ?? false;
            var activities = new List<TrainActivity>();
            foreach (var activity in trip.Element("Activities")?.Elements() ?? [])
            {
                if (activity.Name.LocalName != "Activity")
                {
                    CountUnknown("Activities/" + activity.Name.LocalName);
                    continue;
                }

                CheckElements(activity, "Activity", Known.Activity);
                if (ParseInt(Text(activity, "Activity"), "Activity", context) is not { } code)
                {
                    Warn(ImportSeverity.Warning, $"An activity at {location} has no activity code and was skipped.", train);
                    continue;
                }

                var kind = TrainActivity.KindFromCode(code);
                if (kind == ActivityKind.Unknown)
                {
                    Warn(ImportSeverity.Info, $"Activity code {code} at {location} is not one Stringline knows; kept as Unknown.", train);
                }

                Headcode? associatedHeadcode = Headcode.TryCreate(Text(activity, "AssociatedTrain"), out var h) ? h : null;
                activities.Add(new TrainActivity(kind, code, associatedHeadcode, Text(activity, "AssociatedUID"), null));
            }

            return new TimingPoint
            {
                Index = index,
                Location = location,

                // SimSig ignores an arrival time on a pass; so do we.
                Arrive = isPass ? null : ParseTime(Text(trip, "ArrTime"), "ArrTime", context),
                Depart = ParseTime(Text(trip, "DepPassTime"), "DepPassTime", context),
                IsPass = isPass,
                Platform = Text(trip, "Platform"),
                Line = Text(trip, "Line"),
                Path = Text(trip, "Path"),
                DownDirection = ParseBool(Text(trip, "DownDirection"), "DownDirection", context),
                EngineeringAllowanceSeconds = ParseAllowance(Text(trip, "EngAllowance"), "EngAllowance", context),
                PathingAllowanceSeconds = ParseAllowance(Text(trip, "PathAllowance"), "PathAllowance", context),
                PerformanceAllowanceSeconds = ParseAllowance(Text(trip, "PerfAllowance"), "PerfAllowance", context),
                DwellSeconds = ParseInt(Text(trip, "DwellTime"), "DwellTime", context),
                RequestPercent = ParseInt(Text(trip, "RequestPercent"), "RequestPercent", context),
                SetDownOnly = ParseBool(Text(trip, "SetDownOnly"), "SetDownOnly", context) ?? false,
                NewCategoryId = Text(trip, "NewCategory"),
                Activities = activities,
            };
        }

        private void CheckTimeOrder(List<TimingPoint> points, string train)
        {
            SimTime? previous = null;
            foreach (var point in points)
            {
                foreach (var time in (ReadOnlySpan<SimTime?>)[point.Arrive, point.Depart])
                {
                    if (time is not { } t)
                    {
                        continue;
                    }

                    if (previous is { } p && t < p)
                    {
                        Warn(ImportSeverity.Info, $"Time goes backwards at {point.Location} ({t} after {p}); kept as booked.", train);
                        return;
                    }

                    previous = t;
                }
            }
        }

        public WttImportResult Finish()
        {
            var header = new TimetableHeader(
                _areaId,
                _simSigVersion,
                _name,
                _description,
                _vMajor is null ? null : string.Join('.', new[] { _vMajor, _vMinor, _vBuild }.Where(v => v is not null)),
                _start ?? SimTime.Zero,
                _finish ?? _start ?? SimTime.Zero,
                _template);
            var categories = _categories.ToDictionary(c => c.Id, StringComparer.Ordinal);
            var trains = _trains.Select(t => Complete(t, categories)).ToList();
            var linked = TrainLinker.Link(trains, Warn);
            var rules = TrainLinker.ResolveRules(_rules, linked, Warn);
            foreach (var train in linked.Where(t => t.SeedGroup is { } g && !_seedGroups.Any(s => string.Equals(s.Id, g, StringComparison.OrdinalIgnoreCase))))
            {
                Warn(ImportSeverity.Info, $"Is seeded in group {train.SeedGroup}, which is not in the timetable header; it is taken as seeded at any start.", train.Id.Value);
            }

            var timetable = new WorkingTimetable(header, _categories, linked, rules) { SeedGroups = [.. _seedGroups] };
            return new WttImportResult(timetable, _warnings, _unknown, _skipped);
        }

        /// <summary>Fills in category defaults and the description, once the header and categories are known.</summary>
        private Train Complete(Train train, Dictionary<string, TrainCategory> categories)
        {
            TrainCategory? category = null;
            if (train.CategoryId is { } id && !categories.TryGetValue(id, out category))
            {
                Warn(ImportSeverity.Info, $"Uses category {id}, which is not in the timetable header.", train.Headcode.Value);
            }

            var description = DescriptionTemplate.Expand(
                _rawDescriptions[train],
                _template,
                new DescriptionTemplate.Fields(
                    train.Headcode.Value,
                    train.OriginTime,
                    train.OriginName,
                    train.DestinationName,
                    train.DestinationTime,
                    train.OperatorCode,
                    category?.Description));
            return category is null
                ? train with { Description = description }
                : train with
                {
                    Description = description,
                    MaxSpeedMph = train.MaxSpeedMph ?? category.MaxSpeedMph,
                    LengthMetres = train.LengthMetres ?? category.LengthMetres,
                    IsFreight = train.IsFreight || category.IsFreight,
                    Dwell = train.Dwell.Or(category.Dwell),
                };
        }

        private static string? Text(XElement parent, string name)
        {
            var value = parent.Element(name)?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private void CheckElements(XElement element, string path, HashSet<string> known)
        {
            foreach (var child in element.Elements())
            {
                if (!known.Contains(child.Name.LocalName))
                {
                    CountUnknown(path + "/" + child.Name.LocalName);
                }
            }
        }

        private void CountUnknown(string path)
        {
            _unknown[path] = _unknown.GetValueOrDefault(path) + 1;
            if (_unknown[path] == 1)
            {
                LogUnknownElement(path);
            }
        }

        /// <summary>SimSig writes <c>True</c>/<c>False</c> in older files and Delphi's <c>-1</c>/<c>0</c> in newer ones.</summary>
        private bool? ParseBool(string? text, string field, string? context)
        {
            switch (text?.ToUpperInvariant())
            {
                case null: return null;
                case "TRUE" or "-1" or "1": return true;
                case "FALSE" or "0": return false;
                default:
                    Warn(ImportSeverity.Info, $"{field} '{text}' is not a yes/no value; ignored.", context);
                    return null;
            }
        }

        private int? ParseInt(string? text, string field, string? context)
        {
            if (text is null)
            {
                return null;
            }

            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            Warn(ImportSeverity.Info, $"{field} '{text}' is not a whole number; ignored.", context);
            return null;
        }

        /// <summary>Times are whole seconds after midnight of the first day; they pass 86 400 for trains after midnight.</summary>
        private SimTime? ParseTime(string? text, string field, string? context) =>
            ParseInt(text, field, context) is { } seconds ? new SimTime(seconds) : null;

        private int ParseAllowance(string? text, string field, string context)
        {
            if (text is null)
            {
                return 0;
            }

            if (double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var units) && units >= 0 && units < 10_000)
            {
                return (int)Math.Round(units * AllowanceUnitSeconds, MidpointRounding.AwayFromZero);
            }

            Warn(ImportSeverity.Info, $"{field} '{text}' is not a valid allowance; ignored.", context);
            return 0;
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "WTT import: {Warning}")]
        private partial void LogWarning(ImportWarning warning);

        [LoggerMessage(Level = LogLevel.Debug, Message = "WTT import: unknown element {Path} ignored")]
        private partial void LogUnknownElement(string path);
    }

    /// <summary>A rule as written, before its trains are resolved.</summary>
    internal sealed record RawRule(int Code, string? TrainUid, string? Train, string? Train2Uid, string? Train2, int? Time, Tiploc? Location);

    /// <summary>Element names the reader reads, or knows and deliberately ignores (anything else is reported as unknown).</summary>
    private static class Known
    {
        /// <summary>Root children with no bearing on the plan: the seed group summary (the groups are read from <c>SeedGroups</c>), scenario options, caution speeds, decisions, class-of-service rules.</summary>
        public static readonly HashSet<string> IgnoredHeader =
            ["SeedGroupSummary", "ScenarioOptions", "CautionSpeedSets", "Decisions", "TTClassOfServiceRules"];

        public static readonly HashSet<string> Category =
            ["Description", "IsFreight", "MaxSpeed", "TrainLength", "AccelBrakeIndex", "SpeedClass", "Electrification", "DwellTimes",
             "CanUseGoodsLines", "PowerToWeightCategory", "CautionSpeedSet"];

        public static readonly HashSet<string> Dwell =
            ["RedSignalMoveOff", "StationForward", "StationReverse", "TerminateForward", "TerminateReverse", "Join", "Divide", "CrewChange"];

        /// <summary>Read, plus performance details the forecaster does not use and saved-game state (Delay, Started, Entry…, Shunt…).</summary>
        public static readonly HashSet<string> Train =
            ["ID", "UID", "Description", "Category", "DepartTime", "EntryPoint", "SeedPoint", "MaxSpeed", "TrainLength", "IsFreight",
             "AsRequired", "OriginName", "DestinationName", "OriginTime", "DestinationTime", "OperatorCode", "Notes", "Trips",
             "RedSignalMoveOff", "StationForward", "StationReverse", "TerminateForward", "TerminateReverse", "Join", "Divide", "CrewChange",
             "AccelBrakeIndex", "AsRequiredPercent", "Electrification", "StartTraction", "SpeedClass", "SpeedClassIndex",
             "PowerToWeightCategory", "CanUseGoodsLines", "ClassOfService", "SeedingGap", "SeedGroup", "BonusScore", "Started",
             "ActualEntryPoint", "AsRequiredTested", "Delay", "DelayWarned", "EntryAlerted", "EntryChoice", "EntryDecision",
             "EntrySpeed", "EntryWarned", "IncrementOnTransfer", "NonARSOnEntry", "PermissionDelayed", "QueuePosition", "RunOnTime",
             "STP", "ShuntInterpose", "ShuntLastEntry", "ShuntMove", "ShuntPhone", "ShuntTimes"];

        /// <summary>Read, plus SimSig routing hints (Auto…, Prev/NextPath…) and stopping details the graph does not need.</summary>
        public static readonly HashSet<string> Trip =
            ["Location", "ArrTime", "DepPassTime", "IsPassTime", "Platform", "Line", "Path", "DownDirection", "EngAllowance",
             "PathAllowance", "PerfAllowance", "DwellTime", "RequestPercent", "SetDownOnly", "NewCategory", "Activities",
             "AutoLine", "AutoPath", "AutoPlatform", "PrevPathEndDown", "NextPathStartDown", "AllowStopsOnThroughLines", "BerthsHere",
             "CrewChange", "SetDownKind", "StopAdjustment", "StopLocation", "WaitForBookedTime"];

        public static readonly HashSet<string> Activity =
            ["Activity", "AssociatedTrain", "AssociatedUID", "ActivityDuration", "Choice", "CrewChangeManualTime", "Decision"];

        public static readonly HashSet<string> SeedGroup = ["ID", "StartTime", "Title"];

        public static readonly HashSet<string> Rule =
            ["Rule", "Train", "Train2", "TrainUID", "Train2UID", "Time", "Location", "Achieved", "TimeElapsed", "XOREntered"];
    }
}
