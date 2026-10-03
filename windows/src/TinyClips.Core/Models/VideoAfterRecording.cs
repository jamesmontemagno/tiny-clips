namespace TinyClips.Core.Models;

/// <summary>What happens to a video once its recording stops.</summary>
public enum VideoAfterRecording
{
    /// <summary>Save the video without opening an editor.</summary>
    Save = 0,

    /// <summary>Open the video trimmer before saving.</summary>
    Trimmer = 1,

    /// <summary>Keep the recording as a Tiny Clips Studio project and open it in Studio.</summary>
    Studio = 2,
}
