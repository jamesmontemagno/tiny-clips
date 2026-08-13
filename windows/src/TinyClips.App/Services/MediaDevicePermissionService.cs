using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;

namespace TinyClips.App;

public sealed class MediaDevicePermissionService : IMediaDevicePermissionService
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SemaphoreSlim _cameraRequestGate = new(1, 1);
    private readonly SemaphoreSlim _microphoneRequestGate = new(1, 1);

    public MediaDevicePermissionService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Media permission service must be created on the UI thread.");
    }

    public Task<bool> RequestCameraAccessAsync() =>
        RequestAccessAsync(DeviceClass.VideoCapture, StreamingCaptureMode.Video, _cameraRequestGate);

    public Task<bool> RequestMicrophoneAccessAsync() =>
        RequestAccessAsync(DeviceClass.AudioCapture, StreamingCaptureMode.Audio, _microphoneRequestGate);

    private async Task<bool> RequestAccessAsync(
        DeviceClass deviceClass,
        StreamingCaptureMode captureMode,
        SemaphoreSlim requestGate)
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("Media access must be requested from the UI thread.");
        }

        await requestGate.WaitAsync();
        try
        {
            var accessInformation = DeviceAccessInformation.CreateFromDeviceClass(deviceClass);
            if (accessInformation.CurrentStatus == DeviceAccessStatus.Allowed)
            {
                return true;
            }

            using var mediaCapture = new MediaCapture();
            await mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = captureMode,
            });

            return DeviceAccessInformation.CreateFromDeviceClass(deviceClass).CurrentStatus ==
                DeviceAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to request {deviceClass} access: {ex}");
            return DeviceAccessInformation.CreateFromDeviceClass(deviceClass).CurrentStatus ==
                DeviceAccessStatus.Allowed;
        }
        finally
        {
            requestGate.Release();
        }
    }
}
