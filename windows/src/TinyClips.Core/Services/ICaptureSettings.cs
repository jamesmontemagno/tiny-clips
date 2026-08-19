using TinyClips.Core.Models;

namespace TinyClips.Core.Services;

public interface ICaptureSettings
{
    string SaveDirectory { get; set; }
    bool UseDefaultSaveDirectories { get; set; }
    string ScreenshotSaveDirectory { get; set; }
    string VideoSaveDirectory { get; set; }
    string GifSaveDirectory { get; set; }
    AppTheme Theme { get; set; }
    bool CopyScreenshotToClipboard { get; set; }
    bool CopyVideoToClipboard { get; set; }
    bool CopyGifToClipboard { get; set; }
    bool ShowInExplorer { get; set; }
    bool ShowSaveNotifications { get; set; }
    bool LaunchAtLogin { get; set; }
    bool ReopenPickerAfterCapture { get; set; }
    string FileNameTemplate { get; set; }
    double GifFrameRate { get; set; }
    int GifMaxWidth { get; set; }
    int VideoFrameRate { get; set; }
    bool KeepDisplayAwakeWhileRecording { get; set; }
    bool ShowMouseClickVisualsInVideo { get; set; }
    bool ShowMouseClickVisualsInGif { get; set; }
    bool GifMouseClicksUseVideoSettings { get; set; }
    string VideoMouseClickColorHex { get; set; }
    double VideoMouseClickSize { get; set; }
    double VideoMouseClickStrokeWidth { get; set; }
    double VideoMouseClickOpacity { get; set; }
    double VideoMouseClickDuration { get; set; }
    string GifMouseClickColorHex { get; set; }
    double GifMouseClickSize { get; set; }
    double GifMouseClickStrokeWidth { get; set; }
    double GifMouseClickOpacity { get; set; }
    double GifMouseClickDuration { get; set; }
    bool ShowTrimmer { get; set; }
    bool RecordAudio { get; set; }
    bool RecordMicrophone { get; set; }
    string SelectedMicrophoneId { get; set; }
    bool WebcamEnabled { get; set; }
    string SelectedWebcamId { get; set; }
    WebcamShape WebcamShape { get; set; }
    WebcamSizePreset WebcamSizePreset { get; set; }
    WebcamCornerPosition WebcamCornerPosition { get; set; }
    double? WebcamCornerRadius { get; set; }
    bool ShowScreenshotEditor { get; set; }

    /// <summary>
    /// When true, a region screenshot re-captures the screen after the selection overlay closes
    /// (reflects changes made while selecting, slower). When false (default), the frozen frame
    /// shown behind the overlay is cropped and saved directly — no second capture.
    /// </summary>
    bool ScreenshotUsesLiveCapture { get; set; }
    bool ShowGifTrimmer { get; set; }
    bool SaveImmediatelyScreenshot { get; set; }
    bool SaveImmediatelyVideo { get; set; }
    bool SaveImmediatelyGif { get; set; }
    bool ShowScreenshotCapturePicker { get; set; }
    bool ShowScreenshotCapturePickerAfterCapture { get; set; }
    bool ShowVideoCapturePicker { get; set; }
    bool ShowVideoCapturePickerAfterCapture { get; set; }
    bool ShowGifCapturePicker { get; set; }
    bool ShowGifCapturePickerAfterCapture { get; set; }
    string ScreenshotFormat { get; set; }
    int ScreenshotScale { get; set; }
    double JpegQuality { get; set; }
    bool VideoCountdownEnabled { get; set; }
    int VideoCountdownDuration { get; set; }
    int VideoRecordingTimeLimitMinutes { get; set; }
    bool GifCountdownEnabled { get; set; }
    int GifCountdownDuration { get; set; }
    bool ScreenshotCountdownEnabled { get; set; }
    int ScreenshotCountdownDuration { get; set; }
    bool HasCompletedOnboarding { get; set; }
    MultiMonitorCaptureMode MultiMonitorCaptureMode { get; set; }
    bool ShowRegionIndicator { get; set; }
    bool IncludeTinyClipsInCapture { get; set; }
    bool ShowBrandingOverlay { get; set; }
    bool TeleprompterEnabled { get; set; }
    string TeleprompterTranscript { get; set; }
    double TeleprompterScrollSpeed { get; set; }
    double TeleprompterPosX { get; set; }
    double TeleprompterPosY { get; set; }
    string TeleprompterMonitorDeviceName { get; set; }
    bool UploadcareEnabled { get; set; }
    string UploadcarePublicKey { get; set; }
    bool UploadcareAutoUpload { get; set; }
    bool UploadcareCopyUrl { get; set; }
    int ScreenshotHotKeyCode { get; set; }
    int ScreenshotHotKeyModifiers { get; set; }
    int VideoHotKeyCode { get; set; }
    int VideoHotKeyModifiers { get; set; }
    int GifHotKeyCode { get; set; }
    int GifHotKeyModifiers { get; set; }
    int OcrHotKeyCode { get; set; }
    int OcrHotKeyModifiers { get; set; }
    int ScreenshotRegionHotKeyCode { get; set; }
    int ScreenshotRegionHotKeyModifiers { get; set; }
    int ScreenshotWindowHotKeyCode { get; set; }
    int ScreenshotWindowHotKeyModifiers { get; set; }

    ImageFormat ImageFormat { get; set; }
    bool ShouldCopyToClipboard(CaptureType type);
    bool ShouldShowCapturePicker(CaptureType type);
    bool ShouldShowCapturePickerAfterCapture(CaptureType type);
    MouseClickOverlayStyle MouseClickOverlayStyleFor(CaptureType type);
    bool ShouldShowMouseClickVisuals(CaptureType type);
    void SetShowMouseClickVisuals(bool enabled, CaptureType type);
    void ResetToDefaults();
}
