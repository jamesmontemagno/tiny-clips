using System.Buffers.Binary;
using System.Text;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// What the reader of a file's index does with an index that is not what it should be: parts one
/// inside the other without end, a file of nothing but tracks, a part that does not fit the part
/// it is in, a table that is shorter or longer than it says. It reads all of an index or none
/// of it. What it cannot be sure of it refuses, with the reason, and the file then plays as
/// every file did before: on the grid.
/// </summary>
public sealed partial class StudioPreviewFrameTimesTests
{
    // The times of Whole(): 35 ms of nothing, then four frames a thirtieth apart that are stored
    // out of the order they are shown in, the first of them shown two thirtieths into the track.
    // Every part of the index has a say in them, so that a part passed over shows.
    private static readonly long[] WholeStarts = [1_016_667, 1_350_000, 1_683_333, 2_016_667];

    private const long WholeEnd = 2_350_000;

    /// <summary>The parts of an index that hold other parts.</summary>
    private static readonly string[] Containers = ["moov", "trak", "edts", "mdia", "minf", "stbl"];

    /// <summary>Every part that has one place, and the part that is its place. "file" is the file itself.</summary>
    private static readonly (string Part, string Place)[] Places =
    [
        ("moov", "file"), ("moof", "file"),
        ("mvhd", "moov"), ("mvex", "moov"), ("trak", "moov"),
        ("edts", "trak"), ("mdia", "trak"),
        ("elst", "edts"),
        ("mdhd", "mdia"), ("minf", "mdia"),
        ("stbl", "minf"),
        ("stts", "stbl"), ("ctts", "stbl"),
    ];

    /// <summary>The parts the reader looks into or reads.</summary>
    private static readonly string[] ReadParts = ["moov", "mvhd", "trak", "edts", "elst", "mdia", "mdhd", "hdlr", "minf", "stbl", "stts", "ctts"];

    /// <summary>Changes a named part of <see cref="Whole"/> as the file is put together.</summary>
    private delegate byte[] Change(string part, byte[] bytes);

    [Fact]
    public void TheFileTheseTestsDamage_IsReadWhole_AndEveryPartOfItsIndexHasASayInItsTimes()
    {
        var times = Read(Whole(), out var problem);

        Assert.Null(problem);
        Assert.Equal(WholeStarts, Starts(times!));
        Assert.Equal(WholeEnd, times!.End);

        // Without its edit list every frame is 35 ms earlier, and without its offsets two
        // thirtieths: a reading that lost either would be another file's.
        Assert.Equal(new long[] { 666_667, 1_000_000, 1_333_333, 1_666_667 }, Starts(Read(Whole(At("edts", _ => [])), out _)!));
        Assert.Equal(new long[] { 350_000, 683_333, 1_016_667, 1_350_000 }, Starts(Read(Whole(At("ctts", _ => [])), out _)!));

        // The order of a track's parts is the writer's to choose.
        Assert.Equal(WholeStarts, Starts(Read(Whole(editsLast: true), out _)!));
    }

    // ----------------------------------------------------------------------------------------
    // Depth and breadth: what a file can ask the reader to do
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("trak", "trak")]
    [InlineData("edts", "moov")]
    [InlineData("mdia", "moov")]
    [InlineData("minf", "moov")]
    [InlineData("stbl", "moov")]
    public void PartsOneInsideTheOtherWithoutEnd_AreRefusedAtTheFirstThatIsOutOfPlace(string part, string metIn)
    {
        // A hundred thousand of them, each holding the next, in eight hundred kilobytes of file. A
        // reader that goes into whatever it meets goes on until its stack is used up, and that
        // ends the process: it cannot be caught. This one looks for each part where it belongs
        // and nowhere else, so it calls nothing a second time.
        var nest = Nest(part, 100_000, []);

        foreach (var file in new[] { Whole(At("moov", Holding(nest))), Whole(At("moov", HoldingFirst(nest))) })
        {
            Assert.Null(Read(file, out var problem));
            Assert.Equal($"its index is damaged: '{part}' has no place in '{metIn}'", problem);
        }
    }

    [Theory]
    [InlineData("edts")]
    [InlineData("mdia")]
    [InlineData("minf")]
    [InlineData("stbl")]
    public void ANestInsideThePartOfItsOwnName_IsRefusedOneLevelDown(string part)
    {
        // The first of them is where it belongs, in the video track; what it holds is not.
        var file = Whole(At(part, Holding(Nest(part, 100_000, []))));

        Assert.Null(Read(file, out var problem));
        Assert.Equal($"its index is damaged: '{part}' has no place in '{part}'", problem);
    }

    [Fact]
    public void ANestOfPartsThatAreNotRead_IsNotGoneInto()
    {
        // The same depth in a part the reader has no use for, wherever it is: passed over whole.
        foreach (var where in Containers)
        {
            var times = Read(Whole(At(where, Holding(Nest("udta", 100_000, [])))), out var problem);

            Assert.Null(problem);
            Assert.Equal(WholeStarts, Starts(times!));
        }
    }

    [Fact]
    public void AFileOfNothingButTracks_IsRefusedAtTheSixtyFifth()
    {
        // A hundred thousand empty tracks in eight hundred kilobytes. A file has a handful.
        var file = Concat(Box("ftyp", Ascii("mp42\0\0\0\0")), Box("moov", Concat(Mvhd(1000), Repeat(Box("trak", []), 100_000))));

        Assert.Null(Read(file, out var problem));
        Assert.Equal("it has more than 64 tracks", problem);
    }

    [Fact]
    public void AsManyTracksAsAFileMayHave_AreRead_AndOneMoreIsNot()
    {
        const int Most = StudioPreviewFrameTimes.MostTracks;
        Assert.Equal(64, Most);
        var first = Box("ftyp", Ascii("mp42\0\0\0\0"));
        var video = Video(30000, null, Stts((2, Frame30)));

        var times = Read(Concat(first, Movie(48000, [.. Enumerable.Repeat(Sound(), Most - 1), video])), out var problem);
        Assert.Null(problem);
        Assert.Equal(new long[] { 0, 333_333 }, Starts(times!));

        Assert.Null(Read(Concat(first, Movie(48000, [.. Enumerable.Repeat(Sound(), Most), video])), out problem));
        Assert.Equal("it has more than 64 tracks", problem);
    }

    [Fact]
    public void AFileInMorePartsThanAFileIsIn_IsRefused()
    {
        const int Most = StudioPreviewFrameTimes.MostParts;
        var first = Box("ftyp", Ascii("mp42\0\0\0\0"));
        var index = Movie(1000, Video(30000, null, Stts((2, Frame30))));

        // As many as there may be, the index the last of them: read.
        var times = Read(Concat(first, Repeat(Box("free", []), Most - 2), index), out var problem);
        Assert.Null(problem);
        Assert.Equal(2, times!.Count);

        Assert.Null(Read(Concat(first, Repeat(Box("free", []), Most - 1), index), out problem));
        Assert.Equal($"it is in more than {Most} parts", problem);
    }

    [Fact]
    public void APartOfTheIndexInMorePartsThanItHas_IsRefused_AndEachPartIsCountedByItself()
    {
        const int Most = StudioPreviewFrameTimes.MostParts;
        var filler = Box("free", []);

        // How many parts each has of its own in Whole().
        foreach (var (where, own) in new[] { ("moov", 3), ("trak", 3), ("edts", 1), ("mdia", 3), ("minf", 1), ("stbl", 3) })
        {
            var times = Read(Whole(At(where, Holding(Repeat(filler, Most - own)))), out var problem);
            Assert.Null(problem);
            Assert.Equal(WholeStarts, Starts(times!));

            Assert.Null(Read(Whole(At(where, Holding(Repeat(filler, Most - own + 1)))), out problem));
            Assert.Equal($"its index is damaged: '{where}' is in more than {Most} parts", problem);
        }

        // Counted each by itself: all six nearly full are one file, and it is read.
        var all = Whole((part, bytes) => Containers.Contains(part) ? Holding(Repeat(filler, Most - 3))(bytes) : bytes);
        Assert.Equal(WholeStarts, Starts(Read(all, out _)!));
    }

    [Fact]
    public void MoreFramesThanAFileHas_AreRefused_BeforeAnythingIsMadeForThem()
    {
        // Ten hours at 120 frames a second.
        Assert.Equal(10 * 60 * 60 * 120, StudioPreviewFrameTimes.MostFrames);
        const uint Most = (uint)StudioPreviewFrameTimes.MostFrames;
        var why = $"it has more than {Most} frames";

        Assert.Null(Read(File(null, Stts((Most + 1, Frame30))), out var problem));
        Assert.Equal(why, problem);

        // In runs that are each short of it.
        Assert.Null(Read(File(null, Stts((4_000_000, Frame30), (320_001, Frame30))), out problem));
        Assert.Equal(why, problem);

        // As many as the table's numbers can say, three times over: counted, and no room made for them.
        Assert.Null(Read(File(null, Stts((uint.MaxValue, Frame30), (uint.MaxValue, Frame30), (uint.MaxValue, Frame30))), out problem));
        Assert.Equal(why, problem);
    }

    [Fact]
    public void AnHourOfVideo_IsRead()
    {
        var times = Read(File(null, Stts((108_000, Frame30))), out var problem);

        Assert.Null(problem);
        Assert.Equal(108_000, times!.Count);
        Assert.Equal(35_999_666_667, times.Start(107_999));
        Assert.Equal(36_000_000_000, times.End);
    }

    [Fact]
    public void TryCreate_RefusesMoreFramesThanAFileHas()
    {
        var starts = new long[StudioPreviewFrameTimes.MostFrames + 1];

        Assert.Null(StudioPreviewFrameTimes.TryCreate(starts, 0, out var problem));
        Assert.Equal($"it has {starts.Length} frames", problem);
    }

    [Fact]
    public void AnIndexLargerThanAnIndexIs_IsRefused_AndNothingOfItIsRead()
    {
        // One byte more than the most. The file says so in the eight bytes before it, and those
        // are all that is asked for: no room is made for it.
        var length = 8 + StudioPreviewFrameTimes.LargestIndex + 1;
        using var file = new HollowFile(length, (0, Concat(U32((uint)length), Ascii("moov"))));

        Assert.Null(StudioPreviewFrameTimes.TryRead(file, out var problem));
        Assert.Equal("its index is too large", problem);
        Assert.Equal(8, file.BytesRead);

        // The same with a length written in 64 bits, of a size that 32 bits cannot say.
        const long Five = 5L * 1024 * 1024 * 1024;
        using var larger = new HollowFile(Five, (0, Concat(U32(1), Ascii("moov"), U64((ulong)Five))));

        Assert.Null(StudioPreviewFrameTimes.TryRead(larger, out problem));
        Assert.Equal("its index is too large", problem);
        Assert.Equal(16, larger.BytesRead);
    }

    [Fact]
    public void AFileOfMoreThanFourGigabytes_IsReadWhereItsIndexIs()
    {
        // Six gigabytes of data, whose length takes 64 bits, and the index after them.
        const long Data = 6L * 1024 * 1024 * 1024;
        var first = Box("ftyp", Ascii("mp42\0\0\0\0"));
        var data = Concat(U32(1), Ascii("mdat"), U64((ulong)(16 + Data)));
        var index = Movie(1000, Video(30000, null, Stts((3, Frame30))));
        using var file = new HollowFile(first.Length + 16 + Data + index.Length, (0, first), (first.Length, data), (first.Length + 16 + Data, index));

        var times = StudioPreviewFrameTimes.TryRead(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(new long[] { 0, 333_333, 666_667 }, Starts(times!));

        // The parts' names and lengths, and the index: nothing of the data.
        Assert.Equal(8 + 16 + index.Length, file.BytesRead);
    }

    // ----------------------------------------------------------------------------------------
    // A damaged index is refused, not read in part
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void APartThatDoesNotFit_BeforeTheOffsets_RefusesTheFile_WhichIsNotReadWithoutThem()
    {
        // The sample table holds its times, a part whose length is wrong, and then the offsets
        // that say when each frame is shown. Stopping at the bad part without a word would
        // leave the times of decoding standing for the times of showing.
        var file = Whole(At("stts", FollowedBy(Misfit(4))));

        Assert.Null(Read(file, out var problem));
        Assert.Equal("its index is damaged: a part of 'stbl' does not fit it", problem);
    }

    [Fact]
    public void APartThatDoesNotFit_BeforeTheEditList_RefusesTheFile_WhichIsNotReadWithoutItsLead()
    {
        // The track holds its media, a part whose length is wrong, and then its edit list.
        var file = Whole(At("mdia", FollowedBy(Misfit(4))), editsLast: true);

        Assert.Null(Read(file, out var problem));
        Assert.Equal("its index is damaged: a part of 'trak' does not fit it", problem);
    }

    [Fact]
    public void APartWhoseLengthIsWrong_RefusesTheFile_WhereverItIsAndWhateverItSays()
    {
        foreach (var where in Containers)
        {
            // Too short to be a part at all (1 is the mark for a length of 64 bits, which is
            // then 0 here), or longer than a file can be.
            foreach (var says in new uint[] { 0, 1, 2, 7, 0x7FFF_FFFF, 0xFFFF_FFFF })
            {
                foreach (var file in new[] { Whole(At(where, HoldingFirst(Misfit(says)))), Whole(At(where, Holding(Misfit(says)))) })
                {
                    Assert.Null(Read(file, out var problem));
                    Assert.Equal($"its index is damaged: a part of '{where}' does not fit it", problem);
                }
            }

            // One byte more than there is left for it, and the mark for 64 bits with no room for them.
            foreach (var last in new[] { Misfit(25), Misfit(1, contents: 0) })
            {
                Assert.Null(Read(Whole(At(where, Holding(last))), out var problem));
                Assert.Equal($"its index is damaged: a part of '{where}' does not fit it", problem);
            }
        }
    }

    [Fact]
    public void BytesLeftOverAtTheEndOfAPart_RefuseTheFile()
    {
        foreach (var where in Containers)
        {
            for (var bytes = 1; bytes < 8; bytes++)
            {
                var over = new byte[bytes];
                Array.Fill(over, (byte)0xA5);

                Assert.Null(Read(Whole(At(where, Holding(over))), out var problem));
                Assert.Equal($"its index is damaged: bytes are left over in '{where}'", problem);
            }
        }
    }

    [Theory]
    [InlineData("elst: nothing but its kind", "its index is damaged: 'elst' is cut short")]
    [InlineData("elst: an entry short", "its index is damaged: 'elst' is cut short")]
    [InlineData("elst: part of an entry short", "its index is damaged: 'elst' is cut short")]
    [InlineData("elst: an entry more than it says", "its index is damaged: 'elst' is longer than it says")]
    [InlineData("elst: a byte more than it says", "its index is damaged: 'elst' is longer than it says")]
    [InlineData("ctts: nothing but its kind", "its index is damaged: 'ctts' is cut short")]
    [InlineData("ctts: an entry short", "its index is damaged: 'ctts' is cut short")]
    [InlineData("ctts: an entry more than it says", "its index is damaged: 'ctts' is longer than it says")]
    [InlineData("ctts: for fewer frames", "its table of when frames are shown is for fewer frames than it has")]
    [InlineData("ctts: for more frames", "its table of when frames are shown is for more frames than it has")]
    [InlineData("stts: nothing but its kind", "its table of frame times is cut short")]
    [InlineData("stts: an entry more than it says", "its index is damaged: 'stts' is longer than it says")]
    [InlineData("mvhd: nothing in it", "its index is damaged: 'mvhd' is cut short")]
    [InlineData("mvhd: cut short", "its index is damaged: 'mvhd' is cut short")]
    [InlineData("mdhd: cut short", "its index is damaged: 'mdhd' is cut short")]
    [InlineData("hdlr: cut short", "its index is damaged: 'hdlr' is cut short")]
    [InlineData("mvhd: another kind", "its index has a 'mvhd' of a kind that is not known")]
    [InlineData("mdhd: another kind", "its index has a 'mdhd' of a kind that is not known")]
    [InlineData("elst: another kind", "its index has a 'elst' of a kind that is not known")]
    [InlineData("stts: another kind", "its index has a 'stts' of a kind that is not known")]
    [InlineData("ctts: another kind", "its index has a 'ctts' of a kind that is not known")]
    public void ATableOrAHeaderThatIsNotWhatItSays_RefusesTheFile(string what, string why)
    {
        var file = what switch
        {
            "elst: nothing but its kind" => Whole(At("elst", _ => Box("elst", new byte[4]))),
            "elst: an entry short" => Whole(At("elst", Without(12))),
            "elst: part of an entry short" => Whole(At("elst", Without(5))),
            "elst: an entry more than it says" => Whole(At("elst", Holding(new byte[12]))),
            "elst: a byte more than it says" => Whole(At("elst", Holding(new byte[1]))),
            "ctts: nothing but its kind" => Whole(At("ctts", _ => Box("ctts", new byte[4]))),
            "ctts: an entry short" => Whole(At("ctts", Without(8))),
            "ctts: an entry more than it says" => Whole(At("ctts", Holding(new byte[8]))),
            "ctts: for fewer frames" => Whole(At("ctts", _ => Ctts(0, (1, 6000), (1, 12_000)))),
            "ctts: for more frames" => Whole(At("ctts", _ => Ctts(0, (1, 6000), (1, 12_000), (3, 3000)))),
            "stts: nothing but its kind" => Whole(At("stts", _ => Box("stts", new byte[4]))),
            "stts: an entry more than it says" => Whole(At("stts", Holding(new byte[8]))),
            "mvhd: nothing in it" => Whole(At("mvhd", _ => Box("mvhd", []))),
            "mvhd: cut short" => Whole(At("mvhd", _ => Box("mvhd", new byte[15]))),
            "mdhd: cut short" => Whole(At("mdhd", _ => Box("mdhd", new byte[15]))),
            "hdlr: cut short" => Whole(At("hdlr", _ => Box("hdlr", new byte[11]))),
            "mvhd: another kind" => Whole(At("mvhd", OfKind(2))),
            "mdhd: another kind" => Whole(At("mdhd", OfKind(2))),
            "elst: another kind" => Whole(At("elst", OfKind(2))),
            "stts: another kind" => Whole(At("stts", OfKind(1))),
            "ctts: another kind" => Whole(At("ctts", OfKind(2))),
            _ => throw new ArgumentException(what),
        };

        Assert.Null(Read(file, out var problem));
        Assert.Equal(why, problem);
    }

    [Theory]
    [InlineData("mvhd", "moov")]
    [InlineData("edts", "trak")]
    [InlineData("elst", "edts")]
    [InlineData("mdia", "trak")]
    [InlineData("mdhd", "mdia")]
    [InlineData("hdlr", "mdia")]
    [InlineData("minf", "mdia")]
    [InlineData("stbl", "minf")]
    [InlineData("stts", "stbl")]
    [InlineData("ctts", "stbl")]
    public void APartThereIsOneOf_MetTwice_RefusesTheFile(string part, string where)
    {
        Assert.Null(Read(Whole(At(part, Twice)), out var problem));
        Assert.Equal($"its index is damaged: '{where}' has '{part}' twice", problem);
    }

    [Fact]
    public void APartMetWhereItHasNoPlace_RefusesTheFile_WhateverPartAndWherever()
    {
        var tried = 0;
        foreach (var where in Containers)
        {
            foreach (var (part, place) in Places)
            {
                if (place == where)
                {
                    continue;
                }

                tried++;
                Assert.Null(Read(Whole(At(where, Holding(Box(part, [])))), out var problem));
                Assert.Equal($"its index is damaged: '{part}' has no place in '{where}'", problem);
            }
        }

        Assert.Equal(67, tried);
    }

    [Fact]
    public void APartOfTheIndexOutsideIt_RefusesTheFile()
    {
        var whole = Whole();
        foreach (var (part, place) in Places)
        {
            if (place == "file")
            {
                continue;
            }

            // After the index, and before it: after the first part, which is sixteen bytes.
            foreach (var file in new[] { Concat(whole, Box(part, [])), Concat(whole[..16], Box(part, []), whole[16..]) })
            {
                Assert.Null(Read(file, out var problem));
                Assert.Equal($"its index is damaged: '{part}' is outside it", problem);
            }
        }
    }

    [Fact]
    public void LengthsWrittenIn64Bits_AreReadInsideTheIndexToo()
    {
        // Every part of the file's index at once, the index itself among them; then each by itself.
        var times = Read(Whole((_, bytes) => InLong(bytes)), out var problem);
        Assert.Null(problem);
        Assert.Equal(WholeStarts, Starts(times!));
        foreach (var part in ReadParts)
        {
            Assert.Equal(WholeStarts, Starts(Read(Whole(At(part, InLong)), out _)!));
        }

        // A length of 64 bits that is wrong is as wrong as one of 32: too short to hold its own
        // sixteen bytes, more than is left, more than a file can be.
        foreach (var says in new ulong[] { 0, 15, (ulong)int.MaxValue + 1, 1UL << 63, ulong.MaxValue })
        {
            Assert.Null(Read(Whole(At("stts", bytes => InLong(bytes, says))), out problem));
            Assert.Equal("its index is damaged: a part of 'stbl' does not fit it", problem);
        }
    }

    [Theory]
    [InlineData("a fragment before the index")]
    [InlineData("a fragment after the index")]
    [InlineData("the index says there are fragments")]
    [InlineData("a fragment and no index")]
    public void AFileInFragments_IsRefused_WhereverItSaysSo(string what)
    {
        var whole = Whole();
        var fragment = Box("moof", new byte[8]);
        var file = what switch
        {
            // Neither of these two says so in its index: only going through all of the file finds the fragment.
            "a fragment before the index" => Concat(whole[..16], fragment, whole[16..]),
            "a fragment after the index" => Concat(whole, fragment, Box("mdat", new byte[8])),
            "the index says there are fragments" => Whole(At("moov", Holding(Box("mvex", new byte[8])))),
            _ => Concat(whole[..16], fragment, Box("mdat", new byte[8])),
        };

        Assert.Null(Read(file, out var problem));
        Assert.Equal("its index is in fragments", problem);
    }

    [Fact]
    public void AFileWithTwoIndexes_IsRefused()
    {
        var file = Concat(Whole(), Movie(1000, Video(30000, null, Stts((2, Frame30)))));

        Assert.Null(Read(file, out var problem));
        Assert.Equal("it has two indexes", problem);
    }

    [Fact]
    public void TwoVideoTracks_AreRefused_ForWhichOfThemAPlayerShowsIsNotKnown()
    {
        var first = Box("ftyp", Ascii("mp42\0\0\0\0"));
        var one = Video(30000, null, Stts((2, Frame30)));
        var other = Video(30000, null, Stts((3, 2 * Frame30)));

        foreach (var tracks in new[] { new[] { one, other }, new[] { other, Sound(), one }, new[] { one, one } })
        {
            Assert.Null(Read(Concat(first, Movie(48000, tracks)), out var problem));
            Assert.Equal("it has more than one video track", problem);
        }

        // Tracks of other kinds beside the video are no second video.
        var times = Read(Concat(first, Movie(48000, Sound("text"), one, Sound(), Sound("meta"))), out var none);
        Assert.Null(none);
        Assert.Equal(new long[] { 0, 333_333 }, Starts(times!));
    }

    [Fact]
    public void WhatATrackIs_IsSaidByTheHandlerOfItsMedia_NotByTheOneBesideItsData()
    {
        // As QuickTime lays a track out: a second handler, in minf, that says where the data is.
        var times = Read(Whole(At("stbl", Behind(Full("hdlr", 0, Ascii("dhlr"), Ascii("alis"), new byte[13])))), out var problem);
        Assert.Null(problem);
        Assert.Equal(WholeStarts, Starts(times!));

        // And it is not asked, whatever it says: a track whose media is sound is no video.
        var sound = Whole((part, bytes) => part switch
        {
            "hdlr" => Hdlr("soun"),
            "stbl" => Concat(Full("hdlr", 0, Ascii("dhlr"), Ascii("vide"), new byte[13]), bytes),
            _ => bytes,
        });
        Assert.Null(Read(sound, out problem));
        Assert.Equal("it has no video", problem);
    }

    [Fact]
    public void WhatFollowsTheIndex_IsGoneThroughToo()
    {
        var whole = Whole();

        // Fewer bytes than a part's length and name take are no part, and say nothing.
        for (var bytes = 1; bytes < 8; bytes++)
        {
            Assert.Equal(WholeStarts, Starts(Read(Concat(whole, new byte[bytes]), out _)!));
        }

        // The last part of a file may leave its length out: it goes to the end. The index may be that part.
        Assert.Equal(WholeStarts, Starts(Read(Concat(whole, Saying(0)(Box("free", new byte[100]))), out _)!));
        Assert.Equal(WholeStarts, Starts(Read(Whole(At("moov", Saying(0))), out _)!));

        // A part that says it is longer than what is left of the file: the file was cut off, or
        // is not what it seems. Also when the index came before it and was whole.
        var indexFirst = Concat(whole[..16], whole[32..]);
        Assert.Equal(WholeStarts, Starts(Read(indexFirst, out _)!));
        foreach (var file in new[]
        {
            Concat(whole, Saying(109)(Box("free", new byte[100]))),
            Concat(indexFirst, Saying(2000)(Box("mdat", new byte[64]))),
            Concat(indexFirst, U32(1), Ascii("mdat"), U64(ulong.MaxValue), new byte[64]),
            Concat(indexFirst, U32(1), Ascii("mdat"), U64(15), new byte[64]),
            Concat(indexFirst, U32(1), Ascii("mdat"), new byte[4]),
        })
        {
            Assert.Null(Read(file, out var problem));
            Assert.Equal("a part of it does not fit the file", problem);
        }
    }

    // ----------------------------------------------------------------------------------------
    // Parts moved about at random
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void TheFileBuiltPartByPart_IsTheFileTheOtherTestsDamage_WhateverTheOrderOfItsParts()
    {
        Assert.Equal(Whole(), Written(Tree()));
        Assert.Equal(Whole(editsLast: true), Written(Tree(editsLast: true)));

        for (var layout = 0; layout < 16; layout++)
        {
            var times = Read(Written(Tree((layout & 1) != 0, (layout & 2) != 0, (layout & 4) != 0, (layout & 8) != 0)), out var problem);

            Assert.Null(problem);
            Assert.Equal(WholeStarts, Starts(times!));
            Assert.Equal(WholeEnd, times!.End);
        }
    }

    [Fact]
    public void PartsMovedAboutAtRandom_AreRefused_OrReadAsTheFileWas()
    {
        // The parts of a good file's index, changed in the ways a damaged file's are: a length
        // that says something else, a part twice, a part inside another, parts in another
        // order, the file cut off, a part that does not fit, bytes left over. Each time the
        // file is refused, with a reason, or read to exactly the times it had: never to
        // others. What is not in here, because it makes another file and not a damaged one: a
        // part under another name, a part taken out with the lengths around it set right, and
        // a part the reader has no use for whose length takes in the part after it. Such a
        // part hides what follows it, and nothing in the file says that it should not.
        var random = new Random(23);
        var refused = 0;
        var same = 0;
        for (var round = 0; round < 6000; round++)
        {
            var file = Tree(random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0);
            var (what, expected, cut) = Damage(file, random);
            var bytes = Written(file);
            if (cut >= 0)
            {
                bytes = bytes[..(cut % bytes.Length)];
            }

            var times = Read(bytes, out var problem);
            if (times is null)
            {
                Assert.False(string.IsNullOrEmpty(problem), what);
                Assert.True(expected != Outcome.Same, $"round {round}, {what}: refused, because {problem}");
                refused++;
                continue;
            }

            Assert.Null(problem);
            Assert.True(expected != Outcome.Refused, $"round {round}, {what}: read");
            Assert.True(
                WholeStarts.AsSpan().SequenceEqual(Starts(times)) && times.End == WholeEnd,
                $"round {round}, {what}: read as {string.Join(' ', Starts(times))}, ending at {times.End}");
            same++;
        }

        // Both happen, so that neither half of the sentence is empty.
        Assert.True(refused >= 3500, $"{refused} refused");
        Assert.True(same >= 600, $"{same} read as the file was");
    }

    /// <summary>What a damage has to come to.</summary>
    private enum Outcome
    {
        /// <summary>Refused, or read as the file was.</summary>
        Either,

        Refused,

        /// <summary>Read as the file was: it is no damage at all.</summary>
        Same,
    }

    /// <summary>Damages a file in one way, and says what was done, what has to come of it, and where to cut the file off (-1: nowhere).</summary>
    private static (string What, Outcome Expected, int Cut) Damage(List<Node> file, Random random)
    {
        var read = new List<(List<Node> In, int At)>();
        Find(file, read, node => Array.IndexOf(ReadParts, node.Name) >= 0);
        var (siblings, at) = read[random.Next(read.Count)];
        var node = siblings[at];
        var holders = new List<(List<Node> In, int At)>();
        Find(file, holders, part => part.Parts is not null && Array.IndexOf(Containers, part.Name) >= 0);
        var (holderIn, holderAt) = holders[random.Next(holders.Count)];
        var holder = holderIn[holderAt];
        switch (random.Next(8))
        {
            case 0:
            {
                // A part the reader reads says it has another length. The bytes stay.
                var truth = node.Write().Length;
                var next = at + 1 < siblings.Count ? siblings[at + 1].Write().Length : 8;
                long says = random.Next(8) switch
                {
                    0 => random.Next(0, 8),
                    1 => truth - random.Next(1, Math.Max(2, truth - 7)),
                    2 => truth + random.Next(1, 64),
                    3 => node.Parts is { Count: > 0 } parts ? 8 + parts.Take(random.Next(parts.Count)).Sum(part => part.Write().Length) : truth - 1,
                    4 => truth + next,
                    5 => 0x7FFF_FFFF,
                    6 => 0xFFFF_FFFF,
                    _ => random.Next(8, truth + 200),
                };
                if (says == truth)
                {
                    says++;
                }

                node.Says = (uint)says;
                return ($"'{node.Name}' of {truth} bytes says {says}", Outcome.Either, -1);
            }

            case 1:
                // A part the reader reads, twice. Two sound tracks are a file like any other.
                siblings.Insert(at + 1, node.Copy());
                return ($"'{node.Name}' twice", node.Name == "trak" && !IsVideo(node) ? Outcome.Same : Outcome.Refused, -1);

            case 2:
            {
                // A part the reader reads, inside a part of the index. Only a header or a table
                // inside one of its own name can pass, where it is one of the sound track's:
                // what it holds is then read as its numbers.
                var around = Places[random.Next(Places.Length)].Part;
                siblings[at] = Node.Of(around, node);
                return ($"'{node.Name}' inside a '{around}'", around == node.Name && node.Parts is null ? Outcome.Either : Outcome.Refused, -1);
            }

            case 3:
            {
                // The same, many deep.
                var around = Containers[random.Next(1, Containers.Length)];
                var depth = random.Next(2, 60);
                var inner = node;
                for (var level = 0; level < depth; level++)
                {
                    inner = Node.Of(around, inner);
                }

                siblings[at] = inner;
                return ($"'{node.Name}' inside {depth} of '{around}'", Outcome.Refused, -1);
            }

            case 4:
            {
                // Two parts that are next to each other change places: of the file, or of any part of it.
                var lists = new List<List<Node>> { file };
                var all = new List<(List<Node> In, int At)>();
                Find(file, all, part => part.Parts is { Count: > 1 });
                lists.AddRange(all.Select(found => found.In[found.At].Parts!));
                var list = lists[random.Next(lists.Count)];
                var first = random.Next(list.Count - 1);
                (list[first], list[first + 1]) = (list[first + 1], list[first]);
                return ($"'{list[first + 1].Name}' and '{list[first].Name}' change places", Outcome.Same, -1);
            }

            case 5:
                // The file is cut off somewhere.
                return ("cut off", Outcome.Either, random.Next(1, int.MaxValue));

            case 6:
            {
                // A part whose length cannot be right, somewhere in a part of the index.
                var parts = holder.Parts!;
                var where = random.Next(parts.Count + 1);
                var bad = Node.Of(Box("free", new byte[8 * random.Next(3)]));
                var left = bad.Write().Length + parts.Skip(where).Sum(part => part.Write().Length);
                bad.Says = random.Next(4) switch
                {
                    0 => (uint)random.Next(0, 8),
                    1 => (uint)(left + random.Next(1, 100)),
                    2 => 0x7FFF_FFFF,
                    _ => 0xFFFF_FFFF,
                };
                parts.Insert(where, bad);
                return ($"a part that says {bad.Says} at {where} in '{holder.Name}', where {left} bytes are left", Outcome.Refused, -1);
            }

            default:
            {
                // Bytes left over at the end of a part of the index.
                var over = new byte[random.Next(1, 8)];
                random.NextBytes(over);
                holder.Tail = over;
                return ($"{over.Length} bytes left over in '{holder.Name}'", Outcome.Refused, -1);
            }
        }
    }

    private static bool IsVideo(Node track) => Encoding.ASCII.GetString(track.Write()).Contains("vide", StringComparison.Ordinal);

    private static void Find(List<Node> parts, List<(List<Node> In, int At)> found, Func<Node, bool> wanted)
    {
        for (var at = 0; at < parts.Count; at++)
        {
            if (wanted(parts[at]))
            {
                found.Add((parts, at));
            }

            if (parts[at].Parts is { } inner)
            {
                Find(inner, found, wanted);
            }
        }
    }

    private static byte[] Written(List<Node> file) => Concat([.. file.Select(part => part.Write())]);

    /// <summary>The file of <see cref="Whole"/> as parts that can be moved about, in one of the orders a writer may choose.</summary>
    private static List<Node> Tree(bool editsLast = false, bool indexFirst = false, bool soundFirst = false, bool handlerLast = false)
    {
        var table = Node.Of("stbl", Node.Of(Stts((4, 3000))), Node.Of(Ctts(0, (1, 6000), (1, 12_000), (2, 3000))), Node.Of(Stsz()));
        var information = Node.Of("minf", table);
        var media = handlerLast
            ? Node.Of("mdia", Node.Of(Mdhd(90000)), information, Node.Of(Hdlr("vide")))
            : Node.Of("mdia", Node.Of(Mdhd(90000)), Node.Of(Hdlr("vide")), information);
        var edits = Node.Of("edts", Node.Of(Elst(0, (1680, -1), (144_000, 0))));
        var video = editsLast ? Node.Of("trak", Node.Of(Tkhd(1)), media, edits) : Node.Of("trak", Node.Of(Tkhd(1)), edits, media);
        var sound = Node.Of("trak", Node.Of(Tkhd(2)), Node.Of("mdia", Node.Of(Mdhd(48000)), Node.Of(Hdlr("soun")), Node.Of("minf", Node.Of("stbl", Node.Of(SoundStts())))));
        var index = soundFirst ? Node.Of("moov", Node.Of(Mvhd(48000)), sound, video) : Node.Of("moov", Node.Of(Mvhd(48000)), video, sound);
        var first = Node.Of(Box("ftyp", Ascii("mp42\0\0\0\0")));
        return indexFirst
            ? [first, index, Node.Of(Box("free", new byte[8])), Node.Of(Box("mdat", new byte[16]))]
            : [first, Node.Of(Box("mdat", new byte[8])), index];
    }

    /// <summary>A part of a file: what it holds, or the parts it holds, and what can be wrong with it.</summary>
    private sealed class Node
    {
        public string Name { get; private init; } = string.Empty;

        /// <summary>What a part that holds no parts holds.</summary>
        public byte[] Contents { get; private init; } = [];

        public List<Node>? Parts { get; private init; }

        /// <summary>The length written on it, where that is not its own.</summary>
        public uint? Says { get; set; }

        /// <summary>Bytes after the last of its parts that are no part.</summary>
        public byte[] Tail { get; set; } = [];

        public static Node Of(byte[] box) => new() { Name = NameOf(box), Contents = box[8..] };

        public static Node Of(string name, params Node[] parts) => new() { Name = name, Parts = [.. parts] };

        public Node Copy() => new() { Name = Name, Contents = Contents, Parts = Parts?.ConvertAll(part => part.Copy()), Says = Says, Tail = Tail };

        public byte[] Write()
        {
            var box = Box(Name, Concat(Parts is null ? Contents : Concat([.. Parts.Select(part => part.Write())]), Tail));
            if (Says is { } says)
            {
                BinaryPrimitives.WriteUInt32BigEndian(box, says);
            }

            return box;
        }
    }

    // ----------------------------------------------------------------------------------------
    // The file these tests damage, and the ways of damaging it
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A file with every part the reader reads: a lead in its edit list, frames stored out of
    /// order, and a sound track beside the video. <paramref name="change"/> is handed each named
    /// part of the video track and of the index as it is put together, and what it gives back
    /// is what is written, so that a part which grows or goes leaves the parts around it true.
    /// </summary>
    private static byte[] Whole(Change? change = null, bool editsLast = false)
    {
        byte[] Part(string name, byte[] bytes) => change is null ? bytes : change(name, bytes);

        var table = Part("stbl", Box("stbl", Concat(
            Part("stts", Stts((4, 3000))),
            Part("ctts", Ctts(0, (1, 6000), (1, 12_000), (2, 3000))),
            Part("stsz", Stsz()))));
        var information = Part("minf", Box("minf", table));
        var media = Part("mdia", Box("mdia", Concat(Part("mdhd", Mdhd(90000)), Part("hdlr", Hdlr("vide")), information)));
        var edits = Part("edts", Box("edts", Part("elst", Elst(0, (1680, -1), (144_000, 0)))));
        var header = Part("tkhd", Tkhd(1));
        var track = Part("trak", Box("trak", editsLast ? Concat(header, media, edits) : Concat(header, edits, media)));
        var index = Part("moov", Box("moov", Concat(Part("mvhd", Mvhd(48000)), track, Sound())));
        return Concat(Box("ftyp", Ascii("mp42\0\0\0\0")), Box("mdat", new byte[8]), index);
    }

    private static Change At(string part, Func<byte[], byte[]> change) => (name, bytes) => name == part ? change(bytes) : bytes;

    /// <summary>The part, and another after it.</summary>
    private static Func<byte[], byte[]> FollowedBy(byte[] next) => box => Concat(box, next);

    /// <summary>The part, and another before it.</summary>
    private static Func<byte[], byte[]> Behind(byte[] before) => box => Concat(before, box);

    /// <summary>The part with more in it, after what it has, and saying so.</summary>
    private static Func<byte[], byte[]> Holding(byte[] more) => box => Box(NameOf(box), Concat(box[8..], more));

    /// <summary>The part with more in it, before what it has, and saying so.</summary>
    private static Func<byte[], byte[]> HoldingFirst(byte[] more) => box => Box(NameOf(box), Concat(more, box[8..]));

    /// <summary>The part with so many bytes fewer at its end, and saying so.</summary>
    private static Func<byte[], byte[]> Without(int bytes) => box => Box(NameOf(box), box[8..^bytes]);

    /// <summary>The part as it is, with another length written on it.</summary>
    private static Func<byte[], byte[]> Saying(uint length) => box =>
    {
        var copy = (byte[])box.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy, length);
        return copy;
    };

    /// <summary>The header or table with another number for its kind.</summary>
    private static Func<byte[], byte[]> OfKind(byte kind) => box =>
    {
        var copy = (byte[])box.Clone();
        copy[8] = kind;
        return copy;
    };

    private static byte[] Twice(byte[] box) => Concat(box, box);

    /// <summary>The part with its length written in 64 bits.</summary>
    private static byte[] InLong(byte[] box) => LongBox(NameOf(box), box[8..]);

    /// <summary>The same, with another length written on it.</summary>
    private static byte[] InLong(byte[] box, ulong says)
    {
        var copy = InLong(box);
        BinaryPrimitives.WriteUInt64BigEndian(copy.AsSpan(8), says);
        return copy;
    }

    private static string NameOf(byte[] box) => Encoding.ASCII.GetString(box, 4, 4);

    /// <summary>A part the reader has no use for, with a length written on it that is not its own.</summary>
    private static byte[] Misfit(uint says, int contents = 16) => Saying(says)(Box("free", new byte[contents]));

    /// <summary>
    /// Parts of one name, each holding the next, with something in the innermost. Written in one
    /// go: putting a part around a part a hundred thousand times would copy the lot each time.
    /// </summary>
    private static byte[] Nest(string name, int depth, byte[] innermost)
    {
        var bytes = new byte[(depth * 8) + innermost.Length];
        var letters = Encoding.ASCII.GetBytes(name);
        for (var level = 0; level < depth; level++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(level * 8), (uint)(bytes.Length - (level * 8)));
            letters.CopyTo(bytes, (level * 8) + 4);
        }

        innermost.CopyTo(bytes, depth * 8);
        return bytes;
    }

    private static byte[] Repeat(byte[] part, int count)
    {
        var bytes = new byte[part.Length * count];
        for (var index = 0; index < count; index++)
        {
            part.CopyTo(bytes, index * part.Length);
        }

        return bytes;
    }

    /// <summary>
    /// A file that is mostly not there: it has a length, and bytes where they are given. The rest
    /// reads as zeros. It counts what is read of it.
    /// </summary>
    private sealed class HollowFile(long length, params (long At, byte[] Bytes)[] pieces) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Math.Max(0, length - Position));
            var into = buffer[..count];
            into.Clear();
            foreach (var (at, bytes) in pieces)
            {
                var from = Math.Max(Position, at);
                var to = Math.Min(Position + count, at + bytes.Length);
                if (from < to)
                {
                    bytes.AsSpan((int)(from - at), (int)(to - from)).CopyTo(into[(int)(from - Position)..]);
                }
            }

            Position += count;
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset,
        };

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
