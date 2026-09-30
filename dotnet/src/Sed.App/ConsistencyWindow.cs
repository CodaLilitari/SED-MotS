using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Sed.Core.Model;
using Sed.Core.Validation;

namespace Sed.App;

/// <summary>
/// Lists the problems <see cref="ConsistencyChecker"/> finds in a level
/// (mirrors the original editor's CONS_CHECKER). Selecting a row raises
/// <see cref="IssueSelected"/> so the shell can jump the views to it.
/// </summary>
public sealed class ConsistencyWindow : Window
{
    /// <summary>Raised when a row is picked; the sector/surface it refers to, if resolvable.</summary>
    public Action<Sector?, Surface?>? IssueSelected;

    private sealed class Row
    {
        public ConsistencyIssue Issue { get; init; } = null!;
        public Sector? Sector { get; init; }
        public Surface? Surface { get; init; }

        public string Location => Issue.Type == ItemType.Thing && Issue.ItemIndex >= 0 ? $"Thing {Issue.ItemIndex}" : Issue.SectorIndex < 0
            ? "—"
            : Issue.SurfaceIndex < 0
                ? $"Sector {Issue.SectorIndex}"
                : $"Sector {Issue.SectorIndex} · Surface {Issue.SurfaceIndex}";
    }

    private Level _level;
    private readonly Func<ConsistencyOptions?>? _options;
    private readonly ListBox _list;
    private readonly TextBlock _summary;
    private readonly CheckBox _containment;

    public ConsistencyWindow(Level level, Func<ConsistencyOptions?>? options = null,
        Func<Sed.Formats.Jkl.JklDocument?>? document = null)
    {
        _level = level;
        _options = options;
        Title = "Consistency Check";
        Width = 520;
        Height = 460;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));

        _summary = new TextBlock
        {
            Margin = new Thickness(10, 8),
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        };

        _list = new ListBox { Background = Brushes.Transparent, ItemTemplate = RowTemplate() };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is Row r) IssueSelected?.Invoke(r.Sector, r.Surface);
        };

        var recheck = new Button { Content = "Re-check", Margin = new Thickness(10, 6) };
        recheck.Click += (_, _) => Run();
        _containment = new CheckBox
        {
            Name = "CheckThingContainment", Content = "Check thing containment (may be slow)",
            Margin = new Thickness(10, 0), IsChecked = false,
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(recheck);
        controls.Children.Add(_containment);
        var bottom = new StackPanel();
        bottom.Children.Add(controls);
        if (document is not null)
        {
            var colors = new Button { Name = "CheckDynamicVertexColors", Content = "MotS dynamic vertex-color readiness…", Margin = new Thickness(10, 0, 10, 6) };
            colors.Click += (_, _) => new DynamicVertexColorsWindow(document).Show(this);
            bottom.Children.Add(colors);
        }

        var root = new DockPanel();
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(_summary);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Run();
    }

    /// <summary>Re-runs the check against the current state of the level.</summary>
    public void SetLevel(Level level) { _level = level; Run(); }

    /// <summary>Re-runs the check against the current state of the level.</summary>
    public void Run()
    {
        var supplied = _options?.Invoke();
        var issues = ConsistencyChecker.Check(_level, new ConsistencyOptions
        {
            RetailProfile = supplied?.RetailProfile ?? false,
            ResolveCogParameters = supplied?.ResolveCogParameters,
            CheckThingContainment = _containment.IsChecked == true,
        });
        var rows = issues.Select(i => new Row
        {
            Issue = i,
            Sector = Resolve(i, out var surface),
            Surface = surface,
        }).ToList();

        _list.ItemsSource = rows;

        int errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        int warnings = issues.Count - errors;
        _summary.Text = issues.Count == 0
            ? $"No problems found across {_level.Sectors.Count} sectors."
            : $"{errors} error(s), {warnings} warning(s) across {_level.Sectors.Count} sectors. " +
              "Select a row to jump to it.";
    }

    private Sector? Resolve(ConsistencyIssue issue, out Surface? surface)
    {
        surface = null;
        if (issue.SectorIndex < 0 || issue.SectorIndex >= _level.Sectors.Count) return null;

        var sector = _level.Sectors[issue.SectorIndex];
        if (issue.SurfaceIndex >= 0 && issue.SurfaceIndex < sector.Surfaces.Count)
            surface = sector.Surfaces[issue.SurfaceIndex];
        return sector;
    }

    private static IDataTemplate RowTemplate() =>
        new FuncDataTemplate<Row>((row, _) =>
        {
            bool error = row?.Issue.Severity == IssueSeverity.Error;

            var badge = new Border
            {
                Background = new SolidColorBrush(error
                    ? Color.FromRgb(0x8b, 0x24, 0x24)
                    : Color.FromRgb(0x7a, 0x5c, 0x16)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = error ? "ERROR" : "WARN",
                    FontSize = 9,
                    Foreground = Brushes.White,
                },
            };

            var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = row?.Issue.Message, FontSize = 12 });
            text.Children.Add(new TextBlock
            {
                Text = row?.Location,
                FontSize = 10,
                Foreground = Brushes.Gray,
            });

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3) };
            sp.Children.Add(badge);
            sp.Children.Add(text);
            return sp;
        });
}
