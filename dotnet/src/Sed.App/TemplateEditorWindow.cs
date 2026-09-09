using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sed.Core.Editing;
using Sed.Core.Model;
using Avalonia.Controls.Primitives;

namespace Sed.App;

/// <summary>
/// Browse and edit the level's thing templates (`U_TEMPLATES.PAS` /
/// `U_TPLCREATE.PAS`). The TEMPLATES section already parses and writes
/// faithfully; this exposes it.
///
/// The right pane separates a template's **own** parameters from those it
/// inherits through its parent chain — inherited rows are shown greyed with the
/// template they come from, since that is the thing you actually need to know
/// when deciding whether to override one.
/// </summary>
public sealed class TemplateEditorWindow : Window
{
    private readonly EditHistory _history;
    private readonly AssetCatalog? _assets;
    private Level _level;

    private readonly TextBox _filter;
    private readonly ListBox _list;
    private readonly StackPanel _detail;
    private readonly TextBlock _summary;

    private Template? _selected;

    public TemplateEditorWindow(Level level, EditHistory history, AssetCatalog? assets = null)
    {
        _level = level;
        _history = history;
        _assets = assets;

        Title = "Templates";
        Width = 720;
        Height = 560;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));

        _filter = new TextBox { PlaceholderText = "Filter…", Margin = new Thickness(6, 6, 6, 4) };
        _filter.TextChanged += (_, _) => PopulateList();

        _list = new ListBox { Background = Brushes.Transparent };
        _list.SelectionChanged += (_, _) =>
        {
            _selected = _list.SelectedItem as Template;
            BuildDetail();
        };

        _summary = new TextBlock
        {
            Margin = new Thickness(6, 4),
            Foreground = Brushes.Gray,
            FontSize = 11,
        };

        var newBtn = SmallButton("New", NewTemplate);
        var cloneBtn = SmallButton("Clone", CloneTemplate);
        var deleteBtn = SmallButton("Delete", DeleteTemplate);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 2, 6, 6),
            Spacing = 4,
        };
        buttons.Children.Add(newBtn);
        buttons.Children.Add(cloneBtn);
        buttons.Children.Add(deleteBtn);

        var left = new DockPanel { Width = 250 };
        DockPanel.SetDock(_filter, Dock.Top);
        left.Children.Add(_filter);
        DockPanel.SetDock(_summary, Dock.Top);
        left.Children.Add(_summary);
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        left.Children.Add(_list);

        _detail = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };

        var root = new DockPanel();
        var leftBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3c)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = left,
        };
        DockPanel.SetDock(leftBorder, Dock.Left);
        root.Children.Add(leftBorder);
        root.Children.Add(new ScrollViewer { Content = _detail });
        Content = root;

        PopulateList();
    }

    /// <summary>Points the window at another level (after File ▸ Open).</summary>
    public void SetLevel(Level level)
    {
        _level = level;
        _selected = null;
        PopulateList();
    }

    /// <summary>Rebuilds both panes — call after an edit or an undo.</summary>
    public void Refresh()
    {
        PopulateList();
        BuildDetail();
    }

    private void PopulateList()
    {
        var query = _filter.Text?.Trim() ?? string.Empty;
        var items = _level.Templates.Values
            .OrderBy(t => t.Order)
            .Where(t => query.Length == 0 ||
                        t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        t.Parent.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _list.ItemsSource = items;
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Template>((t, _) =>
            new TextBlock { Text = t?.Name, FontSize = 12, Margin = new Thickness(4, 2) });

        if (_selected is not null && items.Contains(_selected)) _list.SelectedItem = _selected;
        else if (_selected is not null) { _selected = null; BuildDetail(); }

        _summary.Text = $"{items.Count} of {_level.Templates.Count} template(s)";
    }

    private void BuildDetail()
    {
        _detail.Children.Clear();
        if (_selected is not { } tpl)
        {
            _detail.Children.Add(new TextBlock
            {
                Text = "Select a template.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(4),
            });
            return;
        }

        Heading(tpl.Name);

        // Name (rename repoints things and child templates).
        _detail.Children.Add(InspectorPanel.Row("Name", InspectorPanel.TextField(tpl.Name, text =>
        {
            if (string.Equals(text, tpl.Name, StringComparison.Ordinal)) return;
            if (RenameTemplateCommand.Validate(_level, tpl, text) is { } problem) { Warn(problem); return; }
            Commit(new RenameTemplateCommand(_level, tpl, text));
        })));

        _detail.Children.Add(InspectorPanel.Row("Parent", PickerField.Build(this, "Parent template", tpl.Parent,
            () => PickerField.Templates(_level),
            text =>
            {
                if (string.Equals(text, tpl.Parent, StringComparison.Ordinal)) return;
                Commit(new SetTemplateParentCommand(tpl, text));
            })));

        int users = DeleteTemplateCommand.CountUsers(_level, tpl.Name);
        _detail.Children.Add(new TextBlock
        {
            Text = users == 0 ? "Not referenced by any thing or template." : $"Referenced {users} time(s).",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(4, 2, 4, 6),
        });

        Heading("Parameters");
        foreach (var (key, value) in tpl.Values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string paramKey = key;
            var kind = TemplateParams.Describe(TemplateParams.KindOf(key));
            var label = kind.Length > 0 ? $"{key}  ({kind})" : key;

            var kindOf = TemplateParams.KindOf(key);
            Control editor = ParamEditor(kindOf, value, text =>
                Commit(new SetTemplateValueCommand(tpl, paramKey, text)));

            var row = InspectorPanel.Row(label, editor);

            var remove = SmallButton("×", () => Commit(new SetTemplateValueCommand(tpl, paramKey, null)));
            remove.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(remove);

            _detail.Children.Add(row);
        }

        if (tpl.Values.Count == 0)
            _detail.Children.Add(Note("No own parameters — everything is inherited."));

        AddParameterRow(tpl);
        BuildInherited(tpl);
    }

    /// <summary>
    /// Chooses an editor for a parameter by its kind: asset kinds get a browse
    /// button onto the archives, template references onto the level's templates,
    /// everything else stays a plain text box.
    /// </summary>
    private Control ParamEditor(TemplateParamKind kind, string value, Action<string> onCommit)
    {
        if (kind == TemplateParamKind.TemplateRef)
            return PickerField.Build(this, "Template", value, () => PickerField.Templates(_level), onCommit);

        if (AssetCatalog.ExtensionFor(kind) is { } extension && _assets is not null)
            return PickerField.Build(this, TemplateParams.Describe(kind), value,
                () => PickerField.Assets(_assets, extension), onCommit);

        return InspectorPanel.TextField(value, onCommit);
    }

    /// <summary>A key/value pair plus an Add button, for introducing a new parameter.</summary>
    private void AddParameterRow(Template tpl)
    {
        var key = new TextBox { PlaceholderText = "new parameter", FontSize = 11, MinWidth = 120 };
        var value = new TextBox { PlaceholderText = "value", FontSize = 11, MinWidth = 120, Margin = new Thickness(4, 0, 0, 0) };

        void Add()
        {
            var k = key.Text?.Trim() ?? string.Empty;
            var v = value.Text?.Trim() ?? string.Empty;
            if (k.Length == 0 || v.Length == 0) { Warn("Enter both a parameter name and a value."); return; }
            if (k.Any(char.IsWhiteSpace) || k.Contains('=')) { Warn("Parameter names cannot contain spaces or '='."); return; }
            Commit(new SetTemplateValueCommand(tpl, k, v));
        }

        value.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };

        var add = SmallButton("Add", Add);
        add.Margin = new Thickness(4, 0, 0, 0);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6) };
        row.Children.Add(key);
        row.Children.Add(value);
        row.Children.Add(add);
        _detail.Children.Add(row);
    }

    /// <summary>Lists parameters coming from the parent chain, with their source.</summary>
    private void BuildInherited(Template tpl)
    {
        var inherited = new List<(string key, string value, string from)>();
        var seen = new HashSet<string>(tpl.Values.Keys, StringComparer.OrdinalIgnoreCase);

        var name = tpl.Parent;
        for (int depth = 0; depth < 32 && !string.IsNullOrEmpty(name); depth++)
        {
            if (!_level.Templates.TryGetValue(name, out var parent)) break;
            foreach (var (k, v) in parent.Values)
                if (seen.Add(k)) inherited.Add((k, v, parent.Name));
            name = parent.Parent;
        }

        if (inherited.Count == 0) return;

        Heading("Inherited");
        foreach (var (k, v, from) in inherited.OrderBy(x => x.key, StringComparer.OrdinalIgnoreCase))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 1) };
            row.Children.Add(new TextBlock
            {
                Text = k, Width = 130, FontSize = 11, Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = v, Width = 150, FontSize = 11, Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"from {from}", FontSize = 10, Foreground = Brushes.DimGray,
                VerticalAlignment = VerticalAlignment.Center,
            });

            // Override: copy the inherited value onto this template so it can differ.
            string ik = k, iv = v;
            var over = SmallButton("override", () => Commit(new SetTemplateValueCommand(tpl, ik, iv)));
            over.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(over);

            _detail.Children.Add(row);
        }
    }

    // ---- template-level actions ----

    private void NewTemplate()
    {
        var name = UniqueName("new_template");
        var tpl = new Template { Name = name, Parent = _selected?.Name ?? string.Empty };
        var command = new CreateTemplateCommand(_level, tpl);
        _history.Do(command);
        _selected = tpl;
        Refresh();
    }

    private void CloneTemplate()
    {
        if (_selected is not { } source) { Warn("Select a template to clone."); return; }

        var clone = new Template { Name = UniqueName(source.Name + "_copy"), Parent = source.Parent };
        foreach (var (k, v) in source.Values) clone.Values[k] = v;
        clone.SourceParameters.AddRange(source.SourceParameters);

        _history.Do(new CreateTemplateCommand(_level, clone));
        _selected = clone;
        Refresh();
    }

    private void DeleteTemplate()
    {
        if (_selected is not { } tpl) { Warn("Select a template to delete."); return; }

        int users = DeleteTemplateCommand.CountUsers(_level, tpl.Name);
        _history.Do(new DeleteTemplateCommand(_level, tpl));
        _selected = null;
        Refresh();

        if (users > 0)
            Warn($"Deleted '{tpl.Name}' — {users} reference(s) now point at a missing template. Ctrl+Z to undo.");
    }

    private string UniqueName(string basis)
    {
        if (!_level.Templates.ContainsKey(basis)) return basis;
        for (int i = 2; ; i++)
        {
            var candidate = $"{basis}{i}";
            if (!_level.Templates.ContainsKey(candidate)) return candidate;
        }
    }

    private void Commit(IEditCommand command)
    {
        _history.Do(command);
        Refresh();
    }

    // ---- small view helpers ----

    private void Heading(string text) =>
        _detail.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(4, 10, 4, 4),
        });

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Gray,
        FontSize = 11,
        Margin = new Thickness(4, 2),
    };

    private void Warn(string message) => _summary.Text = message;

    private static Button SmallButton(string content, Action onClick)
    {
        var button = new Button { Content = content, FontSize = 11, Padding = new Thickness(6, 1) };
        button.Click += (_, _) => onClick();
        return button;
    }
}
