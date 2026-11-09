using System.Security.Cryptography;

namespace TinyClips.Core.Automation;

/// <summary>
/// Maps native display/window handles to opaque IDs that are stable for the current app session.
/// IDs never expose raw handles; callers must re-validate that the handle still exists before use.
/// </summary>
public sealed class AutomationCaptureTargetRegistry
{
    public const string DisplayPrefix = "display-";
    public const string WindowPrefix = "window-";

    private readonly object _gate = new();
    private readonly Dictionary<nint, string> _displayIds = new();
    private readonly Dictionary<nint, string> _windowIds = new();
    private readonly Dictionary<string, nint> _handles = new(StringComparer.Ordinal);

    public string GetDisplayId(nint handle) => GetOrAdd(_displayIds, handle, DisplayPrefix);

    public string GetWindowId(nint handle) => GetOrAdd(_windowIds, handle, WindowPrefix);

    public bool TryResolveDisplay(string id, out nint handle) => TryResolve(id, DisplayPrefix, out handle);

    public bool TryResolveWindow(string id, out nint handle) => TryResolve(id, WindowPrefix, out handle);

    private string GetOrAdd(Dictionary<nint, string> map, nint handle, string prefix)
    {
        if (handle == 0)
        {
            throw new ArgumentException("Handle must be non-zero.", nameof(handle));
        }

        lock (_gate)
        {
            if (map.TryGetValue(handle, out var existing))
            {
                return existing;
            }

            string id;
            do
            {
                id = prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            }
            while (_handles.ContainsKey(id));

            map[handle] = id;
            _handles[id] = handle;
            return id;
        }
    }

    private bool TryResolve(string id, string prefix, out nint handle)
    {
        handle = 0;
        if (string.IsNullOrEmpty(id) || !id.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_gate)
        {
            return _handles.TryGetValue(id, out handle);
        }
    }
}
