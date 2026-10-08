using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyClips.Core.Studio;

namespace TinyClips.Core.Tests;

/// <summary>
/// A project saved as a folder on a Mac, opened on Windows. The project file is the one a Mac
/// wrote (<c>shared/studio/fixtures/folder</c>); the recordings beside it are a few bytes each,
/// except in the one test that is given a real folder to read.
/// </summary>
public sealed class StudioProjectFolderFromAMacTests : StudioProjectFolderTestBase
{
    /// <summary>Names a real folder saved on a Mac for <see cref="ARealFolderSavedOnAMac_OpensWhole_IsLeftAsItWas_AndIsSavedAgain"/>. The folder is only read.</summary>
    public const string FolderVariable = "TINYCLIPS_STUDIO_MAC_FOLDER";

    /// <summary>Names a file that test writes what it found into. Without it the test only passes or fails.</summary>
    public const string FindingsVariable = "TINYCLIPS_STUDIO_MAC_FOLDER_FINDINGS";

    private const string IdInTheMacsFile = "c12c76bc-d410-44d5-9db4-00c166b6bc34";

    private static string MacsProjectFile =>
        Path.Combine(AppContext.BaseDirectory, "StudioFixtures", "folder", "saved-on-macos-1.9.0.tinyclips");

    [Fact]
    public void TheMacsProjectFile_IsHereAsTheMacWroteIt()
    {
        var bytes = File.ReadAllBytes(MacsProjectFile);
        var text = Encoding.UTF8.GetString(bytes);

        // Lines that end as a Mac ends them, a space on both sides of each colon, no mark at
        // the start: a checkout that changed any of it would make the tests below test less.
        Assert.Equal(3344, bytes.Length);
        Assert.StartsWith("{\n  \"app\" : {\n    \"platform\" : \"macos\",", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void AProjectFileAsAMacWritesIt_OpensWithEverythingItSays()
    {
        var folder = MakeFolderAroundTheMacsFile("From A Mac");

        var opened = Open(folder);

        Assert.NotEqual(IdInTheMacsFile, opened.Id);
        Assert.Equal("TinyClips 2026-10-07 at 15.39.57", opened.Name);
        Assert.Equal("macos", opened.App.Platform);
        Assert.Equal("1.9.0", opened.App.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 22, 39, 57, TimeSpan.Zero), opened.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 22, 41, 56, TimeSpan.Zero), opened.ModifiedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 22, 39, 57, TimeSpan.Zero), opened.LastOpenedAt);
        Assert.Empty(opened.Exports);

        Assert.Equal((2880, 1800, 30d, 14.483333333333333), (opened.Sources.Screen.Width, opened.Sources.Screen.Height, opened.Sources.Screen.FrameRate, opened.Sources.Screen.Duration));
        Assert.Equal((1280, 720, 13.095, 1.408335792), (opened.Sources.Camera!.Width, opened.Sources.Camera.Height, opened.Sources.Camera.Duration, opened.Sources.Camera.StartOffset));
        Assert.Equal("events.json", opened.Sources.Events);

        Assert.Equal([(StudioLayout.Bubble, 0d, StudioTransitionKind.Cut), (StudioLayout.SideBySide, 5.568259401875741, StudioTransitionKind.Morph)],
            opened.Scenes.Select(scene => (scene.Layout, scene.Start, scene.Transition.Kind)));
        Assert.Equal([(5.996854206053528, 8.820395502020313, 2d, StudioZoomFocusMode.Cursor), (9.604701343323693, 12.604701343323693, 3.5, StudioZoomFocusMode.Point)],
            opened.Zooms.Select(zoom => (zoom.Start, zoom.End, zoom.Scale, zoom.Focus.Mode)));
        Assert.Equal((1.408335792, 12.950710527413255), (opened.Edits.TrimStart, opened.Edits.TrimEnd!.Value));
        Assert.Equal([(2.5704111409215593, 4.510652003169483)], opened.Edits.Cuts.Select(cut => (cut.Start, cut.End)));
        Assert.Equal([(6.278711285031138, 7.860499147390271, 2d)], opened.Edits.Speed.Select(speed => (speed.Start, speed.End, speed.Rate)));
        Assert.Equal(("#0003FF", 86d, 6d, false), (opened.Overlays.Clicks.Color, opened.Overlays.Clicks.Size, opened.Overlays.Clicks.StrokeWidth, opened.Overlays.Clicks.Enabled));

        // Two sound tracks, which this version does not know the names of and keeps.
        var written = JsonNode.Parse(File.ReadAllText(Store.GetPaths(opened.Id).ProjectJsonPath))!;
        Assert.Equal(["system", "microphone"], written["sources"]!["screen"]!["audioTracks"]!.AsArray().Select(track => track!.GetValue<string>()));

        var paths = Store.GetPaths(opened);
        Assert.Equal(["camera.mp4", "events.json", "poster.jpg", "project.json", "screen.mp4"], Names(paths.ProjectDirectory));
        Assert.Equal("screen", File.ReadAllText(paths.ScreenPath));
        Assert.Equal("camera", File.ReadAllText(paths.CameraPath!));
    }

    [Fact]
    public void WhatWindowsWritesOfAMacsProject_SaysWhatTheMacsFileSays_ButForTheId()
    {
        var folder = MakeFolderAroundTheMacsFile("From A Mac");
        var opened = Open(folder);
        var again = Path.Combine(Outside, "And Back");

        Save(opened.Id, again);

        var comparison = ProjectFileComparison.Of(File.ReadAllBytes(MacsProjectFile), File.ReadAllBytes(Path.Combine(again, "And Back.tinyclips")));

        // Nothing the Mac wrote is dropped or changed and nothing is added: the one value that
        // differs is the id the project got when it was opened.
        var id = Assert.Single(comparison.Values);
        Assert.Equal($"id: the Mac's file has \"{IdInTheMacsFile}\", the Windows file has \"{opened.Id}\"", id);

        // Every number and every text is written with the same characters.
        Assert.DoesNotContain(comparison.Writing, line => line.Contains("written differently", StringComparison.Ordinal));
    }

    [Fact]
    public void ARealFolderSavedOnAMac_OpensWhole_IsLeftAsItWas_AndIsSavedAgain()
    {
        var macFolder = Environment.GetEnvironmentVariable(FolderVariable);
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(macFolder),
            $"Set {FolderVariable} to a folder a Mac saved a project to, and {FindingsVariable} to a file to write what was found into. The folder is only read.");
        Assert.True(Directory.Exists(macFolder), $"{FolderVariable} names no folder: {macFolder}");

        var findings = new List<string>();
        var before = Snapshot(macFolder!);
        var macFile = StudioProjectFolder.FindProjectFile(macFolder);
        var macProject = JsonNode.Parse(File.ReadAllText(macFile))!;
        var macId = macProject["id"]!.GetValue<string>();
        string[] recordings =
        [
            macProject["sources"]!["screen"]!["file"]!.GetValue<string>(),
            macProject["sources"]!["camera"]!["file"]!.GetValue<string>(),
            macProject["sources"]!["events"]!.GetValue<string>(),
            "poster.jpg",
        ];
        findings.Add($"The Mac's folder: {Path.GetFileName(macFolder)}");
        findings.AddRange(before.Select(entry => $"  {entry}"));
        findings.Add($"The id in the Mac's file: {macId}");

        // Opened from the file and from the folder: two projects, each with the files whole.
        var fromFile = Open(macFile);
        var fromFolder = Open(macFolder!);
        Assert.Equal(3, new[] { macId, fromFile.Id, fromFolder.Id }.Distinct().Count());
        foreach (var (opened, how) in new[] { (fromFile, "from the .tinyclips file"), (fromFolder, "from the folder") })
        {
            var directory = Store.GetPaths(opened.Id).ProjectDirectory;
            Assert.Empty(opened.Exports);
            Assert.Empty(Store.Load(opened.Id).Exports);
            Assert.Equal(recordings.Append("project.json").Order(StringComparer.Ordinal), Names(directory));
            findings.Add($"Opened {how}: id {opened.Id}, exports {opened.Exports.Length}, name \"{opened.Name}\"");
            foreach (var name in recordings)
            {
                var theirs = Path.Combine(macFolder!, name);
                var ours = Path.Combine(directory, name);
                Assert.Equal(new FileInfo(theirs).Length, new FileInfo(ours).Length);
                Assert.Equal(Hash(theirs), Hash(ours));
                findings.Add($"  {name}: {new FileInfo(ours).Length} bytes, the same bytes as the Mac's");
            }
        }

        Assert.Equal(before, Snapshot(macFolder!));
        findings.Add("The Mac's folder after both: the same names, lengths, write times and attributes as before.");

        // Saved again from the store, under the name the Mac's folder has.
        var again = Path.Combine(Outside, Path.GetFileName(macFolder)!);
        Save(fromFile.Id, again);
        var windowsFile = StudioProjectFolder.FindProjectFile(again);
        Assert.Equal(recordings.Append(Path.GetFileName(macFile)).Order(StringComparer.Ordinal), Names(again));
        findings.Add($"Saved again by Windows: {string.Join(", ", Names(again))}");
        foreach (var name in recordings)
        {
            Assert.Equal(Hash(Path.Combine(macFolder!, name)), Hash(Path.Combine(again, name)));
        }

        findings.Add("  The four files have the same bytes as the Mac's.");
        Assert.Equal(before, Snapshot(macFolder!));

        var comparison = ProjectFileComparison.Of(File.ReadAllBytes(macFile), File.ReadAllBytes(windowsFile));
        findings.Add(string.Empty);
        findings.Add($"The project file, the Mac's against the one Windows wrote. Values that differ, are missing or are added ({comparison.Values.Count}):");
        findings.AddRange(comparison.Values.Select(line => $"  {line}"));
        findings.Add($"How it is written ({comparison.Writing.Count}):");
        findings.AddRange(comparison.Writing.Select(line => $"  {line}"));
        findings.Add(string.Empty);
        findings.Add("The file Windows wrote:");
        findings.Add(File.ReadAllText(windowsFile));

        if (Environment.GetEnvironmentVariable(FindingsVariable) is { Length: > 0 } findingsFile)
        {
            File.WriteAllLines(findingsFile, findings);
        }

        Assert.Equal([$"id: the Mac's file has \"{macId}\", the Windows file has \"{fromFile.Id}\""], comparison.Values);
    }

    /// <summary>A folder with the Mac's project file under the folder's name and a few bytes for each file it names.</summary>
    private string MakeFolderAroundTheMacsFile(string name)
    {
        var folder = Path.Combine(Outside, name);
        Directory.CreateDirectory(folder);
        File.Copy(MacsProjectFile, Path.Combine(folder, name + ".tinyclips"));
        File.WriteAllText(Path.Combine(folder, "screen.mp4"), "screen");
        File.WriteAllText(Path.Combine(folder, "camera.mp4"), "camera");
        File.WriteAllText(Path.Combine(folder, "events.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "poster.jpg"), "poster");
        return folder;
    }

    /// <summary>Everything in a folder with its length, when it was last written, and its attributes, and the same of the folder.</summary>
    private static string[] Snapshot(string folder)
    {
        var root = new DirectoryInfo(folder);
        return root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Append(root)
            .Select(entry => $"{Path.GetRelativePath(folder, entry.FullName)} | {(entry is FileInfo file ? file.Length : -1)} bytes | written {entry.LastWriteTimeUtc:O} | {entry.Attributes}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// What differs between two project files that should say the same: the values, and the way
/// they are written.
/// </summary>
/// <param name="Values">A value that differs, one only the Mac's file has, or one only the Windows file has.</param>
/// <param name="Writing">Differences that leave every value what it was: the order of keys, how a number is written, line ends.</param>
internal sealed record ProjectFileComparison(IReadOnlyList<string> Values, IReadOnlyList<string> Writing)
{
    public static ProjectFileComparison Of(byte[] mac, byte[] windows)
    {
        var values = new List<string>();
        var writing = new List<string>();
        var macText = Encoding.UTF8.GetString(mac);
        var windowsText = Encoding.UTF8.GetString(windows);

        writing.Add($"Length: the Mac's {mac.Length} bytes, Windows' {windows.Length} bytes");
        writing.Add($"A byte order mark at the start: the Mac's {HasMark(mac)}, Windows' {HasMark(windows)}");
        writing.Add($"Line ends: the Mac's {LineEnds(macText)}, Windows' {LineEnds(windowsText)}");
        writing.Add($"Ends with a line end: the Mac's {macText.EndsWith('\n')}, Windows' {windowsText.EndsWith('\n')}");
        writing.Add($"Between a key and its value: the Mac's {Separator(macText)}, Windows' {Separator(windowsText)}");
        writing.Add($"Spaces a level is indented by: the Mac's {Indent(macText)}, Windows' {Indent(windowsText)}");
        writing.Add($"An empty list: the Mac's {EmptyList(macText)}, Windows' {EmptyList(windowsText)}");

        using var macDocument = JsonDocument.Parse(macText);
        using var windowsDocument = JsonDocument.Parse(windowsText);
        var orders = new List<string>();
        Compare(string.Empty, macDocument.RootElement, windowsDocument.RootElement, values, writing, orders);
        writing.Add($"Objects whose keys are in another order: {orders.Count}");
        writing.AddRange(orders);
        return new ProjectFileComparison(values, writing);
    }

    private static void Compare(string path, JsonElement mac, JsonElement windows, List<string> values, List<string> writing, List<string> orders)
    {
        var where = path.Length == 0 ? "(the project)" : path;
        if (mac.ValueKind != windows.ValueKind && !(IsBoolean(mac) && IsBoolean(windows)))
        {
            values.Add($"{where}: the Mac's file has {mac.GetRawText()}, the Windows file has {windows.GetRawText()}");
            return;
        }

        switch (mac.ValueKind)
        {
            case JsonValueKind.Object:
                var macNames = mac.EnumerateObject().Select(property => property.Name).ToArray();
                var windowsNames = windows.EnumerateObject().Select(property => property.Name).ToArray();
                foreach (var property in mac.EnumerateObject())
                {
                    var child = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    if (windows.TryGetProperty(property.Name, out var other))
                    {
                        Compare(child, property.Value, other, values, writing, orders);
                    }
                    else
                    {
                        values.Add($"{child}: only the Mac's file has it: {Flat(property.Value)}");
                    }
                }

                foreach (var property in windows.EnumerateObject().Where(property => !macNames.Contains(property.Name)))
                {
                    values.Add($"{(path.Length == 0 ? property.Name : $"{path}.{property.Name}")}: only the Windows file has it: {Flat(property.Value)}");
                }

                var shared = windowsNames.Where(name => macNames.Contains(name)).ToArray();
                if (!macNames.Where(name => windowsNames.Contains(name)).SequenceEqual(shared))
                {
                    orders.Add($"  {where}: the Mac's {string.Join(", ", macNames)}; Windows' {string.Join(", ", windowsNames)}");
                }

                break;

            case JsonValueKind.Array:
                if (mac.GetArrayLength() != windows.GetArrayLength())
                {
                    values.Add($"{where}: the Mac's file has {mac.GetArrayLength()} in the list, the Windows file has {windows.GetArrayLength()}");
                    break;
                }

                var index = 0;
                foreach (var (first, second) in mac.EnumerateArray().Zip(windows.EnumerateArray()))
                {
                    Compare($"{path}[{index++}]", first, second, values, writing, orders);
                }

                break;

            default:
                var same = mac.ValueKind switch
                {
                    JsonValueKind.Number => mac.GetDouble().Equals(windows.GetDouble()),
                    JsonValueKind.String => mac.GetString() == windows.GetString(),
                    _ => mac.ValueKind == windows.ValueKind,
                };
                if (!same)
                {
                    values.Add($"{where}: the Mac's file has {mac.GetRawText()}, the Windows file has {windows.GetRawText()}");
                }
                else if (mac.GetRawText() != windows.GetRawText())
                {
                    writing.Add($"{where}: the same value written differently: the Mac's {mac.GetRawText()}, Windows' {windows.GetRawText()}");
                }

                break;
        }
    }

    private static bool IsBoolean(JsonElement element) => element.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static string Flat(JsonElement element) => Regex.Replace(element.GetRawText(), "\\s+", " ");

    private static bool HasMark(byte[] bytes) => bytes is [0xEF, 0xBB, 0xBF, ..];

    private static string LineEnds(string text)
    {
        var both = Regex.Matches(text, "\r\n").Count;
        var feeds = text.Count(character => character == '\n') - both;
        return $"{both} CRLF and {feeds} LF";
    }

    private static string Separator(string text) =>
        (Regex.IsMatch(text, "\" : "), Regex.IsMatch(text, "\": ")) switch
        {
            (true, false) => "space, colon, space",
            (false, true) => "colon, space",
            (true, true) => "both ways",
            _ => "neither way",
        };

    private static int Indent(string text) => Regex.Match(text, "\n( +)\"").Groups[1].Length;

    private static string Flatten(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

    private static string EmptyList(string text) =>
        Regex.Match(text, "\\[\\s*\\]") is { Success: true } match ? $"\"{Flatten(match.Value)}\"" : "none in the file";
}
