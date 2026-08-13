using System.Threading.Tasks;

namespace TinyClips.App;

public interface IMediaDevicePermissionService
{
    Task<bool> RequestCameraAccessAsync();

    Task<bool> RequestMicrophoneAccessAsync();
}
