using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App.RecordingSetup;

/// <summary>
/// Webcam on/off toggle and webcam settings flyout (camera device, shape, corner, size, rounded
/// corner value) for <see cref="TinyClips.App.RecordingSetupWindow"/>. Camera/webcam permission
/// decisions (which require the host window's close guard, <c>IMediaDevicePermissionService</c>,
/// and the auto-enable-microphone coordination with <see cref="AudioDeviceControl"/>) stay with
/// the window; this control owns only the visuals and the flyout's cached menu items.
/// </summary>
/// <remarks>
/// The static option groups (shape, corner, size, rounded-corner value) are built exactly once, in
/// the constructor, and never rebuilt — only their cached <see cref="ToggleMenuFlyoutItem"/>s'
/// <c>IsChecked</c> flips when the selection changes. The camera device group is the only part
/// that ever gets rebuilt, and only when the enumerated webcam collection actually changes or
/// loading starts/ends (see <see cref="SetWebcams"/> and <see cref="SetWebcamsLoading"/>); an
/// ordinary camera selection only updates <c>IsChecked</c> on the cached items.
/// </remarks>
public sealed partial class WebcamOptionsControl : UserControl
{
    private static readonly double[] CornerRadiusOptions = { -1d, 8d, 12d, 16d, 24d, 32d, 48d };
    private const double SetupPreviewHeight = 54;
    private const double SetupPreviewWideWidth = 96;

    private readonly List<WebcamDeviceInfo> _webcams = new();
    private readonly List<ToggleMenuFlyoutItem> _cameraItems = new();
    private readonly List<ToggleMenuFlyoutItem> _shapeItems = new();
    private readonly List<ToggleMenuFlyoutItem> _cornerItems = new();
    private readonly List<ToggleMenuFlyoutItem> _sizeItems = new();
    private readonly List<ToggleMenuFlyoutItem> _radiusItems = new();

    private MenuFlyoutSubItem _cameraMenu = null!;
    private MenuFlyoutSubItem _radiusMenu = null!;
    private MenuFlyoutItem? _loadingCameraItem;

    private CaptureType _captureType = CaptureType.Video;
    private bool _suppressEvents;
    private bool _webcamsLoading;
    private bool _webcamsEnumerated;
    private bool _webcamEnabled;
    private string _selectedWebcamId = string.Empty;
    private WebcamShape _webcamShape;
    private WebcamSizePreset _webcamSizePreset;
    private WebcamCornerPosition _webcamCornerPosition;
    private double _webcamCornerRadius = -1;

    public WebcamOptionsControl()
    {
        InitializeComponent();

        _webcams.Add(new WebcamDeviceInfo(string.Empty, "System default"));
        BuildFlyout();
    }

    /// <summary>Raised when the user checks the webcam toggle while it was previously off, meaning
    /// the host must request camera (and, for video, auto-enable microphone) access before the
    /// state can change. The host calls <see cref="SetWebcamAllowed"/> with the resolved value once
    /// the request completes.</summary>
    public event EventHandler? WebcamToggleRequested;

    /// <summary>Raised whenever something that can change <see cref="IsWebcamSelectionReady"/>
    /// happens: the webcam toggle, loading state, a permission outcome, or device-list
    /// reconciliation. The host uses this to keep its Start button's enabled state current.</summary>
    public event EventHandler? ReadinessChanged;

    public event EventHandler? PreviewSourceChanged;

    public bool WebcamEnabled => _webcamEnabled;

    public string SelectedWebcamId => _selectedWebcamId;

    public WebcamShape WebcamShape => _webcamShape;

    public WebcamSizePreset WebcamSizePreset => _webcamSizePreset;

    public WebcamCornerPosition WebcamCornerPosition => _webcamCornerPosition;

    public double? WebcamCornerRadiusOrNull => _webcamCornerRadius < 0 ? null : _webcamCornerRadius;

    /// <summary>
    /// <see langword="true"/> when the webcam is not enabled (nothing to resolve), or when it is
    /// enabled and the device list has finished at least one enumeration pass with no load in
    /// progress — i.e. <see cref="SelectedWebcamId"/> reflects a reconciled choice rather than a
    /// persisted id that hasn't been checked against the real device list yet.
    /// </summary>
    public bool IsWebcamSelectionReady => !_webcamEnabled || (_webcamsEnumerated && !_webcamsLoading);

    public bool IsWebcamToggleEnabled
    {
        get => WebcamToggle.IsEnabled;
        set => WebcamToggle.IsEnabled = value;
    }

    public void SetVisibleForVideo(bool isVideo)
    {
        var visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        WebcamToggle.Visibility = visibility;
        WebcamSettingsButton.Visibility = visibility;
    }

    /// <summary>Seeds the initial state from persisted settings. Must be called once, before the
    /// window is shown.</summary>
    public void Initialize(
        CaptureType captureType,
        bool webcamEnabled,
        string? selectedWebcamId,
        WebcamShape shape,
        WebcamSizePreset sizePreset,
        WebcamCornerPosition cornerPosition,
        double? cornerRadius)
    {
        _captureType = captureType;
        _webcamEnabled = webcamEnabled;
        _selectedWebcamId = selectedWebcamId ?? string.Empty;
        _webcamShape = shape;
        _webcamSizePreset = sizePreset;
        _webcamCornerPosition = cornerPosition;
        _webcamCornerRadius = cornerRadius ?? -1;

        _suppressEvents = true;
        try
        {
            WebcamToggle.IsChecked = _webcamEnabled;
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateWebcamVisual();
        UpdateWebcamSettingsEnabled();
        UpdateSetupPreviewShape();
        UpdateCameraSelectionVisuals();
        UpdateShapeSelectionVisuals();
        UpdateCornerSelectionVisuals();
        UpdateSizeSelectionVisuals();
        UpdateRadiusSelectionVisuals();
        UpdateRadiusMenuEnabled();
        UpdateWebcamSettingsSummary();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the outcome of a camera permission request without re-raising
    /// <see cref="WebcamToggleRequested"/>.</summary>
    public void SetWebcamAllowed(bool allowed)
    {
        _webcamEnabled = allowed;
        _suppressEvents = true;
        try
        {
            WebcamToggle.IsChecked = allowed;
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateWebcamVisual();
        UpdateWebcamSettingsEnabled();
        UpdateWebcamSettingsSummary();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
        PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetWebcamsLoading(bool loading)
    {
        _webcamsLoading = loading;
        RenderCameraMenu();
        UpdateWebcamSettingsSummary();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
        if (_webcamEnabled)
        {
            PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Applies the enumerated webcam list. Device menu items are only rebuilt when the
    /// collection actually changed; otherwise this just re-resolves the selected id. A persisted
    /// <see cref="SelectedWebcamId"/> that matches an enumerated device is kept as-is; it only
    /// falls back to system default when the id is absent from the list.</summary>
    public void SetWebcams(IReadOnlyList<WebcamDeviceInfo> webcams)
    {
        var incoming = new List<WebcamDeviceInfo> { new(string.Empty, "System default") };
        incoming.AddRange(webcams);

        if (!DevicesEqual(_webcams, incoming))
        {
            _webcams.Clear();
            _webcams.AddRange(incoming);
            RebuildCameraItems();
        }

        var selected = _webcams.FirstOrDefault(w => w.Id == _selectedWebcamId) ?? _webcams[0];
        _selectedWebcamId = selected.Id;
        _webcamsEnumerated = true;

        RenderCameraMenu();
        UpdateWebcamSettingsSummary();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWebcamToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var isChecked = WebcamToggle.IsChecked == true;
        if (isChecked && !_webcamEnabled)
        {
            WebcamToggleRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _webcamEnabled = isChecked;
        UpdateWebcamVisual();
        UpdateWebcamSettingsEnabled();
        ReadinessChanged?.Invoke(this, EventArgs.Empty);
        PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildFlyout()
    {
        _cameraMenu = new MenuFlyoutSubItem { Text = "Camera" };
        RebuildCameraItems();
        RenderCameraMenu();
        WebcamSettingsFlyout.Items.Add(_cameraMenu);
        WebcamSettingsFlyout.Items.Add(new MenuFlyoutSeparator());

        var shapeMenu = new MenuFlyoutSubItem { Text = "Shape" };
        AddStaticItem(shapeMenu, _shapeItems, "Rectangle", WebcamShape.Rectangle, OnShapeItemClick, _webcamShape == WebcamShape.Rectangle);
        AddStaticItem(shapeMenu, _shapeItems, "Rounded rectangle", WebcamShape.RoundedRectangle, OnShapeItemClick, _webcamShape == WebcamShape.RoundedRectangle);
        AddStaticItem(shapeMenu, _shapeItems, "Circle", WebcamShape.Circle, OnShapeItemClick, _webcamShape == WebcamShape.Circle);
        WebcamSettingsFlyout.Items.Add(shapeMenu);

        var cornerMenu = new MenuFlyoutSubItem { Text = "Corner" };
        AddStaticItem(cornerMenu, _cornerItems, "Top left", WebcamCornerPosition.TopLeft, OnCornerItemClick, _webcamCornerPosition == WebcamCornerPosition.TopLeft);
        AddStaticItem(cornerMenu, _cornerItems, "Top right", WebcamCornerPosition.TopRight, OnCornerItemClick, _webcamCornerPosition == WebcamCornerPosition.TopRight);
        AddStaticItem(cornerMenu, _cornerItems, "Bottom left", WebcamCornerPosition.BottomLeft, OnCornerItemClick, _webcamCornerPosition == WebcamCornerPosition.BottomLeft);
        AddStaticItem(cornerMenu, _cornerItems, "Bottom right", WebcamCornerPosition.BottomRight, OnCornerItemClick, _webcamCornerPosition == WebcamCornerPosition.BottomRight);
        WebcamSettingsFlyout.Items.Add(cornerMenu);

        var sizeMenu = new MenuFlyoutSubItem { Text = "Size" };
        AddStaticItem(sizeMenu, _sizeItems, "Small", WebcamSizePreset.Small, OnSizeItemClick, _webcamSizePreset == WebcamSizePreset.Small);
        AddStaticItem(sizeMenu, _sizeItems, "Medium", WebcamSizePreset.Medium, OnSizeItemClick, _webcamSizePreset == WebcamSizePreset.Medium);
        AddStaticItem(sizeMenu, _sizeItems, "Large", WebcamSizePreset.Large, OnSizeItemClick, _webcamSizePreset == WebcamSizePreset.Large);
        WebcamSettingsFlyout.Items.Add(sizeMenu);

        _radiusMenu = new MenuFlyoutSubItem
        {
            Text = "Rounded corner value",
            IsEnabled = _webcamShape == WebcamShape.RoundedRectangle,
        };
        foreach (var radius in CornerRadiusOptions)
        {
            AddStaticItem(_radiusMenu, _radiusItems, FormatCornerRadius(radius), radius, OnRadiusItemClick, Math.Abs(_webcamCornerRadius - radius) < 0.1);
        }

        WebcamSettingsFlyout.Items.Add(_radiusMenu);
    }

    private static void AddStaticItem<T>(
        MenuFlyoutSubItem menu,
        List<ToggleMenuFlyoutItem> cache,
        string text,
        T tag,
        RoutedEventHandler click,
        bool isChecked)
        where T : notnull
    {
        var item = new ToggleMenuFlyoutItem { Text = text, Tag = tag, IsChecked = isChecked };
        item.Click += click;
        cache.Add(item);
        menu.Items.Add(item);
    }

    private void OnShapeItemClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is ToggleMenuFlyoutItem { Tag: WebcamShape shape })
        {
            _webcamShape = shape;
            UpdateSetupPreviewShape();
            UpdateShapeSelectionVisuals();
            UpdateRadiusMenuEnabled();
            UpdateWebcamSettingsSummary();
        }
    }

    private void OnCornerItemClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is ToggleMenuFlyoutItem { Tag: WebcamCornerPosition corner })
        {
            _webcamCornerPosition = corner;
            UpdateCornerSelectionVisuals();
            UpdateWebcamSettingsSummary();
        }
    }

    private void OnSizeItemClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is ToggleMenuFlyoutItem { Tag: WebcamSizePreset size })
        {
            _webcamSizePreset = size;
            UpdateSizeSelectionVisuals();
            UpdateWebcamSettingsSummary();
            PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRadiusItemClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is ToggleMenuFlyoutItem { Tag: double radius })
        {
            _webcamCornerRadius = radius;
            UpdateSetupPreviewShape();
            UpdateRadiusSelectionVisuals();
            UpdateWebcamSettingsSummary();
        }
    }

    private void OnCameraItemClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (sender is ToggleMenuFlyoutItem { Tag: WebcamDeviceInfo webcam })
        {
            _selectedWebcamId = webcam.Id;
            UpdateCameraSelectionVisuals();
            UpdateWebcamSettingsSummary();
            PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ShowPreview(IWebcamCaptureService capture)
    {
        SetupPreview.Visibility = Visibility.Visible;
        UpdateSetupPreviewShape();
        SetupPreview.Attach(capture);
    }

    /// <summary>Keeps the setup preview's footprint in step with the chosen shape: a circle needs a
    /// square surface (otherwise the rounded corners turn it into an oval), while rectangles keep
    /// the 16:9 thumbnail footprint.</summary>
    private void UpdateSetupPreviewShape()
    {
        SetupPreview.Width = _webcamShape == WebcamShape.Circle ? SetupPreviewHeight : SetupPreviewWideWidth;
        SetupPreview.Height = SetupPreviewHeight;
        SetupPreview.ConfigureShape(_webcamShape, WebcamCornerRadiusOrNull);
    }

    public void HidePreview()
    {
        SetupPreview.Detach();
        SetupPreview.Visibility = Visibility.Collapsed;
    }

    private void RebuildCameraItems()
    {
        foreach (var item in _cameraItems)
        {
            item.Click -= OnCameraItemClick;
        }

        _cameraItems.Clear();

        foreach (var webcam in _webcams)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = webcam.Name,
                Tag = webcam,
                IsChecked = webcam.Id == _selectedWebcamId,
            };
            item.Click += OnCameraItemClick;
            _cameraItems.Add(item);
        }
    }

    private void RenderCameraMenu()
    {
        _cameraMenu.Items.Clear();
        if (_webcamsLoading)
        {
            _loadingCameraItem ??= new MenuFlyoutItem { Text = "Loading webcams...", IsEnabled = false };
            _cameraMenu.Items.Add(_loadingCameraItem);
            return;
        }

        foreach (var item in _cameraItems)
        {
            _cameraMenu.Items.Add(item);
        }

        UpdateCameraSelectionVisuals();
    }

    private void UpdateCameraSelectionVisuals()
    {
        foreach (var item in _cameraItems)
        {
            item.IsChecked = item.Tag is WebcamDeviceInfo device && device.Id == _selectedWebcamId;
        }
    }

    private void UpdateShapeSelectionVisuals()
    {
        foreach (var item in _shapeItems)
        {
            item.IsChecked = item.Tag is WebcamShape shape && shape == _webcamShape;
        }
    }

    private void UpdateCornerSelectionVisuals()
    {
        foreach (var item in _cornerItems)
        {
            item.IsChecked = item.Tag is WebcamCornerPosition corner && corner == _webcamCornerPosition;
        }
    }

    private void UpdateSizeSelectionVisuals()
    {
        foreach (var item in _sizeItems)
        {
            item.IsChecked = item.Tag is WebcamSizePreset size && size == _webcamSizePreset;
        }
    }

    private void UpdateRadiusSelectionVisuals()
    {
        foreach (var item in _radiusItems)
        {
            item.IsChecked = item.Tag is double radius && Math.Abs(radius - _webcamCornerRadius) < 0.1;
        }
    }

    private void UpdateRadiusMenuEnabled()
    {
        _radiusMenu.IsEnabled = _webcamShape == WebcamShape.RoundedRectangle;
    }

    private void UpdateWebcamVisual()
    {
        WebcamSlash.Visibility = _webcamEnabled ? Visibility.Collapsed : Visibility.Visible;
        var state = _webcamEnabled ? "On" : "Off";
        ToolTipService.SetToolTip(WebcamToggle, $"Webcam: {state}");
        AutomationProperties.SetName(WebcamToggle, $"Webcam {state}");
    }

    private void UpdateWebcamSettingsEnabled()
    {
        WebcamSettingsButton.IsEnabled = _captureType == CaptureType.Video;
    }

    private void UpdateWebcamSettingsSummary()
    {
        var selected = _webcams.FirstOrDefault(w => w.Id == _selectedWebcamId);
        var deviceName = selected?.Name ?? (_webcamsLoading ? "Loading webcams..." : "System default");
        var state = _webcamEnabled ? "On" : "Off";
        var summary = $"Webcam settings: {state}, {deviceName}, {_webcamShape}, {_webcamSizePreset}, {FormatCorner(_webcamCornerPosition)}";
        ToolTipService.SetToolTip(WebcamSettingsButton, summary);
        AutomationProperties.SetName(WebcamSettingsButton, summary);
    }

    private static string FormatCorner(WebcamCornerPosition corner) => corner switch
    {
        WebcamCornerPosition.TopLeft => "top left",
        WebcamCornerPosition.TopRight => "top right",
        WebcamCornerPosition.BottomLeft => "bottom left",
        WebcamCornerPosition.BottomRight => "bottom right",
        _ => "bottom right",
    };

    private static string FormatCornerRadius(double radius) => radius < 0 ? "Default" : $"{radius:0} px";

    private static bool DevicesEqual(IReadOnlyList<WebcamDeviceInfo> a, IReadOnlyList<WebcamDeviceInfo> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name)
            {
                return false;
            }
        }

        return true;
    }
}
