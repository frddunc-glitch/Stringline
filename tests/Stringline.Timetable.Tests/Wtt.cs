using System.IO.Compression;
using System.Text;

namespace Stringline.Timetable.Tests;

/// <summary>Builds small synthetic timetables for reader tests.</summary>
internal static class Wtt
{
    public const string Category =
        """
        <TrainCategory ID="CAT1">
          <Description>Class 999</Description>
          <IsFreight>False</IsFreight>
          <MaxSpeed>100</MaxSpeed>
          <TrainLength>120</TrainLength>
          <AccelBrakeIndex>3</AccelBrakeIndex>
          <SpeedClass>8</SpeedClass>
          <Electrification>3</Electrification>
          <DwellTimes><RedSignalMoveOff>12</RedSignalMoveOff><StationForward>30</StationForward><Join>240</Join></DwellTimes>
        </TrainCategory>
        """;

    /// <summary>A full document with the given trains (Timetable elements), categories and rules.</summary>
    public static string Document(string trains, string? categories = Category, string rules = "", string header = "") =>
        $"""
        <SimSigTimetable ID="testsim" Version="5.18">
          <Name>Test timetable</Name>
          <Description>For tests</Description>
          <StartTime>18000</StartTime>
          <FinishTime>90000</FinishTime>
          <VMajor>1</VMajor><VMinor>2</VMinor><VBuild>3</VBuild>
          {header}
          {(categories is null ? "" : $"<TrainCategories>{categories}</TrainCategories>")}
          <Timetables>{trains}</Timetables>
          {(rules.Length == 0 ? "" : $"<TimetableRules>{rules}</TimetableRules>")}
        </SimSigTimetable>
        """;

    /// <summary>A Timetable element. <paramref name="trips"/> are Trip elements; <paramref name="extra"/> goes before Trips.</summary>
    public static string Train(string id, string trips, string extra = "", string? uid = null, string category = "CAT1") =>
        $"""
        <Timetable>
          <ID>{id}</ID>
          {(uid is null ? "" : $"<UID>{uid}</UID>")}
          <Description>{id} test train</Description>
          <Category>{category}</Category>
          {extra}
          <Trips>{trips}</Trips>
        </Timetable>
        """;

    public static string Trip(string location, int? arr = null, int? dep = null, string extra = "") =>
        $"<Trip><Location>{location}</Location>{(dep is null ? "" : $"<DepPassTime>{dep}</DepPassTime>")}{(arr is null ? "" : $"<ArrTime>{arr}</ArrTime>")}{extra}</Trip>";

    public static string Activity(int code, string? train = null, string? uid = null) =>
        $"<Activities><Activity><Activity>{code}</Activity>{(train is null ? "" : $"<AssociatedTrain>{train}</AssociatedTrain>")}{(uid is null ? "" : $"<AssociatedUID>{uid}</AssociatedUID>")}</Activity></Activities>";

    /// <summary>A simple two-point train A→B departing at <paramref name="dep"/>.</summary>
    public static string SimpleTrain(string id, int dep = 36000, string? uid = null, string from = "AAA", string to = "BBB", string fromExtra = "", string toExtra = "") =>
        Train(id, Trip(from, dep: dep, extra: fromExtra) + Trip(to, arr: dep + 300, dep: dep + 300, extra: toExtra), uid: uid);

    public static WttImportResult Read(string xml) => new WttReader().Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "test.xml");

    public static byte[] Zip(string xml, string entryName = "SavedTimetable.xml")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var header = zip.CreateEntry("TimetableHeader.xml");
            using (var w = new StreamWriter(header.Open()))
            {
                w.Write("<SimSigTimetable ID=\"testsim\" />");
            }

            using var s = new StreamWriter(zip.CreateEntry(entryName).Open());
            s.Write(xml);
        }

        return buffer.ToArray();
    }
}
