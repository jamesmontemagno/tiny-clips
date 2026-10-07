using TinyClips.App.Models.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;

namespace TinyClips.App.ViewModels.Studio;

// The values behind the inspector. A setter hands the new value to the session, which clamps it
// and records the undo step; the getter then reports what the project holds. Which panel of
// the inspector is on show is the session's to say as well.
public sealed partial class StudioViewModel
{
    private const string DefaultLookSavedText = "Saved. New Studio recordings will start with this look.";

    private StudioEditableState? _defaultLookState;

    // The rail and its panels

    /// <summary>
    /// The inspector panel on show. The rail chooses it, and so does taking hold of something
    /// a panel edits: a zoom, a cut, a speed change, a scene, or the camera in the preview.
    /// </summary>
    public StudioInspectorPanel InspectorPanel => _session.InspectorPanel;

    /// <summary>
    /// Shows an inspector panel, or the nearest one this recording has. It is what the rail
    /// does. Not an edit: Undo leaves it.
    /// </summary>
    public void ShowInspectorPanel(StudioInspectorPanel panel) => _session.ShowInspectorPanel(panel);

    /// <summary>The Show scene button of the Camera panel, for a layout that hides the camera.</summary>
    public void ShowScenePanel() => ShowInspectorPanel(StudioInspectorPanel.Scene);

    // Layout

    public bool HasCamera => _session.HasCamera;

    /// <summary>True when the project is open and was recorded without a camera.</summary>
    public bool HasNoCamera => IsReady && !HasCamera;

    private StudioLayout Layout => _session.Model?.EffectiveLayout ?? StudioLayout.Screen;

    public int LayoutIndex
    {
        get => (int)Layout;
        set
        {
            if (value is >= 0 and <= (int)StudioLayout.Camera && value != LayoutIndex)
            {
                _session.SetLayout((StudioLayout)value);
            }

            ResyncIfDifferent(value, LayoutIndex);
        }
    }

    // Background

    private StudioBackground Background => Project?.Canvas.Background ?? new StudioBackground();

    /// <summary>Off is a black canvas. Turning it on brings back the default gradient.</summary>
    public bool IsBackgroundShown
    {
        get => Background.Style != StudioBackgroundStyle.None;
        set
        {
            if (value != IsBackgroundShown)
            {
                if (!value)
                {
                    _session.RemoveBackground();
                }
                else if (StudioSwatch.Find(StudioSwatch.DefaultPresetId) is { } swatch)
                {
                    SelectSwatch(swatch);
                }
            }

            ResyncIfDifferent(value, IsBackgroundShown);
        }
    }

    /// <summary>The id of the preset the canvas is filled with, or null when there is none.</summary>
    public string? BackgroundPresetId => IsBackgroundShown ? Background.Preset : null;

    public void SelectSwatch(StudioSwatch swatch)
    {
        ArgumentNullException.ThrowIfNull(swatch);
        _session.SetBackgroundPreset(swatch.Style, swatch.Id, swatch.PrimaryHex, swatch.SecondaryHex);
    }

    public double CanvasPadding
    {
        get => Project?.Canvas.Padding ?? 0;
        set
        {
            if (IsRequest(value, CanvasPadding))
            {
                _session.SetCanvasPadding(value);
            }
        }
    }

    public string CanvasPaddingText => StudioEditorText.GetPercentText(CanvasPadding);

    // Screen

    public double ScreenCornerRadius
    {
        get => Project?.Screen.CornerRadius ?? 0;
        set
        {
            if (IsRequest(value, ScreenCornerRadius))
            {
                _session.SetScreenCornerRadius(value);
            }
        }
    }

    /// <summary>The radius is stored as a fraction of the short side, where 0.2 is fully round.</summary>
    public string ScreenCornerRadiusText => StudioEditorText.GetPercentText(ScreenCornerRadius * 5);

    public double ScreenShadow
    {
        get => Project?.Screen.Shadow ?? 0;
        set
        {
            if (IsRequest(value, ScreenShadow))
            {
                _session.SetScreenShadow(value);
            }
        }
    }

    public string ScreenShadowText => StudioEditorText.GetPercentText(ScreenShadow);

    // Camera

    private StudioCameraStyle Camera => Project?.Camera ?? new StudioCameraStyle();

    public bool IsCameraHiddenNoteVisible => HasCamera && Layout == StudioLayout.Screen;

    public bool AreBubbleControlsVisible => HasCamera && Layout == StudioLayout.Bubble;

    public bool IsCameraCornerRadiusVisible =>
        AreBubbleControlsVisible && Camera.Shape == StudioCameraShape.RoundedRectangle;

    public bool AreSideBySideControlsVisible => HasCamera && Layout == StudioLayout.SideBySide;

    public bool AreCameraStyleControlsVisible => HasCamera && Layout != StudioLayout.Screen;

    /// <summary>
    /// Where the camera is belongs to the scene: its size and place as a bubble, or its side
    /// and size next to the screen. A camera that fills the video has nothing to place.
    /// </summary>
    public bool IsCameraPlacementVisible => AreBubbleControlsVisible || AreSideBySideControlsVisible;

    /// <summary>
    /// Placement is the scene's and the rest is the whole video's. Said once there is more
    /// than one scene for it to matter in.
    /// </summary>
    public bool IsCameraPlacementNoteVisible => IsCameraPlacementVisible && Scenes.Count > 1;

    public int CameraShapeIndex
    {
        get => (int)Camera.Shape;
        set
        {
            if (value is >= 0 and <= (int)StudioCameraShape.Rectangle && value != CameraShapeIndex)
            {
                _session.SetCameraShape((StudioCameraShape)value);
            }

            ResyncIfDifferent(value, CameraShapeIndex);
        }
    }

    public double CameraCornerRadius
    {
        get => Camera.CornerRadius;
        set
        {
            if (IsRequest(value, CameraCornerRadius))
            {
                _session.SetCameraCornerRadius(value);
            }
        }
    }

    /// <summary>The radius is stored as a fraction of the short side, where 0.5 is fully round.</summary>
    public string CameraCornerRadiusText => StudioEditorText.GetPercentText(CameraCornerRadius * 2);

    public double CameraBubbleSize
    {
        get => Scene.Bubble.Size;
        set
        {
            if (IsRequest(value, CameraBubbleSize))
            {
                _session.SetCameraBubbleSize(value);
            }
        }
    }

    public string CameraBubbleSizeText => StudioEditorText.GetPercentText(CameraBubbleSize);

    /// <summary>The corner the bubble is anchored to. Choosing one clears the offsets.</summary>
    public int CameraAnchorIndex
    {
        get => (int)Scene.Bubble.Anchor;
        set
        {
            // A two-way binding also hands back a corner the editor chose itself, as it does while
            // the bubble is dragged past the middle of the canvas. Only a different corner is a
            // choice; clearing the offsets for the same one would snap the bubble back mid-drag.
            if (value is >= 0 and <= (int)StudioAnchor.BottomRight && value != CameraAnchorIndex)
            {
                _session.SetCameraAnchor((StudioAnchor)value);
            }

            ResyncIfDifferent(value, CameraAnchorIndex);
        }
    }

    public double CameraOffsetX
    {
        get => Scene.Bubble.OffsetX;
        set
        {
            if (IsRequest(value, CameraOffsetX))
            {
                _session.SetCameraBubbleOffsetX(value);
            }
        }
    }

    public string CameraOffsetXText => StudioEditorText.GetSignedPercentText(CameraOffsetX);

    public double CameraOffsetY
    {
        get => Scene.Bubble.OffsetY;
        set
        {
            if (IsRequest(value, CameraOffsetY))
            {
                _session.SetCameraBubbleOffsetY(value);
            }
        }
    }

    public string CameraOffsetYText => StudioEditorText.GetSignedPercentText(CameraOffsetY);

    public int CameraSideIndex
    {
        get => (int)Scene.Split.CameraSide;
        set
        {
            if (value is >= 0 and <= (int)StudioCameraSide.Trailing && value != CameraSideIndex)
            {
                _session.SetSideBySide((StudioCameraSide)value, Scene.Split.CameraFraction);
            }

            ResyncIfDifferent(value, CameraSideIndex);
        }
    }

    public double CameraShare
    {
        get => Scene.Split.CameraFraction;
        set
        {
            if (IsRequest(value, CameraShare))
            {
                _session.SetCameraShare(value);
            }
        }
    }

    public string CameraShareText => StudioEditorText.GetPercentText(CameraShare);

    public bool IsCameraMirrored
    {
        get => Camera.Mirror;
        set
        {
            if (value != IsCameraMirrored)
            {
                _session.SetCameraMirror(value);
            }

            ResyncIfDifferent(value, IsCameraMirrored);
        }
    }

    /// <summary>
    /// Whether people can be found in a camera picture at all, as the app said when this editor
    /// opened. Without that a background can be neither blurred nor removed, every background
    /// is drawn as it was recorded, and the choice is not offered.
    /// </summary>
    public bool CanFindPeople { get; }

    /// <summary>
    /// What is behind the people in the camera picture: kept, blurred or removed. The number is
    /// the choice's place in <see cref="StudioCameraCutout"/>.
    /// </summary>
    public int CameraCutoutIndex
    {
        get => (int)Camera.Cutout;
        set
        {
            // A two-way binding also hands back the choice the editor reported itself. Only
            // another one is an edit.
            if (value is >= 0 and <= (int)StudioCameraCutout.Remove && value != CameraCutoutIndex)
            {
                _session.SetCameraCutout((StudioCameraCutout)value);
            }

            ResyncIfDifferent(value, CameraCutoutIndex);
        }
    }

    /// <summary>
    /// True while the background is removed, which takes the camera's border and its shadow with
    /// it: the panel then says so, next to the two sliders that have nothing to show.
    /// </summary>
    public bool IsCameraCutoutRemovedNoteVisible => CanFindPeople && Camera.Cutout == StudioCameraCutout.Remove;

    public double CameraBorderWidth
    {
        get => Camera.BorderWidth;
        set
        {
            if (IsRequest(value, CameraBorderWidth))
            {
                _session.SetCameraBorderWidth(value);
            }
        }
    }

    /// <summary>The border is stored as a fraction of the short side, where 0.02 is the widest.</summary>
    public string CameraBorderWidthText => StudioEditorText.GetPercentText(CameraBorderWidth * 50);

    public double CameraShadow
    {
        get => Camera.Shadow;
        set
        {
            if (IsRequest(value, CameraShadow))
            {
                _session.SetCameraShadow(value);
            }
        }
    }

    public string CameraShadowText => StudioEditorText.GetPercentText(CameraShadow);

    // Click highlights in the Screen panel, the badge in Project, and Mute in Audio

    public bool AreClickRingsEnabled
    {
        get => Project?.Overlays.Clicks.Enabled ?? false;
        set
        {
            if (value != AreClickRingsEnabled)
            {
                _session.SetClickRingsEnabled(value);
            }

            ResyncIfDifferent(value, AreClickRingsEnabled);
        }
    }

    public bool IsBrandingEnabled
    {
        get => Project?.Overlays.Branding ?? false;
        set
        {
            if (value != IsBrandingEnabled)
            {
                _session.SetBrandingEnabled(value);
            }

            ResyncIfDifferent(value, IsBrandingEnabled);
        }
    }

    public bool IsMuted
    {
        get => Project?.Audio.Muted ?? false;
        set
        {
            if (value != IsMuted)
            {
                _session.SetMuted(value);
            }

            ResyncIfDifferent(value, IsMuted);
        }
    }

    // Project

    /// <summary>
    /// Whether the project is pinned against automatic cleanup. It is written at once and is not
    /// an edit: Undo leaves it alone.
    /// </summary>
    public bool KeepsProject
    {
        get => _session.KeepSources;
        set
        {
            _session.SetKeepSources(value);
            ResyncIfDifferent(value, KeepsProject);
        }
    }

    // Default look

    /// <summary>Says that the look was saved as the default, until the project is edited again.</summary>
    public string DefaultLookStatus
    {
        get => _defaultLookStatus;
        private set
        {
            if (SetProperty(ref _defaultLookStatus, value))
            {
                OnPropertyChanged(nameof(HasDefaultLookStatus));
            }
        }
    }

    public bool HasDefaultLookStatus => _defaultLookStatus.Length > 0;

    /// <summary>Makes the canvas, screen and camera styling the look new recordings start with.</summary>
    public void SaveDefaultLook()
    {
        if (_session.Model is not { } model)
        {
            return;
        }

        _session.SaveDefaultLook();
        _defaultLookState = model.EditableState;
        DefaultLookStatus = DefaultLookSavedText;
        Announce(DefaultLookSavedText, "StudioDefaultLookSaved", StudioAnnouncementKind.Completed);
    }

    private void SetLayoutFromKey(StudioLayout layout)
    {
        var before = Layout;
        _session.SetLayout(layout);
        if (Layout != before)
        {
            Announce($"Layout: {StudioEditorModel.GetLayoutName(Layout)}.", "StudioLayoutChanged", StudioAnnouncementKind.Completed);
        }
    }

    /// <summary>Takes the saved-as-default note away once the project is no longer what was saved.</summary>
    private void ClearDefaultLookStatusIfEdited()
    {
        if (_defaultLookState is not { } saved)
        {
            return;
        }

        if (_session.Model is { } model && saved.ContentEquals(model.EditableState))
        {
            return;
        }

        _defaultLookState = null;
        _defaultLookStatus = string.Empty;
    }
}
