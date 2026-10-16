using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace StudioEngineSpike.Engine;

/// <summary>Small Media Foundation conveniences shared by the export and encoder modes.</summary>
internal static class MfHelpers
{
    public const int MfESampleAllocatorEmpty = unchecked((int)0xC00D4A3E);
    public const int MfESinkNoSamplesProcessed = unchecked((int)0xC00D4A44);

    private const uint MftEnumAll = 0x1 | 0x2 | 0x4 | 0x10 | 0x20;
    private static readonly object NamesGate = new();
    private static Dictionary<Guid, string>? _transformNames;

    public static string? TryGetString(IMFAttributes attributes, Guid key)
    {
        try
        {
            // Vortice returns an empty string, not an error, for some missing string attributes.
            var value = attributes.GetString(key);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    public static uint? TryGetUInt32(IMFAttributes attributes, Guid key)
    {
        var result = attributes.GetUInt32(key, out var value);
        return result.Success ? value : null;
    }

    public static Guid? TryGetGuid(IMFAttributes attributes, Guid key)
    {
        var result = attributes.GetGUID(key, out var value);
        return result.Success ? value : null;
    }

    /// <summary>"0xC00D36E5 MF_E_INVALID_POSITION" from a SharpGen exception.</summary>
    public static string Describe(Exception exception)
    {
        var message = exception.Message;
        const string marker = "ApiCode: [";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        var name = string.Empty;
        if (start >= 0)
        {
            var end = message.IndexOf(']', start);
            if (end > start)
            {
                name = " " + message[(start + marker.Length)..end].Split('/')[0];
            }
        }

        return $"0x{exception.HResult:X8}{name}";
    }

    public static string CategoryName(Guid category)
    {
        if (category == TransformCategoryGuids.VideoDecoder)
        {
            return "video decoder";
        }

        if (category == TransformCategoryGuids.VideoEncoder)
        {
            return "video encoder";
        }

        if (category == TransformCategoryGuids.VideoProcessor)
        {
            return "video processor";
        }

        if (category == TransformCategoryGuids.VideoEffect)
        {
            return "video effect";
        }

        if (category == TransformCategoryGuids.AudioDecoder)
        {
            return "audio decoder";
        }

        if (category == TransformCategoryGuids.AudioEncoder)
        {
            return "audio encoder";
        }

        if (category == TransformCategoryGuids.AudioEffect)
        {
            return "audio effect";
        }

        return category == Guid.Empty ? "transform" : category.ToString();
    }

    /// <summary>
    /// "category: name [hardware|software, async, D3D11-aware]" for a transform inside a source
    /// reader or sink writer, so the report can state which decoder and encoder actually ran.
    /// </summary>
    public static string DescribeTransform(Guid category, IMFTransform transform)
    {
        var name = "(unnamed)";
        var traits = new List<string>();
        try
        {
            using var attributes = transform.Attributes;
            var clsid = TryGetGuid(attributes, TransformAttributeKeys.MftTransformClsidAttribute);
            if (clsid is { } id && TransformNames().TryGetValue(id, out var known))
            {
                name = known;
            }
            else if (TryGetString(attributes, TransformAttributeKeys.MftFriendlyNameAttribute) is { } friendly)
            {
                name = friendly;
            }
            else if (clsid is { } unknown)
            {
                name = unknown.ToString();
            }

            // Only say "hardware" when the transform is marked as one. The Microsoft H.264 decoder
            // is a software MFT that drives DXVA when it is D3D11-aware and given a device manager.
            if (TryGetString(attributes, TransformAttributeKeys.MftEnumHardwareUrlAttribute) is { } url)
            {
                var vendor = TryGetString(attributes, TransformAttributeKeys.MftEnumHardwareVendorIdAttribute);
                traits.Add($"hardware MFT{(vendor is null ? string.Empty : " " + vendor)}");
                if (name == "(unnamed)")
                {
                    // The hardware URL is the only identity a sink writer's encoder exposes.
                    name = url.Length > 60 ? url[..60] + "…" : url;
                }
            }
            if (TryGetUInt32(attributes, TransformAttributeKeys.TransformAsync) is > 0)
            {
                traits.Add("async");
            }

            if (TryGetUInt32(attributes, TransformAttributeKeys.D3D11Aware) is > 0)
            {
                traits.Add("D3D11-aware");
            }
        }
        catch (SharpGenException)
        {
            traits.Add("no attributes");
        }

        return $"{CategoryName(category)}: {name} [{string.Join(", ", traits)}]";
    }

    /// <summary>CLSID → friendly name for every registered video/audio codec transform.</summary>
    private static Dictionary<Guid, string> TransformNames()
    {
        lock (NamesGate)
        {
            if (_transformNames is not null)
            {
                return _transformNames;
            }

            var names = new Dictionary<Guid, string>();
            foreach (var category in new[]
                     {
                         TransformCategoryGuids.VideoDecoder, TransformCategoryGuids.VideoEncoder, TransformCategoryGuids.VideoProcessor,
                         TransformCategoryGuids.AudioDecoder, TransformCategoryGuids.AudioEncoder, TransformCategoryGuids.VideoEffect,
                     })
            {
                try
                {
                    using var collection = MediaFactory.MFTEnumEx(category, MftEnumAll, null, null);
                    foreach (var activate in collection)
                    {
                        var clsid = TryGetGuid(activate, TransformAttributeKeys.MftTransformClsidAttribute);
                        var name = TryGetString(activate, TransformAttributeKeys.MftFriendlyNameAttribute);
                        if (clsid is { } id && name is not null)
                        {
                            names.TryAdd(id, name);
                        }
                    }
                }
                catch (SharpGenException)
                {
                    // Enumeration is only for nicer names in the report.
                }
            }

            _transformNames = names;
            return names;
        }
    }
}
