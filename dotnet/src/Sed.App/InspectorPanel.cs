using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sed.Core.Editing;
using Sed.Core.Model;

namespace Sed.App;

/// <summary>
/// What an inspector needs beyond the entity itself in order to offer pickers:
/// a window to parent the dialog on, the level (for thing/sector/template lists)
/// and the archive catalog (for asset lists). All optional — inspectors fall back
/// to plain text fields without it.
/// </summary>
public sealed record InspectorContext(
    Avalonia.Controls.Window? Owner = null,
    Sed.Core.Model.Level? Level = null,
    AssetCatalog? Assets = null,
    Func<Sed.Formats.Resources.ExplicitArchiveResources?>? CaptureArchitectureResources = null);

/// <summary>
/// A contextual inspector panel that rebuilds its content based on the current
/// EditMode and selection. Mirrors the original SED's adaptive TItemEdit
/// (ITEM_EDIT.PAS) — one panel, reflowed per entity type.
/// </summary>
public sealed class InspectorPanel : Border
{
    private readonly ScrollViewer _scroll = new();
    private EditMode _mode = EditMode.Surface;
    private object? _target;
    private EditHistory? _history;
    private bool _refreshing;
    private bool _refreshPending;

    /// <summary>Context handed to inspectors so their name fields can offer pickers.</summary>
    public InspectorContext Context { get; set; } = new();

    public InspectorPanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));
        Width = 240;
        Child = _scroll;
    }

    public void SetHistory(EditHistory history) => _history = history;

    public EditMode Mode
    {
        get => _mode;
        set { _mode = value; Refresh(); }
    }

    public void SetTarget(object? target)
    {
        _target = target;
        Refresh();
    }

    private void Refresh()
    {
        // Replacing focused content can commit its edit on LostFocus. History
        // callbacks may request another target while Avalonia is still replacing
        // the visual child; coalesce that refresh until the assignment completes.
        if (_refreshing) { _refreshPending = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                RefreshContent();
            } while (_refreshPending);
        }
        finally { _refreshing = false; }
    }

    private void RefreshContent()
    {
        Control? content;
        if (_target is null || _history is null)
            content = MakeEmpty();
        else
            content = (_mode, _target) switch
            {
                (EditMode.Sector,  Sector s)  => SectorInspector.Build(s, _history),
                (EditMode.Surface, Surface s) => SurfaceInspector.Build(s, _history, Context),
                (EditMode.Vertex,  Vertex v)  => VertexInspector.Build(v, _history),
                (EditMode.Thing,   Thing t)   => ThingInspector.Build(t, _history, Context),
                (EditMode.Light,   Light l)   => LightInspector.Build(l, _history, Context),
                _ => MakeEmpty(),
            };
        _scroll.Content = content;
    }

    private static Control MakeEmpty()
    {
        return new TextBlock
        {
            Text = "Nothing selected",
            Margin = new Thickness(10, 8),
            Foreground = Brushes.Gray,
        };
    }

    // ---- shared field-building helpers ----

    internal static StackPanel Row(string label, Control editor)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 2),
        };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Width = 90,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = Brushes.LightGray,
        });
        sp.Children.Add(editor);
        return sp;
    }

    internal static TextBox TextField(string value, Action<string> onCommit)
    {
        var tb = new TextBox
        {
            Text = value,
            FontSize = 11,
            MinWidth = 110,
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                onCommit(tb.Text ?? string.Empty);
        };
        tb.LostFocus += (_, _) => onCommit(tb.Text ?? string.Empty);
        return tb;
    }

    internal static TextBox NumericField(double value, Action<double> onCommit)
    {
        var tb = new TextBox
        {
            Text = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 11,
            MinWidth = 110,
        };
        void Commit()
        {
            if (double.TryParse(tb.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
                onCommit(v);
        }
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };
        tb.LostFocus += (_, _) => Commit();
        return tb;
    }

    internal static CheckBox CheckField(string label, bool value, Action<bool> onCommit)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            FontSize = 11,
        };
        cb.IsCheckedChanged += (_, _) => onCommit(cb.IsChecked ?? false);
        return cb;
    }

    internal static StackPanel Panel(string title)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        sp.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(4, 6, 4, 4),
            Foreground = Brushes.White,
        });
        return sp;
    }
}
