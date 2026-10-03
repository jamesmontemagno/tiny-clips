using System.Runtime.InteropServices;
using StudioEngineSpike.Engine;
using Vortice.MediaFoundation;

namespace StudioEngineSpike.Modes;

/// <summary>Prints what the spike is running on: runtime flavour, adapter, and the H.264 MFTs Media Foundation offers.</summary>
internal static class InfoMode
{
    private const uint MftEnumFlagSync = 0x1;
    private const uint MftEnumFlagAsync = 0x2;
    private const uint MftEnumFlagHardware = 0x4;
    private const uint MftEnumFlagLocal = 0x10;
    private const uint MftEnumFlagTranscodeOnly = 0x20;
    private const uint MftEnumFlagSortAndFilter = 0x40;

    public static int Run(SpikeOptions options)
    {
        using var report = options.OpenReport();
        using var graphics = GraphicsDevice.Create();
        SpikeEnvironment.Describe(report, graphics);

        MediaFactory.MFStartup(true).CheckError();
        try
        {
            var h264 = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
            var hevc = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.Hevc };
            var aac = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };
            List(report, "H.264 encoders", TransformCategoryGuids.VideoEncoder, null, h264);
            List(report, "HEVC encoders", TransformCategoryGuids.VideoEncoder, null, hevc);
            List(report, "H.264 decoders", TransformCategoryGuids.VideoDecoder, h264, null);
            List(report, "AAC encoders", TransformCategoryGuids.AudioEncoder, null, aac);
            List(report, "AAC decoders", TransformCategoryGuids.AudioDecoder, aac, null);
        }
        finally
        {
            MediaFactory.MFShutdown();
        }

        return 0;
    }

    private static void List(Report report, string title, Guid category, RegisterTypeInfo? input, RegisterTypeInfo? output)
    {
        report.Section(title);
        const uint flags = MftEnumFlagSync | MftEnumFlagAsync | MftEnumFlagHardware | MftEnumFlagLocal | MftEnumFlagTranscodeOnly | MftEnumFlagSortAndFilter;
        using var collection = MediaFactory.MFTEnumEx(category, flags, input, output);
        var count = 0;
        foreach (var activate in collection)
        {
            count++;
            var name = MfHelpers.TryGetString(activate, TransformAttributeKeys.MftFriendlyNameAttribute) ?? "(unnamed)";
            var hardwareUrl = MfHelpers.TryGetString(activate, TransformAttributeKeys.MftEnumHardwareUrlAttribute);
            var transformFlags = MfHelpers.TryGetUInt32(activate, TransformAttributeKeys.TransformFlagsAttribute) ?? 0;
            var kind = hardwareUrl is not null || (transformFlags & MftEnumFlagHardware) != 0 ? "hardware" : "software";
            var model = (transformFlags & MftEnumFlagAsync) != 0 ? "async" : "sync";
            report.Line($"  {name}  [{kind}, {model}, flags 0x{transformFlags:X}]");
        }

        if (count == 0)
        {
            report.Line("  (none)");
        }
    }
}

/// <summary>The header every mode's report starts with.</summary>
internal static class SpikeEnvironment
{
    public static void Describe(Report report, GraphicsDevice graphics)
    {
        report.Section("Environment");
        report.Line($"time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.Line($"runtime: {SpikeOptions.RuntimeFlavor}, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
        report.Line($"os: {RuntimeInformation.OSDescription}");
        report.Line($"cpu: {Environment.ProcessorCount} logical processors");
        report.Line($"adapter: {graphics.AdapterName} (vendor 0x{graphics.AdapterVendorId:X4}), dedicated {graphics.DedicatedVideoMemory / 1024 / 1024} MB, shared {graphics.SharedSystemMemory / 1024 / 1024} MB");
        report.Line($"d3d11: feature level {graphics.Device.FeatureLevel}, video support {(graphics.VideoSupport ? "yes" : "NO")}, multithread protected");
    }
}
