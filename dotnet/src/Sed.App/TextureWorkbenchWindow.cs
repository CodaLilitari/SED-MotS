using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.App;

/// <summary>A shared-pivot UV editor for the surface selection captured when opened.</summary>
public sealed class TextureWorkbenchWindow : Window
{
    private readonly Surface[] _surfaces;
    private readonly EditHistory _history;
    private readonly ComboBox _mode;
    private readonly ComboBox _direction;
    private readonly Dictionary<string, TextBox> _fields = new(StringComparer.Ordinal);
    private readonly TextBlock _error;
    private readonly TextBlock _status;
    private readonly Button _apply;

    public TextureWorkbenchWindow(IReadOnlyList<Surface> surfaces, EditHistory history)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(history);
        _surfaces = surfaces.ToArray();
        _history = history;
        Title = "Texture Workbench";
        Width = 610; Height = 660; MinWidth = 540; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(18), LastChildFill = true };
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = $"Edit UVs on {_surfaces.Length} selected surface{(_surfaces.Length == 1 ? "" : "s")}", FontSize = 19, FontWeight = FontWeight.SemiBold });
        header.Children.Add(Hint("This selection stays fixed while the window is open. Each Apply is one undoable edit. UV values are in texels; use a decimal point for fractional values."));
        _mode = new ComboBox
        {
            Name = "TextureWorkbenchMode", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Project from world coordinates", "Transform existing UVs" }, SelectedIndex = 0,
        };
        header.Children.Add(_mode);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        _error = new TextBlock { Name = "TextureWorkbenchError", IsVisible = false, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
        _status = new TextBlock { Name = "TextureWorkbenchStatus", TextWrapping = TextWrapping.Wrap };
        _apply = new Button { Name = "TextureWorkbenchApply", Content = "Project selected surfaces", IsEnabled = _surfaces.Length > 0 };
        _apply.Click += (_, _) => ApplyEdit();
        var close = new Button { Content = "Close", Name = "TextureWorkbenchClose" };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(_apply); buttons.Children.Add(close);
        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(_error); footer.Children.Add(_status); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var projection = new StackPanel { Spacing = 10 };
        projection.Children.Add(Hint("Map every selected face from one world origin and scale. Equal world positions receive equal UVs. Faces edge-on to the chosen direction are refused."));
        _direction = new ComboBox
        {
            Name = "TextureWorkbenchDirection", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "+X  (U: +Y, V: +Z)", "−X  (U: −Y, V: +Z)", "+Y  (U: −X, V: +Z)", "−Y  (U: +X, V: +Z)", "+Z  (U: +X, V: +Y)", "−Z  (U: −X, V: +Y)" },
            SelectedIndex = 4,
        };
        projection.Children.Add(Row("Projection normal", _direction));
        projection.Children.Add(Number("World origin X", "OriginX", 0));
        projection.Children.Add(Number("World origin Y", "OriginY", 0));
        projection.Children.Add(Number("World origin Z", "OriginZ", 0));
        projection.Children.Add(Number("Texels per world unit", "TexelsPerUnit", 64));

        var transform = new StackPanel { Spacing = 10, IsVisible = false };
        transform.Children.Add(Hint("Scale about a shared UV pivot, then rotate counterclockwise in U-right/V-up coordinates, then add the offset. Negative scales mirror; zero scales are refused."));
        transform.Children.Add(Number("Pivot U", "PivotU", 0));
        transform.Children.Add(Number("Pivot V", "PivotV", 0));
        transform.Children.Add(Number("Scale U", "ScaleU", 1));
        transform.Children.Add(Number("Scale V", "ScaleV", 1));
        transform.Children.Add(Number("Rotation (degrees CCW)", "Rotation", 0));
        transform.Children.Add(Number("Offset U", "OffsetU", 0));
        transform.Children.Add(Number("Offset V", "OffsetV", 0));

        _mode.SelectionChanged += (_, _) =>
        {
            projection.IsVisible = _mode.SelectedIndex == 0;
            transform.IsVisible = _mode.SelectedIndex == 1;
            _apply.Content = _mode.SelectedIndex == 1 ? "Transform selected surfaces" : "Project selected surfaces";
            _error.IsVisible = false; _error.Text = null;
        };
        var body = new StackPanel(); body.Children.Add(projection); body.Children.Add(transform);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        if (_surfaces.Length == 0) _status.Text = "Select surfaces before opening the texture workbench.";
    }

    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private static Control Row(string label, Control field)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        Grid.SetColumn(field, 1); row.Children.Add(field);
        return row;
    }

    private Control Number(string label, string key, double value)
    {
        var field = new TextBox { Name = "TextureWorkbench" + key, Text = value.ToString("G", CultureInfo.InvariantCulture) };
        _fields.Add(key, field);
        return Row(label, field);
    }

    private double Read(string key, string label)
    {
        if (!double.TryParse(_fields[key].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new ArgumentException($"{label} must be a finite number; use a decimal point for fractional values.");
        return value;
    }

    private void ApplyEdit()
    {
        _error.IsVisible = false; _error.Text = null; _status.Text = null;
        try
        {
            BatchSurfaceUvCommand command;
            if (_mode.SelectedIndex == 0)
            {
                if (_direction.SelectedIndex is < 0 or > 5) throw new ArgumentException("Choose a projection direction.");
                command = BatchSurfaceUvCommand.Project(_surfaces, (SurfaceUvProjectionDirection)_direction.SelectedIndex,
                    new Vec3(Read("OriginX", "World origin X"), Read("OriginY", "World origin Y"), Read("OriginZ", "World origin Z")),
                    Read("TexelsPerUnit", "Texels per world unit"));
            }
            else if (_mode.SelectedIndex == 1)
            {
                command = BatchSurfaceUvCommand.Transform(_surfaces,
                    new TexVertex(Read("PivotU", "Pivot U"), Read("PivotV", "Pivot V")),
                    new TexVertex(Read("ScaleU", "Scale U"), Read("ScaleV", "Scale V")), Read("Rotation", "Rotation"),
                    new TexVertex(Read("OffsetU", "Offset U"), Read("OffsetV", "Offset V")));
            }
            else throw new ArgumentException("Choose a texture operation.");
            _history.Do(command);
            _status.Text = $"Updated {_surfaces.Length} surface{(_surfaces.Length == 1 ? "" : "s")}. Close this window to undo or redo from the editor.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            _error.Text = error.Message; _error.IsVisible = true;
        }
    }
}
