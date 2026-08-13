using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace TinyClips.App.ScreenshotEditor;

/// <summary>The vertical tool rail. Owns nothing but its own toggle buttons — every click just
/// tells the shared <see cref="EditorController"/> which tool is active.</summary>
public sealed partial class EditorToolbar : UserControl
{
    private EditorController _controller = null!;

    public EditorToolbar()
    {
        InitializeComponent();
    }

    internal void Attach(EditorController controller)
    {
        _controller = controller;
        _controller.ToolChanged += (_, tool) => SyncCheckedState(tool);
        SyncCheckedState(controller.Tool);
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<EditTool>(tag, out var tool))
        {
            _controller.SetTool(tool);
        }
    }

    private void SyncCheckedState(EditTool tool)
    {
        foreach (var (button, value) in ToolButtons())
        {
            button.IsChecked = value == tool;
        }
    }

    private IEnumerable<(ToggleButton Button, EditTool Tool)> ToolButtons()
    {
        yield return (ToolSelect, EditTool.Select);
        yield return (ToolCrop, EditTool.Crop);
        yield return (ToolRectangle, EditTool.Rectangle);
        yield return (ToolEllipse, EditTool.Ellipse);
        yield return (ToolArrow, EditTool.Arrow);
        yield return (ToolLine, EditTool.Line);
        yield return (ToolPen, EditTool.Pen);
        yield return (ToolText, EditTool.Text);
        yield return (ToolCounter, EditTool.Counter);
        yield return (ToolRedact, EditTool.Redact);
    }
}
