using System.Buffers.Binary;
using System.Text;
using TinyClips.Core.Studio.Preview;

namespace TinyClips.Core.Tests;

/// <summary>
/// The table of a clip's frame times, and the reading of it from an MP4 file's index. The files
/// are written here box by box, as Media Foundation's writer lays a recording out and as other
/// writers do, with nothing of the reader's in them. What the reader does with an index that is
/// damaged, or made to wear it out, is in the other half of this class.
/// </summary>
public sealed partial class StudioPreviewFrameTimesTests
{
    // A thirtieth of a second in the units of a 30000th: what Media Foundation's writer counts a 30 a second track in.
    private const uint Frame30 = 1000;

    [Fact]
    public void FrameAt_IsTheLastFrameThatHasBegun_AndMinusOneBeforeTheFirst()
    {
        var times = Table(350_000, 683_333, 1_350_000, 1_683_333);

        Assert.Equal(-1, times.FrameAt(0));
        Assert.Equal(-1, times.FrameAt(349_999));
        Assert.Equal(0, times.FrameAt(350_000));
        Assert.Equal(0, times.FrameAt(683_332));
        Assert.Equal(1, times.FrameAt(683_333));

        // The frame before a gap shows until the next one begins.
        Assert.Equal(1, times.FrameAt(1_349_999));
        Assert.Equal(2, times.FrameAt(1_350_000));
        Assert.Equal(3, times.FrameAt(1_683_333));
        Assert.Equal(3, times.FrameAt(long.MaxValue / 2));
    }

    [Fact]
    public void FrameAt_HasNoAllowance_AFrameHasBegunWhenTheTimeHasReachedItsStart()
    {
        var times = Table(0, 333_333, 666_667);

        // One unit before a frame's start it has not begun; at its start it has. That is the
        // export's rule, and a table needs no margin for rounding as the grid does.
        Assert.Equal(0, times.FrameAt(333_332));
        Assert.Equal(1, times.FrameAt(333_333));
        Assert.Equal(1, times.FrameAt(666_666));
        Assert.Equal(2, times.FrameAt(666_667));
    }

    [Fact]
    public void Start_End_AndTheUsualSpacing()
    {
        var times = Table(350_000, 683_333, 1_350_000, 1_683_333, 2_016_667);

        Assert.Equal(5, times.Count);
        Assert.Equal(350_000, times.Start(0));
        Assert.Equal(2_016_667, times.Start(4));

        // A number outside the file stands for the first or the last frame.
        Assert.Equal(350_000, times.Start(-3));
        Assert.Equal(2_016_667, times.Start(99));

        // The middle one of the spacings, which a gap does not move; and the end, where none is given, one of those after the last frame.
        Assert.Equal(333_334, times.TypicalSpacing);
        Assert.Equal(2_016_667 + 333_334, times.End);
    }

    [Fact]
    public void TryCreate_KeepsTheEndItIsGiven_WhenThatIsAfterTheLastFrame()
    {
        var times = StudioPreviewFrameTimes.TryCreate([0, 333_333], 700_000, out var problem);

        Assert.Null(problem);
        Assert.Equal(700_000, times!.End);
    }

    [Fact]
    public void TryCreate_OneFrame_LastsAsLongAsItIsSaidTo_OrAThirtiethOfASecond()
    {
        Assert.Equal(400_000, StudioPreviewFrameTimes.TryCreate([0], 400_000, out _)!.End);
        Assert.Equal(333_333, StudioPreviewFrameTimes.TryCreate([0], 0, out _)!.End);
    }

    [Fact]
    public void TryCreate_RefusesTimesAFileCannotHave()
    {
        Assert.Null(StudioPreviewFrameTimes.TryCreate([], 0, out var none));
        Assert.Equal("it has no frames", none);

        Assert.Null(StudioPreviewFrameTimes.TryCreate([-1, 333_333], 0, out var before));
        Assert.Equal("its first frame begins before the start", before);

        Assert.Null(StudioPreviewFrameTimes.TryCreate([0, 333_333, 333_333], 0, out var twice));
        Assert.Equal("frame 2 does not begin after frame 1", twice);

        Assert.Null(StudioPreviewFrameTimes.TryCreate([0, 666_667, 333_333], 0, out var back));
        Assert.Equal("frame 2 does not begin after frame 1", back);
    }

    // ----------------------------------------------------------------------------------------
    // From a file's index
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void ARecordingAsMediaFoundationWritesIt_GivesEachFramesTime()
    {
        // The index after the data, no edit list, the times as runs of equal lengths: three frames
        // a thirtieth apart, one that lasts two thirtieths (the slot after it has no frame), two more.
        var file = Concat(
            Box("ftyp", Ascii("mp42\0\0\0\0")),
            Box("uuid", new byte[24]),
            Box("mdat", new byte[40]),
            Movie(48000, Video(30000, null, Stts((3, Frame30), (1, 2 * Frame30), (2, Frame30))), Sound()));

        var times = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(6, times!.Count);
        Assert.Equal(new long[] { 0, 333_333, 666_667, 1_000_000, 1_666_667, 2_000_000 }, Starts(times));
        Assert.Equal(2_333_333, times.End);
        Assert.Equal(333_333, times.TypicalSpacing);
    }

    [Fact]
    public void TimesThatAreNotOnAGrid_ComeOutAsTheyWereWritten()
    {
        // A camera's times: 0, 2 and 4 ms off the thirtieths, and a stall of 369 ms.
        var file = Concat(
            Box("ftyp", Ascii("mp42\0\0\0\0")),
            Box("mdat", new byte[8]),
            Movie(30000, Video(30000, null, Stts((1, 1060), (1, 1060), (1, 880), (1, 11070), (1, 1000), (1, 1000)))));

        var times = Read(file, out _);

        Assert.Equal(new long[] { 0, 353_333, 706_667, 1_000_000, 4_690_000, 5_023_333 }, Starts(times!));
        Assert.Equal(353_333, times!.TypicalSpacing);
    }

    [Fact]
    public void AStretchOfNothingBeforeTheVideo_MovesEveryFrameLater()
    {
        // 35 ms of nothing in the movie's units (a 48000th), then the track from its beginning.
        var file = Concat(
            Box("ftyp", Ascii("mp42\0\0\0\0")),
            Box("mdat", new byte[8]),
            Movie(48000, Video(30000, Elst(0, (1680, -1), (144_000, 0)), Stts((4, Frame30))), Sound()));

        var times = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(new long[] { 350_000, 683_333, 1_016_667, 1_350_000 }, Starts(times!));
        Assert.Equal(1_683_333, times!.End);
    }

    [Fact]
    public void AnEditThatStartsTheTrackAtItsBeginning_ChangesNothing_WhereverTheIndexIs()
    {
        // As ffmpeg writes a file for streaming: the index before the data, one edit from 0.
        var file = Concat(
            Box("ftyp", Ascii("isom\0\0\0\0")),
            Movie(1000, Video(15360, Elst(0, (12_000, 0)), Stts((360, 512)))),
            Box("free", new byte[8]),
            Box("mdat", new byte[16]));

        var times = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(360, times!.Count);
        Assert.Equal(0, times.Start(0));
        Assert.Equal(333_333, times.Start(1));
        Assert.Equal(119_666_667, times.Start(359));
        Assert.Equal(120_000_000, times.End);
    }

    [Fact]
    public void FramesThatAreStoredOutOfOrder_ComeOutInTheOrderTheyAreShown()
    {
        // Decoded in the order 0 3 1 2, each shown some frames after it is decoded, and the edit
        // list starts the track where its first frame is shown.
        var file = Concat(
            Box("ftyp", Ascii("isom\0\0\0\0")),
            Movie(1000, Video(90000, Elst(0, (200, 6000)), Stts((4, 3000)), Ctts(0, (1, 6000), (1, 12_000), (2, 3000)))),
            Box("mdat", new byte[16]));

        var times = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(new long[] { 0, 333_333, 666_667, 1_000_000 }, Starts(times!));
        Assert.Equal(1_333_333, times!.End);
    }

    [Fact]
    public void OffsetsThatGoBackwards_AreRead()
    {
        // The other way of writing the same: offsets that can be negative, and no edit list.
        var file = Concat(
            Box("ftyp", Ascii("isom\0\0\0\0")),
            Movie(1000, Video(90000, null, Stts((4, 3000)), Ctts(1, (1, 0), (1, 6000), (2, unchecked((uint)-3000))))),
            Box("mdat", new byte[16]));

        var times = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(new long[] { 0, 333_333, 666_667, 1_000_000 }, Starts(times!));
    }

    [Fact]
    public void TheLongFormsOfTheHeaders_AreRead()
    {
        // Times of 64 bits in the movie's header, the track's and the edit list, and a data box
        // whose length is written in 64 bits.
        var video = Box("trak", Concat(
            Box("edts", Full("elst", 1, U32(2), U64(1680), U64(ulong.MaxValue), U16(1), U16(0), U64(144_000), U64(0), U16(1), U16(0))),
            Box("mdia", Concat(
                Full("mdhd", 1, U64(0), U64(0), U32(30000), U64(4000), U16(0), U16(0)),
                Full("hdlr", 0, U32(0), Ascii("vide"), new byte[13]),
                Box("minf", Box("stbl", Stts((4, Frame30))))))));
        var file = Concat(
            Box("ftyp", Ascii("mp42\0\0\0\0")),
            LongBox("mdat", new byte[12]),
            Box("moov", Concat(Full("mvhd", 1, U64(0), U64(0), U32(48000), U64(192_000), new byte[80]), video)));

        var times = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(new long[] { 350_000, 683_333, 1_016_667, 1_350_000 }, Starts(times!));
    }

    [Fact]
    public void TheVideoIsFound_WhenTheSoundComesFirst()
    {
        var file = Concat(
            Box("ftyp", Ascii("mp42\0\0\0\0")),
            Box("mdat", new byte[8]),
            Movie(48000, Sound(), Video(30000, null, Stts((2, Frame30)))));

        Assert.Equal(new long[] { 0, 333_333 }, Starts(Read(file, out _)!));
    }

    [Theory]
    [InlineData("no index")]
    [InlineData("fragments")]
    [InlineData("no video")]
    [InlineData("two parts")]
    [InlineData("speed")]
    [InlineData("nothing in the middle")]
    [InlineData("only nothing")]
    [InlineData("inside")]
    [InlineData("before the track")]
    [InlineData("cut short")]
    [InlineData("same time")]
    [InlineData("no frames")]
    [InlineData("no time units")]
    public void AFileWhoseTimesItCannotBeSureOf_IsNotRead_AndSaysWhy(string what)
    {
        var (file, why) = what switch
        {
            "no index" => (Concat(Box("ftyp", Ascii("mp42\0\0\0\0")), Box("mdat", new byte[64])), "it has no index"),
            "fragments" => (
                Concat(Box("ftyp", Ascii("mp42\0\0\0\0")), Box("moov", Concat(Mvhd(1000), VideoTrack(30000, null, Stts((2, Frame30))), Box("mvex", new byte[8]))), Box("moof", new byte[8])),
                "its index is in fragments"),
            "no video" => (Concat(Box("ftyp", Ascii("mp42\0\0\0\0")), Movie(48000, Sound())), "it has no video"),
            "two parts" => (File(Elst(0, (1000, 0), (1000, 60_000)), Stts((90, Frame30))), "its edit list has more than one part"),
            "speed" => (File(Concat(Full("elst", 0, U32(1), U32(1000), U32(0), U16(2), U16(0))), Stts((90, Frame30))), "its edit list changes the speed"),
            "nothing in the middle" => (File(Elst(0, (1000, 0), (500, -1)), Stts((90, Frame30))), "its edit list has a stretch of nothing in the middle"),
            "only nothing" => (File(Elst(0, (1000, -1)), Stts((90, Frame30))), "its edit list shows nothing of the video"),
            "inside" => (File(Elst(0, (1000, 1500)), Stts((90, Frame30))), "its edit list begins inside the video"),
            "before the track" => (
                Concat(Box("ftyp", Ascii("isom\0\0\0\0")), Movie(1000, Video(90000, null, Stts((4, 3000)), Ctts(1, (1, unchecked((uint)-3000)), (3, 0))))),
                "its first frame is shown before its track begins"),
            "cut short" => (File(null, Full("stts", 0, U32(5), U32(3), U32(Frame30))), "its table of frame times is cut short"),
            "same time" => (File(null, Stts((2, Frame30), (1, 0), (2, Frame30))), "frame 3 does not begin after frame 2"),
            "no frames" => (File(null, Stts()), "it has no frames"),
            "no time units" => (
                Concat(Box("ftyp", Ascii("mp42\0\0\0\0")), Box("moov", Concat(Mvhd(1000), VideoTrack(0, null, Stts((2, Frame30)))))),
                "its index does not say what its times count in"),
            _ => throw new ArgumentException(what),
        };

        Assert.Null(Read(file, out var problem));
        Assert.Equal(why, problem);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WhatIsNotAnMp4File_IsNotRead_AndNothingIsThrown(int which)
    {
        var good = File(null, Stts((90, Frame30)));
        byte[] file = which switch
        {
            0 => [],
            1 => [0, 0, 0, 1, 0x6D, 0x6F, 0x6F, 0x76],
            2 => Concat(U32(0x7FFF_FFFF), Ascii("moov"), new byte[64]),
            _ => good[..(good.Length - 9)],
        };

        Assert.Null(Read(file, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void BytesAtRandom_AreReadOrRefusedWithAReason_AndNothingIsThrown()
    {
        // Every index of a good file with a stretch of it overwritten: read or refused, and never
        // thrown at. This shows no more than that. A few bytes changed inside a number are
        // another number, and a file with other numbers is a file, so whether what was read is
        // what the damage left cannot be said here. What a damaged index has to come to is in
        // the other half of this class: a part that does not fit, a table that is not as long
        // as it says, and PartsMovedAboutAtRandom_AreRefused_OrReadAsTheFileWas, where the
        // answer is known.
        var good = File(Elst(0, (1680, -1), (144_000, 0)), Stts((3, Frame30), (1, 2 * Frame30), (40, Frame30)));
        var random = new Random(11);
        for (var round = 0; round < 3000; round++)
        {
            var file = (byte[])good.Clone();
            var at = random.Next(8, file.Length);
            var length = random.Next(1, 6);
            for (var index = at; index < Math.Min(file.Length, at + length); index++)
            {
                file[index] = (byte)random.Next(256);
            }

            var times = Read(file, out var problem);
            Assert.True(times is not null || problem is not null);
            if (times is not null)
            {
                for (var frame = 1; frame < times.Count; frame++)
                {
                    Assert.True(times.Start(frame) > times.Start(frame - 1));
                }
            }
        }
    }

    [Fact]
    public void AFileOnDisk_IsRead_WhileSomebodyElseHasItOpen_AndAMissingOneIsNot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tc-frame-times-{Guid.NewGuid():N}.mp4");
        try
        {
            System.IO.File.WriteAllBytes(path, File(null, Stts((90, Frame30))));
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                var times = StudioPreviewFrameTimes.TryRead(path, out var problem);
                Assert.Null(problem);
                Assert.Equal(90, times!.Count);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        Assert.Null(StudioPreviewFrameTimes.TryRead(path, out var missing));
        Assert.StartsWith("it could not be read", missing);
    }

    private static StudioPreviewFrameTimes Table(params long[] starts) => StudioPreviewFrameTimes.TryCreate(starts, 0, out _)!;

    private static long[] Starts(StudioPreviewFrameTimes times) => [.. Enumerable.Range(0, times.Count).Select(frame => times.Start(frame))];

    private static StudioPreviewFrameTimes? Read(byte[] file, out string? problem)
    {
        using var stream = new MemoryStream(file);
        return StudioPreviewFrameTimes.TryRead(stream, out problem);
    }

    // ----------------------------------------------------------------------------------------
    // Boxes
    // ----------------------------------------------------------------------------------------

    /// <summary>A file with one video track of a 30000th, the index after the data.</summary>
    private static byte[] File(byte[]? editList, byte[] timeToSample) =>
        Concat(Box("ftyp", Ascii("mp42\0\0\0\0")), Box("mdat", new byte[8]), Movie(1000, Video(30000, editList, timeToSample)));

    private static byte[] Movie(uint timescale, params byte[][] tracks) => Box("moov", Concat([Mvhd(timescale), .. tracks]));

    private static byte[] Mvhd(uint timescale) => Full("mvhd", 0, U32(0), U32(0), U32(timescale), U32(0), new byte[80]);

    private static byte[] Video(uint timescale, byte[]? editList, byte[] timeToSample, byte[]? offsets = null) => VideoTrack(timescale, editList, timeToSample, offsets);

    private static byte[] VideoTrack(uint timescale, byte[]? editList, byte[] timeToSample, byte[]? offsets = null) =>
        Box("trak", Concat(
            Tkhd(1),
            editList is null ? [] : Box("edts", editList),
            Box("mdia", Concat(
                Mdhd(timescale),
                Hdlr("vide"),
                Box("minf", Box("stbl", Concat(timeToSample, offsets ?? [], Stsz())))))));

    /// <summary>A track that is not video: sound, unless another kind is named.</summary>
    private static byte[] Sound(string kind = "soun") =>
        Box("trak", Concat(Tkhd(2), Box("mdia", Concat(Mdhd(48000), Hdlr(kind), Box("minf", Box("stbl", SoundStts()))))));

    private static byte[] Tkhd(uint track) => Full("tkhd", 0, U32(0), U32(0), U32(track), U32(0), U32(0), new byte[60]);

    private static byte[] Mdhd(uint timescale) => Full("mdhd", 0, U32(0), U32(0), U32(timescale), U32(0), U16(0), U16(0));

    /// <summary>What a track is: "vide" for video.</summary>
    private static byte[] Hdlr(string kind) => Full("hdlr", 0, U32(0), Ascii(kind), new byte[13]);

    /// <summary>The sizes of the samples: a table the reader has no use for.</summary>
    private static byte[] Stsz() => Full("stsz", 0, U32(100), U32(1));

    private static byte[] SoundStts() => Full("stts", 0, U32(1), U32(500), U32(960));

    private static byte[] Stts(params (uint Count, uint Delta)[] runs) =>
        Full("stts", 0, [U32((uint)runs.Length), .. runs.SelectMany(run => new[] { U32(run.Count), U32(run.Delta) })]);

    private static byte[] Ctts(byte version, params (uint Count, uint Offset)[] runs) =>
        Full("ctts", version, [U32((uint)runs.Length), .. runs.SelectMany(run => new[] { U32(run.Count), U32(run.Offset) })]);

    /// <summary>An edit list in its short form. A media time of -1 is a stretch of nothing.</summary>
    private static byte[] Elst(byte version, params (uint Duration, int MediaTime)[] edits) =>
        Full("elst", version, [U32((uint)edits.Length), .. edits.SelectMany(edit => new[] { U32(edit.Duration), U32(unchecked((uint)edit.MediaTime)), U16(1), U16(0) })]);

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    /// <summary>A box whose length is written in 64 bits after its name.</summary>
    private static byte[] LongBox(string type, byte[] payload)
    {
        var box = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, 1);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), (ulong)box.Length);
        payload.CopyTo(box, 16);
        return box;
    }

    private static byte[] Full(string type, byte version, params byte[][] fields) => Box(type, Concat([[version, 0, 0, 0], .. fields]));

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
}
