using System.Threading.Tasks;

namespace TinyClips.App;

public interface IMediaDevicePermissionService
{
    bool HasCameraAccess { get; }

    bool HasMicrophoneAccess { get; }

    Task<bool> RequestCameraAccessAsync();

    Task<bool> RequestMicrophoneAccessAsync();
}
