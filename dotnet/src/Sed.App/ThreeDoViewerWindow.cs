using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Sed.Formats.Keyframe;
using Sed.Formats.ThreeDo;

namespace Sed.App;

/// <summary>
/// The 3DO model browser (the original's U_3DOS / U_3DOPREV data side): loads a
/// .3do (and optionally a .key animation), shows the hierarchy tree with each
/// node's mesh, parent, offset and orientation, and — when a key file is
/// loaded — a frame scrubber that shows the interpolated pose of the selected
/// node at each frame (TKEYNode.GetFrame).
/// </summary>
public sealed class ThreeDoViewerWindow : Window
{
    private readonly TreeView _tree;
    private readonly StackPanel _detail;
    private readonly TextBlock _summary;
    private readonly Slider _scrubber;
    private readonly TextBlock _frameLabel;
    private readonly Button _keyButton;

    private ThreeDoModel? _model;
    private KeyFile? _key;
    private sealed class NodeItem
    {
        public readonly string Kind;
        public readonly string Label;
        public object? Tag;
        public List<NodeItem>? Children;

        public NodeItem(string kind, string label, object? tag)
        {
            Kind = kind;
            Label = label;
            Tag = tag;
        }
    }

    public ThreeDoViewerWindow()
    {
        Title = "3DO Model Viewer";
        Width = 620;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));

        _tree = new TreeView
        {
            Background = Brushes.Transparent,
            MinWidth = 280,
            ItemTemplate = new FuncTreeDataTemplate(
                typeof(NodeItem),
                (item, _) => new TextBlock { Text = (item as NodeItem)?.Label ?? string.Empty, FontSize = 12, Margin = new Thickness(4, 2) },
                item => (item as NodeItem)?.Children ?? []),
        };
        _tree.SelectionChanged += (_, _) => ShowDetail();
        _detail = new StackPanel { Margin = new Thickness(12) };
        _summary = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(4, 2) };

        _frameLabel = new TextBlock { FontSize = 12, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
        _scrubber = new Slider { Minimum = 0, Maximum = 1, Value = 0, IsSnapToTickEnabled = false, VerticalAlignment = VerticalAlignment.Center, MinWidth = 240 };
        _scrubber.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Value") ShowDetail();
        };

        var scrubRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4) };
        scrubRow.Children.Add(_frameLabel);
        scrubRow.Children.Add(_scrubber);

        var modelButton = new Button { Content = "Load 3DO…", Margin = new Thickness(0, 0, 6, 0) };
        modelButton.Click += (_, _) => LoadModel();
        _keyButton = new Button { Content = "Load KEY…", IsEnabled = false };
        _keyButton.Click += (_, _) => LoadKey();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8) };
        buttons.Children.Add(modelButton);
        buttons.Children.Add(_keyButton);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Top);
        root.Children.Add(buttons);
        DockPanel.SetDock(scrubRow, Dock.Top);
        root.Children.Add(scrubRow);
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(_summary);

        var splitterHost = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        var left = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3c)), BorderThickness = new Thickness(0, 0, 1, 0), Child = _tree };
        Grid.SetColumn(left, 0);
        splitterHost.Children.Add(left);
        var right = new ScrollViewer { Content = _detail };
        Grid.SetColumn(right, 1);
        splitterHost.Children.Add(right);
        root.Children.Add(splitterHost);

        Content = root;
    }

    private async void LoadModel()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a 3DO model",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Sith engine model (*.3do)") { Patterns = new[] { "*.3do" } },
                FilePickerFileTypes.All,
            },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            _model = ThreeDoParser.Parse(File.ReadAllText(file.Path.LocalPath));
            _model.Name = Path.GetFileNameWithoutExtension(file.Name);
            _key = null;
            _keyButton.IsEnabled = true;
            PopulateTree();
        }
        catch (Exception ex)
        {
            _summary.Text = $"Failed to parse {file.Name}: {ex.Message}";
        }
    }

    private async void LoadKey()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a KEY animation",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Sith engine animation (*.key)") { Patterns = new[] { "*.key" } },
                FilePickerFileTypes.All,
            },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            _key = KeyFile.Parse(File.ReadAllText(file.Path.LocalPath));
            _key.Name = Path.GetFileNameWithoutExtension(file.Name);
            PopulateTree();
        }
        catch (Exception ex)
        {
            _summary.Text = $"Failed to parse {file.Name}: {ex.Message}";
        }
    }

    private void PopulateTree()
    {
        if (_model is null) return;
        _tree.ItemsSource = null;

        var root = new List<NodeItem>();

        var meshes = new NodeItem("section", $"{_model.Name} — {_model.Meshes.Count} mesh(es), {_model.Nodes.Count} node(s)", null);
        root.Add(meshes);
        var meshChildren = new List<NodeItem>();
        for (int i = 0; i < _model.Meshes.Count; i++)
        {
            var m = _model.Meshes[i];
            meshChildren.Add(new NodeItem("mesh", $"Mesh {i}: {m.Name} — {m.Vertices.Count} verts, {m.Faces.Count} faces, {m.Uvs.Count} UVs", m));
        }
        meshes.Children = meshChildren;

        if (_model.Nodes.Count > 0)
        {
            var hierarchy = new NodeItem("section", $"Hierarchy — {_model.Nodes.Count} node(s)", null);
            root.Add(hierarchy);
            var nodeChildren = new List<NodeItem>();
            for (int i = 0; i < _model.Nodes.Count; i++)
            {
                var n = _model.Nodes[i];
                var name = i < _model.Meshes.Count && _model.Meshes[i].Name.Length > 0
                    ? _model.Meshes[i].Name : "$$DUMMY";
                nodeChildren.Add(new NodeItem("node",
                    $"Node {i}: {name} (mesh {n.Mesh}, parent {n.Parent})", n));
            }
            hierarchy.Children = nodeChildren;
        }

        if (_key is { } key)
        {
            var anim = new NodeItem("section",
                $"Animation: {key.Name} — {key.FrameCount} frames @ {key.Fps} fps, {key.Nodes.Count} node(s)", null);
            root.Add(anim);
            var animChildren = new List<NodeItem>();
            foreach (var kn in key.Nodes)
                animChildren.Add(new NodeItem("keynode", $"{kn.MeshName} — {kn.Entries.Count} key(s)", kn));
            anim.Children = animChildren;

            _scrubber.Maximum = Math.Max(1, key.FrameCount - 1);
            _scrubber.Value = 0;
            _scrubber.IsEnabled = true;
        }
        else
        {
            _scrubber.Maximum = 1;
            _scrubber.Value = 0;
            _scrubber.IsEnabled = false;
        }

        _tree.ItemsSource = root;
        _summary.Text = _key is null
            ? "Load a .key file to scrub the animation frames."
            : $"{_model?.Name} — drag the scrubber to step through the animation.";
        ShowDetail();
    }

    private void ShowDetail()
    {
        _detail.Children.Clear();
        var frame = (int)Math.Round(_scrubber.Value);
        _frameLabel.Text = _key is null ? "—" : $"frame {frame}";

        if (_tree.SelectedItem is not NodeItem item) return;

        switch (item.Kind)
        {
            case "mesh":
                if (item.Tag is Mesh3do m)
                {
                    AddDetail("Mesh", m.Name.Length > 0 ? m.Name : "(unnamed)");
                    AddDetail("Vertices", m.Vertices.Count.ToString());
                    AddDetail("Texture vertices", m.Uvs.Count.ToString());
                    AddDetail("Faces", m.Faces.Count.ToString());
                    if (m.Faces.FirstOrDefault() is { } f)
                    {
                        AddDetail("Face material index", f.Material.ToString());
                        AddDetail("Face flags", $"0x{f.FaceFlags:x}");
                    }
                }
                break;

            case "node":
                if (item.Tag is HierarchyNode n)
                {
                    AddDetail("Mesh", n.Mesh.ToString());
                    AddDetail("Parent", n.Parent.ToString());
                    AddDetail("Offset", Fmt(n.Offset));
                    AddDetail("Pitch / Yaw / Roll", $"{n.Pitch:0.###} / {n.Yaw:0.###} / {n.Roll:0.###}");

                    // When a key file animates this mesh, show the interpolated pose.
                    if (_key is { } key && _model is { } model && KeyNodeFor(key, model, n) is { } kn)
                    {
                        AddDetail("Animation", kn.MeshName);
                        if (kn.GetFrame(frame, out var x, out var y, out var z, out var pch, out var yaw, out var rol))
                            AddDetail($"Pose @ frame {frame}",
                                $"({x:0.###}, {y:0.###}, {z:0.###})  pch {pch:0.###} yaw {yaw:0.###} rol {rol:0.###}");
                    }
                }
                break;

            case "keynode":
                if (item.Tag is KeyNode kn2)
                {
                    AddDetail("Mesh", kn2.MeshName);
                    AddDetail("Keys", kn2.Entries.Count.ToString());
                    if (kn2.GetFrame(frame, out var x2, out var y2, out var z2, out var pch2, out var yaw2, out var rol2))
                        AddDetail($"Pose @ frame {frame}",
                            $"({x2:0.###}, {y2:0.###}, {z2:0.###})  pch {pch2:0.###} yaw {yaw2:0.###} rol {rol2:0.###}");
                    if (kn2.Entries.FirstOrDefault() is { } first)
                        AddDetail("First key", $"frame {first.Frame}, flags 0x{first.Flags:x}");
                }
                break;
        }
    }

    /// <summary>The KEY node that animates the mesh this hierarchy node draws.</summary>
    private static KeyNode? KeyNodeFor(KeyFile key, ThreeDoModel model, HierarchyNode node)
    {
        var meshName = (uint)node.Mesh < (uint)model.Meshes.Count ? model.Meshes[node.Mesh].Name : string.Empty;
        return key.Nodes.FirstOrDefault(kn =>
            kn.MeshName.Equals(meshName, StringComparison.OrdinalIgnoreCase));
    }

    private void AddDetail(string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 150, FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        _detail.Children.Add(row);
    }

    private static string Fmt(Sed.Core.Math.Vec3 v) => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})";
}
