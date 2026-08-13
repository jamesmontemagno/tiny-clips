namespace TinyClips.App;

internal static class BuildFlavor
{
#if TINYCLIPS_STORE_BUILD
    public const bool IsStoreBuild = true;
#else
    public const bool IsStoreBuild = false;
#endif

    public const bool IsDirectBuild = !IsStoreBuild;
}
