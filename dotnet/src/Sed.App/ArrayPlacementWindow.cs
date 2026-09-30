using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.App;

/// <summary>Human-facing placement recipes using the same bounded commands as the agent.</summary>
public sealed class ArrayPlacementWindow : Window
{
    private readonly Level _level;
    private readonly EditHistory _history;
    private readonly Action? _validateReferenceClosure;
    private readonly ComboBox _type = Choice("ArrayType", ["New things from template", "New source lights"]);
    private readonly ComboBox _layout = Choice("ArrayLayout", ["Linear", "Grid", "Radial"]);
    private readonly ComboBox _template;
    private readonly ComboBox _sector;
    private readonly ComboBox _layer;
    private readonly TextBox _prefix = Field("ArrayNamePrefix", "array");
    private readonly TextBox _origin = Field("ArrayOrigin", "0, 0, 0");
    private readonly TextBox _orientation = Field("ArrayOrientation", "0, 0, 0");
    private readonly TextBox _step = Field("ArrayStep", "0.25, 0, 0");
    private readonly TextBox _count = Field("ArrayCount", "3");
    private readonly TextBox _stepU = Field("ArrayStepU", "0.25, 0, 0");
    private readonly TextBox _stepV = Field("ArrayStepV", "0, 0.25, 0");
    private readonly TextBox _countU = Field("ArrayCountU", "3");
    private readonly TextBox _countV = Field("ArrayCountV", "3");
    private readonly TextBox _axis = Field("ArrayAxis", "0, 0, 1");
    private readonly TextBox _radius = Field("ArrayRadius", "0.5");
    private readonly TextBox _start = Field("ArrayStartDegrees", "0");
    private readonly TextBox _sweep = Field("ArraySweepDegrees", "360");
    private readonly TextBox _rgb = Field("ArrayRgb", "1, 1, 1");
    private readonly TextBox _intensity = Field("ArrayIntensity", "1");
    private readonly TextBox _range = Field("ArrayRange", "4");
    private readonly CheckBox _inherit = new() { Name = "ArrayInheritRgb", Content = "RGB intensity follows white", IsChecked = true };
    private readonly TextBox _rgbIntensity = Field("ArrayRgbIntensity", "1");
    private readonly TextBox _legacyRange = Field("ArrayLegacyRange", "");
    private readonly TextBox _flags = Field("ArrayFlags", "0");
    private readonly StackPanel _thingFields = new() { Spacing = 6 };
    private readonly StackPanel _lightFields = new() { Spacing = 6 };
    private readonly StackPanel _linearFields = new() { Spacing = 6 };
    private readonly StackPanel _gridFields = new() { Spacing = 6 };
    private readonly StackPanel _radialFields = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { Name = "ArrayStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8) };
    private readonly StackPanel _countRow;

    public ArrayPlacementWindow(Level level, EditHistory history, Action? validateReferenceClosure = null)
    {
        ArgumentNullException.ThrowIfNull(level); ArgumentNullException.ThrowIfNull(history);
        _level = level; _history = history; _validateReferenceClosure = validateReferenceClosure;
        Title = "Place arrays"; Width = 690; Height = 810; MinWidth = 540; MinHeight = 520;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));
        _template = Choice("ArrayTemplate", level.Templates.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray());
        _sector = Choice("ArraySector", new[] { "Automatic: one strict interior match" }.Concat(
            level.Sectors.Select((_, i) => $"Sector {i} (all points must be inside)")).ToArray());
        _layer = Choice("ArrayLayer", level.Layers.Count == 0 ? ["0 (default)"]
            : level.Layers.Select((name, i) => $"{i}: {name}").ToArray());
        var content = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        content.Children.Add(Note("Create up to 256 placements as one undo step. Values use decimal points; vectors are X, Y, Z. New things use declared capacity and strict sector interiors. Source lights do not bake lighting."));
        content.Children.Add(Row("Place", _type));
        content.Children.Add(Row("Editor layer", _layer));
        _thingFields.Children.Add(Row("Template", _template));
        _thingFields.Children.Add(Row("Name prefix", _prefix));
        _thingFields.Children.Add(Note("Names are prefix_000, prefix_001, …; existing names are never reused. New instances carry no copied COG, path or ARCH data."));
        _thingFields.Children.Add(Row("Containing sector", _sector));
        _thingFields.Children.Add(Row("Pitch, yaw, roll (degrees)", _orientation));
        content.Children.Add(_thingFields);
        _lightFields.Children.Add(Row("Color R, G, B (0–1)", _rgb));
        _lightFields.Children.Add(Row("White intensity", _intensity));
        _lightFields.Children.Add(Row("Range", _range));
        _lightFields.Children.Add(_inherit);
        _lightFields.Children.Add(Row("Separate RGB intensity", _rgbIntensity));
        _lightFields.Children.Add(Row("Legacy RGB range (optional)", _legacyRange));
        _lightFields.Children.Add(Note("Legacy RGB range is retained metadata. Both lighting channels use Range when baked. Leave its field blank for no metadata."));
        _lightFields.Children.Add(Row("Flags (decimal)", _flags));
        if (level.Kind != ProjectType.MysteriesOfTheSith)
        {
            _rgb.IsEnabled = false; _inherit.IsEnabled = false;
            _lightFields.Children.Add(Note("JK source lights are white with inherited intensity. Colored and independent RGB authoring require MotS."));
        }
        content.Children.Add(_lightFields);
        content.Children.Add(Row("Layout", _layout));
        content.Children.Add(Row("Origin / radial center", _origin));
        _countRow = Row("Count", _count); content.Children.Add(_countRow);
        _linearFields.Children.Add(Row("Step X, Y, Z", _step));
        content.Children.Add(_linearFields);
        _gridFields.Children.Add(Row("Step U X, Y, Z", _stepU));
        _gridFields.Children.Add(Row("Step V X, Y, Z", _stepV));
        _gridFields.Children.Add(Row("Count U", _countU));
        _gridFields.Children.Add(Row("Count V", _countV));
        _gridFields.Children.Add(Note("U changes fastest. Count U × Count V must be at most 256; overlapping points are refused."));
        content.Children.Add(_gridFields);
        _radialFields.Children.Add(Row("Axis X, Y, Z", _axis));
        _radialFields.Children.Add(Row("Radius", _radius));
        _radialFields.Children.Add(Row("Start angle (degrees)", _start));
        _radialFields.Children.Add(Row("Sweep (−360 to 360)", _sweep));
        _radialFields.Children.Add(Note("The sweep endpoint is excluded, even for partial arcs. Zero sweep/axis are invalid. Start direction projects the least-aligned X/Y/Z axis perpendicular to the chosen axis; positive angles turn toward axis × start. Thing orientation stays fixed."));
        content.Children.Add(_radialFields);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var preview = new Button { Name = "ArrayPreview", Content = "Validate / Preview" };
        preview.Click += (_, _) => Prepare(apply: false); buttons.Children.Add(preview);
        var create = new Button { Name = "ArrayCreate", Content = "Create array" };
        create.Click += (_, _) => Prepare(apply: true); buttons.Children.Add(create);
        var close = new Button { Content = "Close" }; close.Click += (_, _) => Close(); buttons.Children.Add(close);
        content.Children.Add(buttons); content.Children.Add(_status);
        Content = new ScrollViewer { Content = content };
        _type.SelectionChanged += (_, _) => RefreshVisibility();
        _layout.SelectionChanged += (_, _) => RefreshVisibility();
        _inherit.IsCheckedChanged += (_, _) => RefreshVisibility();
        RefreshVisibility();
    }

    private void RefreshVisibility()
    {
        bool things = _type.SelectedIndex == 0;
        _thingFields.IsVisible = things; _lightFields.IsVisible = !things;
        _linearFields.IsVisible = _layout.SelectedIndex == 0;
        _gridFields.IsVisible = _layout.SelectedIndex == 1;
        _radialFields.IsVisible = _layout.SelectedIndex == 2;
        _countRow.IsVisible = _layout.SelectedIndex != 1;
        _rgbIntensity.IsEnabled = _level.Kind == ProjectType.MysteriesOfTheSith && _inherit.IsChecked != true;
        _status.Text = string.Empty;
    }

    private void Prepare(bool apply)
    {
        try
        {
            Vec3 origin = Vector(_origin, "Origin");
            IReadOnlyList<Vec3> points = _layout.SelectedIndex switch
            {
                0 => PlacementArrayLayout.Linear(origin, Vector(_step, "Step"), Integer(_count, "Count")),
                1 => PlacementArrayLayout.Grid(origin, Vector(_stepU, "Step U"), Vector(_stepV, "Step V"), Integer(_countU, "Count U"), Integer(_countV, "Count V")),
                2 => PlacementArrayLayout.Radial(origin, Vector(_axis, "Axis"), Number(_radius, "Radius"), Integer(_count, "Count"), Number(_start, "Start angle"), Number(_sweep, "Sweep")),
                _ => throw new ArgumentException("Choose an array layout."),
            };
            IEditCommand command;
            if (_type.SelectedIndex == 0)
            {
                string template = _template.SelectedItem as string ?? throw new ArgumentException("This level needs a defined template before placing new things.");
                command = new CreateThingArrayCommand(_level, points, template, _prefix.Text ?? string.Empty,
                    _sector.SelectedIndex <= 0 ? null : _level.Sectors[_sector.SelectedIndex - 1],
                    Vector(_orientation, "Pitch, yaw, roll"), _layer.SelectedIndex, _validateReferenceClosure);
            }
            else
            {
                var rgb = Vector(_rgb, "Color R, G, B");
                if (rgb.X is < 0 or > 1 || rgb.Y is < 0 or > 1 || rgb.Z is < 0 or > 1)
                    throw new ArgumentException("Color components must be between zero and one.");
                if (!uint.TryParse(_flags.Text, NumberStyles.None, CultureInfo.InvariantCulture, out uint flags))
                    throw new ArgumentException("Flags must be a decimal unsigned 32-bit integer.");
                command = new CreateLightArrayCommand(_level, points, new ColorF((float)rgb.X, (float)rgb.Y, (float)rgb.Z),
                    Number(_intensity, "White intensity"), Number(_range, "Range"),
                    _inherit.IsChecked == true ? null : Number(_rgbIntensity, "Separate RGB intensity"),
                    string.IsNullOrWhiteSpace(_legacyRange.Text) ? null : Number(_legacyRange, "Legacy RGB range"), flags, _layer.SelectedIndex);
            }
            if (apply) _history.Do(command);
            _status.Foreground = Brushes.LightGreen;
            string positions = string.Join("; ", points.Take(8).Select(point => point.ToString()));
            _status.Text = (apply ? $"Created {points.Count} placements in one undo step. " : $"Valid: {points.Count} placements. No changes made. ")
                + positions + (points.Count > 8 ? "; …" : string.Empty);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            _status.Foreground = Brushes.Salmon; _status.Text = error.Message;
        }
    }

    private static ComboBox Choice(string name, string[] items) => new() { Name = name, ItemsSource = items, SelectedIndex = items.Length > 0 ? 0 : -1, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static TextBox Field(string name, string text) => new() { Name = name, Text = text, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, FontSize = 12 };
    private static StackPanel Row(string label, Control field) => new() { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = Brushes.LightGray }, field } };
    private static double Number(TextBox field, string name)
    {
        if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new ArgumentException($"{name} requires a finite number using a decimal point.");
        return value;
    }
    private static int Integer(TextBox field, string name)
    {
        if (!int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new ArgumentException($"{name} requires an integer.");
        return value;
    }
    private static Vec3 Vector(TextBox field, string name)
    {
        string[] parts = (field.Text ?? string.Empty).Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new ArgumentException($"{name} requires exactly three numbers: X, Y, Z.");
        var values = new double[3];
        for (int i = 0; i < 3; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                throw new ArgumentException($"{name} requires finite numbers using decimal points.");
        return new(values[0], values[1], values[2]);
    }
}
