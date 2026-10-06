namespace TinyClips.Core.Studio.Editing;

// The inspector. One of its panels is on show, and the rail down its edge chooses which. Taking
// hold of something a panel edits shows that panel too: selecting, adding or stepping to a zoom,
// a cut or a speed change, suggesting zooms, going to a scene or splitting one, and moving the
// camera in the preview. Undo and redo, the layout, playing, scrubbing, letting go of what is
// selected and deleting it leave the panel as it is. What the inspector shows is not part of
// the project and not an undo step.
public sealed partial class StudioEditorSession
{
    private StudioInspectorPanel _inspectorPanel = StudioInspectorPanel.Background;
    private bool? _isScreenCropOpen;
    private bool? _isCameraCropOpen;

    /// <summary>
    /// The inspector panel on show. A project opens on Scene, or on Background when it was
    /// recorded without a camera.
    /// </summary>
    public StudioInspectorPanel InspectorPanel => _inspectorPanel;

    /// <summary>
    /// Whether the Crop group of the Screen panel is open. Until its header is pressed it is
    /// open while the screen is cropped, so a panel is not half crop sliders that are rarely
    /// moved. From then on it is as the header left it.
    /// </summary>
    public bool IsScreenCropOpen => _isScreenCropOpen ?? Model is { ScreenCropInsets.IsEmpty: false };

    /// <summary>Whether the Crop group of the Camera panel is open, as <see cref="IsScreenCropOpen"/>.</summary>
    public bool IsCameraCropOpen => _isCameraCropOpen ?? Model is { CameraCropInsets.IsEmpty: false };

    /// <summary>
    /// Shows an inspector panel, or the nearest one this recording has: without a camera, Scene
    /// is Background and Camera is Screen. The rail does this, and so does taking hold of
    /// something a panel edits. Not an edit: Undo leaves it.
    /// </summary>
    public void ShowInspectorPanel(StudioInspectorPanel panel)
    {
        var resolved = StudioInspectorPanels.Resolve(panel, HasCamera);
        if (resolved == _inspectorPanel)
        {
            return;
        }

        _inspectorPanel = resolved;
        RaiseChanged(StudioEditorChanges.Inspector);
    }

    /// <summary>
    /// Opens or closes the Crop group of the Screen panel, as a press on its header does. The
    /// state the group is in already says nothing new, and leaves it following the crop.
    /// </summary>
    public void SetScreenCropOpen(bool isOpen)
    {
        if (isOpen != IsScreenCropOpen)
        {
            _isScreenCropOpen = isOpen;
            RaiseChanged(StudioEditorChanges.Inspector);
        }
    }

    /// <summary>Opens or closes the Crop group of the Camera panel, as <see cref="SetScreenCropOpen"/>.</summary>
    public void SetCameraCropOpen(bool isOpen)
    {
        if (isOpen != IsCameraCropOpen)
        {
            _isCameraCropOpen = isOpen;
            RaiseChanged(StudioEditorChanges.Inspector);
        }
    }

    // An edit that says which zoom, cut or speed change it left selected shows the panel of
    // that one. Adding one does, and so does an edit to the selected one. An edit that leaves
    // none selected, as deleting the selected one does, shows nothing.
    private void ShowPanelOf(EditSelection? selection)
    {
        switch (selection?.Kind)
        {
            case EditSelectionKind.Zoom when SelectedZoomIndex is not null:
                ShowInspectorPanel(StudioInspectorPanel.Zoom);
                break;
            case EditSelectionKind.Cut when SelectedCutIndex is not null:
                ShowInspectorPanel(StudioInspectorPanel.Cut);
                break;
            case EditSelectionKind.Speed when SelectedSpeedIndex is not null:
                ShowInspectorPanel(StudioInspectorPanel.Speed);
                break;
        }
    }
}
