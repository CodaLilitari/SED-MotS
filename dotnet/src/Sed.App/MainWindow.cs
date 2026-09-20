using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Sed.Core.Editing;
using Sed.Core.Model;
using Sed.Formats.Game;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Launching;
using Sed.Formats.Material;
using Sed.Formats.Resources;
using Sed.Rendering;

namespace Sed.App;

/// <summary>
/// The editor shell. Levels and textures are resolved from a configured game
/// installation directory (Game menu, persisted in <see cref="AppSettings"/>),
/// mirroring the original editor's per-game path options; loose .jkl/.gob files
/// can also be opened directly via File ▸ Open.
/// </summary>
public class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CommandKeys _bindings;
    private readonly TextBlock _status;
    private readonly VulkanView _view;
    private readonly MapView _mapView = new();
    private readonly ListBox _levelList;
    private readonly ItemsControl _layerList = new();
    private readonly ListBox _materialList;
    private readonly TextBox _materialFilter;
    private readonly TextBlock _sidePanelHeader;
    private readonly InspectorPanel _inspector = new();
    private Sed.Formats.Material.MaterialLibrary? _matLibrary;
    private List<MaterialThumb> _matThumbs = new();

    // Active session (whichever source is open).
    private GameInstall? _install;
    private PreparedLevelOpen? _openSession;
    private bool _changingLevelList;
    private GobArchive? _levelArchive;
    private GobArchive[] _materialArchives = Array.Empty<GobArchive>();
    private Sed.Formats.ThreeDo.ModelLibrary? _modelLibrary;
    private JklDocument? _currentDoc;     // for saving the currently loaded level
    private Level? _currentLevel;         // the model the views are editing
    private string _levelName = "level";  // current level's file name (for Save and Test)
    private string? _documentBaseline;
    private List<GobEntry> _levels = new();
    private ConsistencyWindow? _consistencyWindow;
    private FindWindow? _findWindow;
    private HeaderEditorWindow? _headerWindow;
    private TemplateEditorWindow? _templateWindow;
    private CogEditorWindow? _cogWindow;
    private MenuItem? _recentMenu;
    private Avalonia.Threading.DispatcherTimer? _autosaveTimer;
    private Sed.Formats.Cogs.CogScriptLibrary? _cogScripts;
    private AssetCatalog? _assetCatalog;
    private readonly PluginHost _plugins = new();

    /// <summary>The editable document; also usable by isolated headless integration tests.</summary>
    public JklDocument? CurrentDocument => _currentDoc;

    public MainWindow(AppSettings? settings = null)
    {
        _settings = settings ?? AppSettings.Load();
        _bindings = new CommandKeys(_settings);
        Title = "SED — Sith Engine Editor (.NET / Vulkan)";
        Width = 1100;
        Height = 720;

        _status = new TextBlock
        {
            Margin = new Thickness(8, 4),
            Text = "WASD/QE to fly, drag to look, click to select. Game ▸ Set folder, or File ▸ Open.",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _view = new VulkanView(SampleScene.CreateCube())
        {
            SelectionChanged = desc => _status.Text = desc ?? "Nothing selected",
            Bindings = _bindings,
        };

        _inspector.SetHistory(_view.History);

        // Share the undo history and the multi-selection between the 3D and 2D
        // views — one EditHistory, one SelectionSet, so a pick or an edit in
        // either pane is immediately reflected in the other.
        _mapView.History = _view.History;
        _mapView.Selection = _view.Selection;
        _mapView.Layers = _view.Layers;
        _mapView.SelectionChanged = desc => { if (desc is not null) _status.Text = desc; };

        _view.Selection.Changed += () =>
        {
            _mapView.NotifySelectionChanged(_view.ActiveSector);
            UpdateInspectorTarget();
        };
        _view.SelectionFromExternal = (_, _, _, sec) => _mapView.NotifySelectionChanged(sec);

        _view.History.Changed += UpdateInspectorTarget;

        _sidePanelHeader = new TextBlock { Margin = new Thickness(10, 8), Text = "No game configured", Foreground = Brushes.Gray };
        _levelList = new ListBox { Background = Brushes.Transparent };
        _levelList.SelectionChanged += (_, _) => OnLevelSelected();

        _materialList = new ListBox { Background = Brushes.Transparent, ItemTemplate = MaterialItemTemplate() };
        _materialList.SelectionChanged += (_, _) => OnMaterialPicked();

        _materialFilter = new TextBox { PlaceholderText = "Filter…", Margin = new Thickness(4, 2) };
        _materialFilter.TextChanged += (_, _) => FilterMaterials();

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x2b)), Child = _status, Height = 28,
        };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);

        var sideContent = new DockPanel { Width = 240 };
        DockPanel.SetDock(_sidePanelHeader, Dock.Top);
        sideContent.Children.Add(_sidePanelHeader);

        var layerPanel = BuildLayerPanel();
        DockPanel.SetDock(layerPanel, Dock.Bottom);
        sideContent.Children.Add(layerPanel);

        sideContent.Children.Add(_levelList);
        var sidePanel = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22)), Child = sideContent };
        DockPanel.SetDock(sidePanel, Dock.Left);
        root.Children.Add(sidePanel);

        // Right: inspector + material panel (stacked).
        var rightDock = new DockPanel { Width = 240 };

        DockPanel.SetDock(_inspector, Dock.Top);
        rightDock.Children.Add(_inspector);

        // Material inspector (click a material to assign to the selected surface).
        var matContent = new DockPanel();
        var matHeader = new TextBlock { Margin = new Thickness(10, 8), Text = "Materials — click to assign", Foreground = Brushes.Gray };
        DockPanel.SetDock(matHeader, Dock.Top);
        matContent.Children.Add(matHeader);
        DockPanel.SetDock(_materialFilter, Dock.Top);
        matContent.Children.Add(_materialFilter);
        matContent.Children.Add(_materialList);
        rightDock.Children.Add(matContent);

        var rightPanel = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22)), Child = rightDock };
        DockPanel.SetDock(rightPanel, Dock.Right);
        root.Children.Add(rightPanel);

        // Mode toolbar (Sector/Surface/Vertex/Thing/Light).
        var modeBar = BuildModeToolbar();
        DockPanel.SetDock(modeBar, Dock.Top);
        root.Children.Add(modeBar);

        // Center: 3D viewport (top) split over the 2D map view (bottom).
        var center = new Grid { RowDefinitions = new RowDefinitions("3*,Auto,2*") };
        Grid.SetRow(_view, 0);
        center.Children.Add(_view);
        var splitter = new GridSplitter
        {
            Height = 4, ResizeDirection = GridResizeDirection.Rows,
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3c)),
        };
        Grid.SetRow(splitter, 1);
        center.Children.Add(splitter);
        Grid.SetRow(_mapView, 2);
        center.Children.Add(_mapView);
        root.Children.Add(center);
        Content = root;
        _view.History.Changed += RefreshMaterialNames;

        TryAutoOpenConfiguredGame();
        StartAutosave();
        PromptForAutosaveRecovery();
    }

    // ---- game install flow ----

    private static readonly (string label, ProjectType game)[] Games =
    {
        ("Dark Forces II / Jedi Knight", ProjectType.JediKnight),
        ("Mysteries of the Sith", ProjectType.MysteriesOfTheSith),
        ("Indiana Jones and the Infernal Machine", ProjectType.InfernalMachine),
    };

    private void TryAutoOpenConfiguredGame()
    {
        foreach (var (_, game) in Games)
        {
            var dir = _settings.DirFor(game);
            if (!string.IsNullOrEmpty(dir) && GameInstall.IsValid(game, dir))
            {
                OpenGame(game);
                return;
            }
        }
    }

    private async void SetGameFolder(ProjectType game)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Select {Games.First(g => g.game == game).label} installation folder",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        var dir = folder.Path.LocalPath;
        if (!GameInstall.IsValid(game, dir))
        {
            var (level, resources) = GameInstall.ExpectedFiles(game);
            _status.Text = $"That folder doesn't look like a {game} install " +
                           $"(expected e.g. {string.Join(" / ", level.Concat(resources).Take(2))} under it).";
            return;
        }

        _settings.SetDir(game, dir);
        _settings.Save();
        OpenGame(game);
    }

    private void OpenGame(ProjectType game)
    {
        var dir = _settings.DirFor(game);
        if (string.IsNullOrEmpty(dir)) { _status.Text = $"No folder configured for {game}."; return; }

        try
        {
            var prepared = PreparedLevelOpen.FromGame(game, dir);
            CommitPreparedOpen(prepared, $"{game} — {Path.GetFileName(dir.TrimEnd('/', '\\'))}");
        }
        catch (Exception ex)
        {
            _status.Text = $"Failed to open {game}: {ex.Message}";
        }
    }

    // ---- loose file flow ----

    private async void OpenFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open level or archive",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Jedi Knight / MotS levels and archives")
                    { Patterns = new[] { "*.jkl", "*.jed", "*.sed", "*.ned", "*.gob", "*.goo" } },
                FilePickerFileTypes.All,
            },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        OpenPath(file.Path.LocalPath, file.Name);
    }

    /// <summary>Opens a loose .jkl/.gob (the shared path behind File ▸ Open and the recent list).</summary>
    private void OpenPath(string path, string? displayName)
    {
        displayName ??= Path.GetFileName(path);
        try
        {
            var prepared = PreparedLevelOpen.FromPath(path, _settings.DirFor);
            CommitPreparedOpen(prepared, displayName);
            _settings.AddRecent(path);
            RebuildRecentMenu();
        }
        catch (Exception ex)
        {
            _status.Text = $"Failed to open {displayName}: {ex.Message}";
        }
    }

    private void CommitPreparedOpen(PreparedLevelOpen prepared, string header)
    {
        string? recoveryPath;
        try { recoveryPath = SaveRecoveryBeforeSwitch(); }
        catch { prepared.Dispose(); throw; }
        var oldSession = _openSession;
        var oldDocument = _currentDoc;
        var oldName = _levelName;
        var oldArchive = _levelArchive;
        var oldInstall = _install;
        var oldResources = _materialArchives;
        var oldBaseline = _documentBaseline;
        try
        {
            _openSession = prepared;
            _install = prepared.Install;
            _levelArchive = prepared.LevelArchive;
            _materialArchives = prepared.Resources.ToArray();
            ResetResourceCaches();
            _currentDoc = prepared.Document;
            LoadLevel(prepared.Document.Level, prepared.LevelName);
        }
        catch
        {
            _openSession = oldSession;
            _install = oldInstall;
            _levelArchive = oldArchive;
            _materialArchives = oldResources;
            ResetResourceCaches();
            _currentDoc = oldDocument;
            _currentLevel = oldDocument?.Level;
            _levelName = oldName;
            _documentBaseline = oldBaseline;
            prepared.Dispose();
            if (oldDocument is not null)
                try { LoadLevel(oldDocument.Level, oldName); }
                catch { /* preserve the document even if rendering also fails */ }
            throw;
        }
        // Do not release old resources until the candidate document has loaded.
        oldSession?.Dispose();
        RememberDocumentBaseline();
        _consistencyWindow?.Close();
        _consistencyWindow = null;
        PopulateLevels(header, selectFirst: false);
        if (_levels.Count > 0)
        {
            _changingLevelList = true;
            try { _levelList.SelectedIndex = 0; }
            finally { _changingLevelList = false; }
        }
        if (recoveryPath is not null) _status.Text += " — previous edits saved in recovery";
    }

    // ---- shared session ----

    private void PopulateLevels(string header, bool selectFirst = true)
    {
        _levels = _levelArchive?.FindByExtension(".jkl").OrderBy(e => e.NormalizedName).ToList() ?? new();
        _sidePanelHeader.Text = $"{header} — {_levels.Count} levels";
        _changingLevelList = true;
        try { _levelList.ItemsSource = _levels.Select(e => Path.GetFileName(e.NormalizedName)).ToList(); }
        finally { _changingLevelList = false; }
        if (selectFirst && _levels.Count > 0) _levelList.SelectedIndex = 0;
    }

    private void OnLevelSelected()
    {
        if (_changingLevelList) return;
        int i = _levelList.SelectedIndex;
        if (_levelArchive is null || (uint)i >= (uint)_levels.Count) return;
        var entry = _levels[i];
        try
        {
            var document = JklParser.ParseDocument(_levelArchive.ReadText(entry));
            SaveRecoveryBeforeSwitch();
            var oldDocument = _currentDoc;
            try
            {
                _currentDoc = document;
                LoadLevel(document.Level, Path.GetFileName(entry.NormalizedName));
                RememberDocumentBaseline();
                _consistencyWindow?.Close();
                _consistencyWindow = null;
            }
            catch { _currentDoc = oldDocument; _currentLevel = oldDocument?.Level; throw; }
        }
        catch (Exception ex) { _status.Text = $"Failed to parse {entry.Name}: {ex.Message}"; }
    }

    private void NewLevel(ProjectType game)
    {
        try
        {
            var level = LevelFactory.CreateUntexturedRoom(game);
            foreach (var template in Sed.Formats.Jed.JedImporter.LoadTemplates(game))
                level.Templates[template.Name] = template;
            var name = game == ProjectType.MysteriesOfTheSith ? "new_mots.jkl" : "new_jk.jkl";
            var prepared = PreparedLevelOpen.FromDocument(new JklDocument(level), name, _settings.DirFor(game));
            CommitPreparedOpen(prepared, game + " — new level");
            _status.Text = $"New {game} room — add a player start and game content before testing; save with File > Save As.";
        }
        catch (Exception ex) { _status.Text = "Could not create the new level: " + ex.Message; }
    }

    private void LoadLevel(Level level, string name)
    {
        TextureLookup? textures = null;
        ColormapLookup? colormaps = null;
        Func<string, Sed.Formats.ThreeDo.ThreeDoModel?>? models = null;
        byte[]? paletteRgb = null, lightTable = null;

        if (_materialArchives.Length > 0)
        {
            try
            {
                var colormap = MaterialLibrary.LoadPalette(level.ColorMaps, _materialArchives);
                var library = new MaterialLibrary(colormap, _materialArchives);
                _matLibrary = library;
                paletteRgb = colormap.PaletteRgb;
                lightTable = colormap.LightTable;
                textures = material =>
                {
                    var t = library.GetIndexed(material);
                    return t is { } r ? new IndexedTexture(r.Width, r.Height, r.Indices, r.IsSolid, r.HasColorKey) : null;
                };
                colormaps = name => library.GetColormap(name) is { } cmp
                    ? new ColormapData(cmp.PaletteRgb, cmp.LightTable) : null;
                _modelLibrary ??= new Sed.Formats.ThreeDo.ModelLibrary(_materialArchives);
                models = _modelLibrary.Get;
            }
            catch { textures = null; }
        }

        _currentLevel = level;
        _levelName = Path.GetFileNameWithoutExtension(name);
        _inspector.Context = new InspectorContext(this, level, Assets(),
            () => GuiResourceExport.CaptureResources(_materialArchives));
        _view.Layers.ShowAll();      // a new level starts fully visible
        _view.Materials = _currentDoc?.Materials ?? new List<string>();
        _view.SetLevel(level, textures, models, paletteRgb, lightTable, colormaps);
        _mapView.SetLevel(level);
        _consistencyWindow?.Run();
        _findWindow?.SetLevel(level);
        _headerWindow?.SetHeader(level.Header);
        _templateWindow?.SetLevel(level);
        _cogWindow?.SetLevel(level);
        PopulateLayers();
        PopulateMaterials();
        int surfaces = level.Sectors.Sum(s => s.Surfaces.Count);
        _status.Text = $"{name} — {level.Sectors.Count} sectors, {surfaces} surfaces, " +
                       $"{level.Things.Count} things ({(textures is null ? "untextured" : "textured")})";
        if (_currentDoc?.PreservationWarnings.Count > 0)
            _status.Text += $" — {_currentDoc.PreservationWarnings.Count} import/preservation warning(s): {_currentDoc.PreservationWarnings[0]}";
        if (_view.RenderDiagnostics.Count > 0)
            _status.Text += $" — {_view.RenderDiagnostics.Count} rendering warning(s): {_view.RenderDiagnostics[0]}";
    }

    // ---- material inspector ----

    private sealed class MaterialThumb
    {
        public string Name { get; init; } = string.Empty;
        public int Index { get; init; }
        public Avalonia.Media.Imaging.Bitmap? Image { get; init; }
    }

    private static Avalonia.Controls.Templates.IDataTemplate MaterialItemTemplate() =>
        new Avalonia.Controls.Templates.FuncDataTemplate<MaterialThumb>((m, _) =>
        {
            var img = new Image { Width = 32, Height = 32, Source = m?.Image, Stretch = Stretch.Fill };
            var txt = new TextBlock { Text = m?.Name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(img);
            sp.Children.Add(txt);
            return sp;
        });

    private void PopulateMaterials()
    {
        _matThumbs = new List<MaterialThumb>();
        var names = _currentDoc?.Materials ?? new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            Avalonia.Media.Imaging.Bitmap? thumb = null;
            try
            {
                var t = _matLibrary?.Get(names[i]);
                if (t is { } r) thumb = MakeThumb(r.Rgba, r.Width, r.Height);
            }
            catch { /* skip unrenderable */ }
            _matThumbs.Add(new MaterialThumb { Name = names[i], Index = i, Image = thumb });
        }
        _materialList.ItemsSource = _matThumbs;
    }

    private void RefreshMaterialNames()
    {
        if (_currentDoc is null || _currentLevel is null) return;
        bool changed = false;
        foreach (string name in _currentLevel.Sectors.SelectMany(s => s.Surfaces).Select(s => s.Material)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_currentDoc.Materials.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            _currentDoc.Materials.Add(name);
            changed = true;
        }
        if (changed)
        {
            _view.Materials = _currentDoc.Materials;
            PopulateMaterials();
        }
    }

    private void FilterMaterials()
    {
        var query = _materialFilter.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
            _materialList.ItemsSource = _matThumbs;
        else
            _materialList.ItemsSource = _matThumbs
                .Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private static Avalonia.Media.Imaging.Bitmap MakeThumb(byte[] rgba, int w, int h)
    {
        const int S = 32;
        var dst = new byte[S * S * 4];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int sx = w > 0 ? x * w / S : 0, sy = h > 0 ? y * h / S : 0;
                int si = (sy * w + sx) * 4, di = (y * S + x) * 4;
                if (si + 3 < rgba.Length) Array.Copy(rgba, si, dst, di, 4);
            }
        return VulkanViewport.ToBitmap(dst, S, S);
    }

    private void OnMaterialPicked()
    {
        if (_materialList.SelectedItem is MaterialThumb m && !_view.SetSelectedSurfaceMaterial(m.Name, m.Index))
            _status.Text = "Select a surface first, then click a material";
    }

    private async void SaveAs()
    {
        if (_currentDoc is null) { _status.Text = "No level loaded to save."; return; }
        var document = _currentDoc;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save level as JKL",
            DefaultExtension = "jkl",
            SuggestedFileName = "edited.jkl",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Sith engine level (*.jkl)") { Patterns = new[] { "*.jkl" } },
            },
        });
        if (file is null) return;
        if (!ReferenceEquals(document, _currentDoc)) { _status.Text = "The open document changed. Choose Save As again for the current document."; return; }

        try
        {
            using var resources = CaptureExportResources(document);
            string revision = ResourceValidatedExport.DocumentRevision(document);
            var prepared = ResourceValidatedExport.Prepare(document, resources);
            GuiResourceExport.Write([GuiResourceExport.Text(file.Path.LocalPath, prepared.OutputText)],
                GuiResourceExport.FreshnessGuard(document, revision, resources));
            RememberDocumentBaseline();
            _status.Text = $"Saved → {file.Name}. Architectural lighting in current archives: {prepared.Validation.Status}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Save failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Exports a JK/MotS archive with an episode definition and its level entry.
    /// </summary>
    private async void SaveGob()
    {
        if (_currentDoc is null) { _status.Text = "No level loaded to save."; return; }
        var document = _currentDoc;
        string currentLevelName = _levelName;

        bool mots = _currentDoc.Level.Kind == ProjectType.MysteriesOfTheSith;
        string extension = mots ? "goo" : "gob";

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save level archive",
            DefaultExtension = extension,
            SuggestedFileName = "edited." + extension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Sith engine archive") { Patterns = new[] { "*.gob", "*.goo" } },
            },
        });
        if (file is null) return;
        if (!ReferenceEquals(document, _currentDoc)) { _status.Text = "The open document changed. Choose Save as GOB again for the current document."; return; }

        try
        {
            var path = file.Path.LocalPath;
            var entryName = TestEpisode.SafeLevelName(currentLevelName);
            using var resources = CaptureExportResources(document);
            string revision = ResourceValidatedExport.DocumentRevision(document);
            var prepared = ResourceValidatedExport.Prepare(document, resources);
            var verify = GuiResourceExport.FreshnessGuard(document, revision, resources);
            if (document.Level.Kind == ProjectType.InfernalMachine)
                GuiResourceExport.Write([new GuiExportOutput(path, temporary =>
                    GobWriter.Build(temporary, new[] { ($"jkl\\{entryName}.jkl", System.Text.Encoding.ASCII.GetBytes(prepared.OutputText)) }))], verify);
            else
            {
                var package = TestEpisode.Build(document.Level.Kind, entryName + ".jkl", prepared.OutputText);
                GuiResourceExport.Write([new GuiExportOutput(path, temporary =>
                    GobWriter.Build(temporary, package.Entries.Select(e => (e.Name, e.Data))))], verify);
            }
            _status.Text = $"Saved → {file.Name}  (jkl\\{entryName}.jkl). Architectural lighting in current archives: {prepared.Validation.Status}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"GOB save failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Prepares the loose override the engine actually reads, plus a GOB/GOO,
    /// then starts the reviewed executable directly on Windows.
    /// </summary>
    private void SaveAndTest()
    {
        if (_currentDoc is null || _currentLevel is null)
        {
            _status.Text = "Open a level first, then save and test.";
            return;
        }

        var game = _currentLevel.Kind;
        var gameDir = _settings.DirFor(game);
        if (string.IsNullOrEmpty(gameDir))
        {
            _status.Text = "Configure a game folder first (Game ▸ Set Folder), then save and test.";
            return;
        }

        try
        {
            var projectDir = _settings.ResolvedProjectDir();
            Directory.CreateDirectory(projectDir);
            var levelName = TestEpisode.SafeLevelName(_levelName);
            var episodePath = Path.Combine(projectDir, "episode.jk");
            var existingEpisode = File.Exists(episodePath) ? File.ReadAllText(episodePath) : null;
            using var resources = CaptureExportResources(_currentDoc);
            string revision = ResourceValidatedExport.DocumentRevision(_currentDoc);
            var prepared = ResourceValidatedExport.Prepare(_currentDoc, resources);
            var verify = GuiResourceExport.FreshnessGuard(_currentDoc, revision, resources);
            var package = TestEpisode.Build(game, levelName + ".jkl", prepared.OutputText, existingEpisode);
            var gobPath = Path.Combine(projectDir, $"Test_{levelName}{package.ArchiveExtension}");
            GameLaunchPlan? plan = OperatingSystem.IsWindows() ? GameLauncher.PrepareLaunch(new LaunchRequest(
                game, gameDir, projectDir, package.LevelFileName, _settings.TestExecutableFor(game), _settings.TestEngineFor(game))) : null;
            string? command = OperatingSystem.IsWindows() ? null : _settings.BuildTestCommand(game, projectDir, gobPath, levelName);
            if (plan is null && string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("Configure a non-Windows launch command in Game > Test Setup.");

            // -path loads loose resources; a GOB merely sitting in this folder is insufficient.
            var outputs = new List<GuiExportOutput>();
            foreach (var entry in package.Entries)
            {
                if (existingEpisode is not null && entry.Name == "episode.jk") continue;
                var destination = Path.Combine(projectDir, entry.Name.Replace('\\', Path.DirectorySeparatorChar));
                outputs.Add(GuiResourceExport.Bytes(destination, entry.Data));
            }
            outputs.Add(new GuiExportOutput(gobPath, temporary => GobWriter.Build(temporary, package.Entries.Select(e => (e.Name, e.Data)))));
            GuiResourceExport.Write(outputs, verify);
            verify();
            LaunchTest(plan, command, gameDir);
            _status.Text = $"Saved {Path.GetFileName(gobPath)} and loose test files; select the test episode in the game menus. Architectural lighting in current archives: {prepared.Validation.Status}. Runtime resource overrides are unverified.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Save-and-test failed: {ex.Message}";
        }
    }

    private ExplicitArchiveResources? CaptureExportResources(JklDocument document)
    {
        if (!document.Level.Things.Any(thing => thing.ArchLightingIndex >= 0)) return null;
        return GuiResourceExport.CaptureResources(_materialArchives)
            ?? throw new InvalidOperationException("Configure the game's resource archives before exporting architectural lighting. Recovery and backup remain available.");
    }

    private static void LaunchTest(GameLaunchPlan? plan, string? command, string gameDir)
    {
        if (plan is not null)
        {
            var start = new System.Diagnostics.ProcessStartInfo(plan.Executable)
                { WorkingDirectory = plan.WorkingDirectory, UseShellExecute = false };
            foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("The game process could not be started.");
            return;
        }
        if (!OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(command))
        {
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh")
                { WorkingDirectory = gameDir, UseShellExecute = false };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("The launch command could not be started.");
            return;
        }
        throw new InvalidOperationException("No launch plan was prepared.");
    }

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "level" : name;
    }

    // ---- autosave / backup / recovery (the original's SaveTimer + backup copies) ----

    private void RememberDocumentBaseline()
    {
        try { _documentBaseline = _currentDoc is null ? null : DocumentRecovery.Fingerprint(_currentDoc); }
        catch { _documentBaseline = null; }
    }

    private string? SaveRecoveryBeforeSwitch()
    {
        if (_currentDoc is null) return null;
        string text;
        try { text = JklWriter.Build(_currentDoc); }
        catch when (!_view.History.CanUndo && !_view.History.CanRedo && _documentBaseline is null)
        { return null; } // An untouched recovery/import-only document still exists in its source file.
        string fingerprint = DocumentRecovery.FingerprintText(text);
        if (fingerprint == _documentBaseline) return null;
        return DocumentRecovery.Save(Path.Combine(_settings.ResolvedProjectDir(), "recovery"), _levelName, text);
    }

    private void RecoverPreviousSession()
    {
        string directory = Path.Combine(_settings.ResolvedProjectDir(), "recovery");
        string? newest = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.jkl")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        if (newest is null) { _status.Text = "No recovery snapshot has been saved yet."; return; }
        OpenPath(newest, Path.GetFileName(newest) + " (recovered)");
    }

    private void StartAutosave()
    {
        _autosaveTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.AutoSaveIntervalMinutes)),
        };
        _autosaveTimer.Tick += (_, _) => AutosaveNow();
        _autosaveTimer.IsEnabled = _settings.AutoSave;
    }

    private void AutosaveNow()
    {
        if (_currentDoc is null) return;
        try
        {
            var dir = Path.Combine(_settings.ResolvedProjectDir(), "autosave");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{SanitizeName(_levelName)}.jkl");
            File.WriteAllText(path, JklWriter.Build(_currentDoc));
            _status.Text = $"Autosaved → {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Autosave failed: {ex.Message}";
        }
    }

    /// <summary>Numbered backup copy into the project dir's backup folder (the original's 100 slots).</summary>
    private void MakeBackupCopy()
    {
        if (_currentDoc is null)
        {
            _status.Text = "Open a level first, then make a backup.";
            return;
        }

        try
        {
            var dir = Path.Combine(_settings.ResolvedProjectDir(), "backup");
            Directory.CreateDirectory(dir);
            string? path = null;
            for (int n = 0; n < 100; n++)
            {
                var candidate = Path.Combine(dir, $"{SanitizeName(_levelName)}_{n:00}.jkl");
                if (!File.Exists(candidate)) { path = candidate; break; }
            }
            if (path is null)
            {
                _status.Text = "You have 100 backup copies! Remove some.";
                return;
            }
            File.WriteAllText(path, JklWriter.Build(_currentDoc));
            _status.Text = $"Backup → {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Backup failed: {ex.Message}";
        }
    }

    /// <summary>Offers the newest autosaved level once the window is up (crash recovery).</summary>
    private void PromptForAutosaveRecovery()
    {
        Opened += async (_, _) =>
        {
            var dir = Path.Combine(_settings.ResolvedProjectDir(), "autosave");
            if (!Directory.Exists(dir)) return;
            var newest = Directory.EnumerateFiles(dir, "*.jkl")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (newest is null) return;

            if (await Dialogs.Confirm(this, "Recover autosave",
                    $"An autosaved version of {Path.GetFileName(newest)} was found " +
                    $"(saved {File.GetLastWriteTime(newest):g}). Recover it?"))
                OpenPath(newest, $"{Path.GetFileName(newest)} (recovered)");
        };
    }

    /// <summary>Rebuilds the File ▸ Recent Files submenu from the settings.</summary>
    private void RebuildRecentMenu()
    {
        if (_recentMenu is null) return;
        _recentMenu.Items.Clear();

        var recent = _settings.RecentFiles.Where(File.Exists).ToList();
        if (recent.Count == 0)
        {
            _recentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        foreach (var p in recent)
        {
            var item = new MenuItem { Header = Path.GetFileName(p), Icon = Glyph("\uE61E") };
            var path = p;
            item.Click += (_, _) => OpenPath(path, null);
            _recentMenu.Items.Add(item);
        }
        _recentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear Recent Files" };
        clear.Click += (_, _) =>
        {
            _settings.RecentFiles.Clear();
            _settings.Save();
            RebuildRecentMenu();
        };
        _recentMenu.Items.Add(clear);
    }

    private void ShowTestSetup()
    {
        new TestSetupWindow(_settings).ShowDialog(this);
    }

    private void ClearSession()
    {
        _currentDoc = null;
        _currentLevel = null;
        _openSession?.Dispose(); _openSession = null;
        _install = null;
        _levelArchive = null;
        _materialArchives = Array.Empty<GobArchive>();
        ResetResourceCaches();
        _materialList.ItemsSource = null;
        _levels = new();
        _levelList.ItemsSource = null;
    }

    private void ResetResourceCaches()
    {
        _cogScripts = null;
        _assetCatalog = null;
        _modelLibrary = null;
        _matLibrary = null;
    }

    /// <summary>
    /// Layer visibility list (mirrors the original's layer bar). Unchecking a layer
    /// removes its sectors, things and lights from both views and from picking,
    /// so a complex level can be worked on a floor at a time. Visibility is editor
    /// state — it changes nothing that gets saved.
    /// </summary>
    private Control BuildLayerPanel()
    {
        var header = new TextBlock
        {
            Margin = new Thickness(10, 8, 10, 4),
            Text = "Layers",
            Foreground = Brushes.Gray,
        };

        var showAll = new Button
        {
            Content = "Show all",
            FontSize = 11,
            Padding = new Thickness(6, 1),
            Margin = new Thickness(10, 0, 10, 6),
        };
        showAll.Click += (_, _) => _view.Layers.ShowAll();

        var content = new DockPanel { MaxHeight = 190 };
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        DockPanel.SetDock(showAll, Dock.Bottom);
        content.Children.Add(showAll);
        content.Children.Add(_layerList);

        // Rebuild the checkboxes when visibility changes elsewhere (e.g. Show all).
        _view.Layers.Changed += RefreshLayerChecks;

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3c)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = content,
        };
    }

    /// <summary>
    /// Rebuilds the layer checkbox list. Retail levels ship without a LAYERS
    /// section but still place everything on layer 0, so the list is sized by the
    /// highest layer index actually in use as well as by the declared names —
    /// otherwise those levels would show nothing to toggle.
    /// </summary>
    private void PopulateLayers()
    {
        _layerList.Items.Clear();
        if (_currentLevel is not { } level) return;

        var layers = level.Layers;
        int used = 0;
        foreach (var s in level.Sectors) used = System.Math.Max(used, s.Layer + 1);
        foreach (var t in level.Things) used = System.Math.Max(used, t.Layer + 1);
        foreach (var l in level.Lights) used = System.Math.Max(used, l.Layer + 1);
        int count = System.Math.Max(layers.Count, used);

        for (int i = 0; i < count; i++)
        {
            int index = i;
            var declared = i < layers.Count ? layers[i] : string.Empty;
            var name = string.IsNullOrWhiteSpace(declared) ? $"Layer {i}" : declared;
            var box = new CheckBox
            {
                Content = $"{i}: {name}",
                IsChecked = _view.Layers.IsVisible(i),
                FontSize = 11,
                Margin = new Thickness(10, 0),
            };
            box.IsCheckedChanged += (_, _) => _view.Layers.SetVisible(index, box.IsChecked ?? true);
            _layerList.Items.Add(box);
        }

        if (count == 0)
            _layerList.Items.Add(new TextBlock
            {
                Text = "(no layers)",
                Foreground = Brushes.DimGray,
                FontSize = 11,
                Margin = new Thickness(10, 2),
            });
    }

    /// <summary>Syncs the checkboxes after a visibility change made elsewhere.</summary>
    private void RefreshLayerChecks()
    {
        int i = 0;
        foreach (var item in _layerList.Items)
        {
            if (item is CheckBox box)
            {
                bool visible = _view.Layers.IsVisible(i);
                if (box.IsChecked != visible) box.IsChecked = visible;
                i++;
            }
        }
        _mapView.InvalidateVisual();
    }

    private Menu BuildMenu()
    {
        var open = new MenuItem { Header = "_Open…", Icon = Glyph("\uE61E") };
        open.Click += (_, _) => OpenFile();
        var exit = new MenuItem { Header = "E_xit" };
        exit.Click += (_, _) => (Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(Item(null, "New _MotS Level", new KeyGesture(Key.N, KeyModifiers.Control),
            () => NewLevel(ProjectType.MysteriesOfTheSith)));
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.N, KeyModifiers.Control),
            Command = new RelayCommand(() => NewLevel(ProjectType.MysteriesOfTheSith)) });
        file.Items.Add(Item(null, "New _JK Level", null, () => NewLevel(ProjectType.JediKnight)));
        file.Items.Add(new Separator());
        file.Items.Add(open);
        var saveAs = new MenuItem { Header = "Save _As…", InputGesture = new KeyGesture(Key.S, KeyModifiers.Control), Icon = Glyph("\uE236") };
        saveAs.Click += (_, _) => SaveAs();
        file.Items.Add(saveAs);
        file.Items.Add(Item(null, "Save as _GOB…", null, SaveGob));
        file.Items.Add(new Separator());
        file.Items.Add(Item(null, "Save GOB and _Test…", null, SaveAndTest));
        file.Items.Add(new Separator());
        file.Items.Add(Item(null, "Make a _Backup Copy…", null, MakeBackupCopy));
        file.Items.Add(Item(null, "Recover _Previous Session", null, RecoverPreviousSession));
        file.Items.Add(new Separator());
        _recentMenu = new MenuItem { Header = "Recent _Files" };
        RebuildRecentMenu();
        file.Items.Add(_recentMenu);
        file.Items.Add(new Separator());
        file.Items.Add(exit);

        var undo = new MenuItem { Header = "_Undo", InputGesture = _bindings.Primary(CommandKeys.Undo) };
        undo.Click += (_, _) => _view.History.Undo();
        var redo = new MenuItem { Header = "_Redo", InputGesture = _bindings.Primary(CommandKeys.Redo) };
        redo.Click += (_, _) => _view.History.Redo();
        foreach (var g in _bindings.Gestures(CommandKeys.Undo))
            KeyBindings.Add(new KeyBinding { Gesture = g, Command = new RelayCommand(() => _view.History.Undo()) });
        foreach (var g in _bindings.Gestures(CommandKeys.Redo))
            KeyBindings.Add(new KeyBinding { Gesture = g, Command = new RelayCommand(() => _view.History.Redo()) });
        var newSector = new MenuItem { Header = "_New Box Sector", InputGesture = new KeyGesture(Key.N) };
        newSector.Click += (_, _) => _view.CreateSector();
        var delSector = new MenuItem { Header = "Delete Se_ctor" };
        delSector.Click += (_, _) => _view.DeleteSector();

        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(undo);
        edit.Items.Add(redo);
        edit.Items.Add(new Separator());
        edit.Items.Add(Item(CommandKeys.Copy, "_Copy", new KeyGesture(Key.C, KeyModifiers.Control), () => _view.CopySelection()));
        edit.Items.Add(Item(CommandKeys.Paste, "_Paste", new KeyGesture(Key.V, KeyModifiers.Control), () => _view.PasteClipboard()));
        edit.Items.Add(Item(CommandKeys.Duplicate, "_Duplicate", new KeyGesture(Key.D, KeyModifiers.Control), () => _view.DuplicateSelection()));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item(CommandKeys.SelectAllInSector, "Select All in _Sector", new KeyGesture(Key.A, KeyModifiers.Control),
            () => _view.SelectActiveSectorSurfaces()));
        edit.Items.Add(Item(CommandKeys.SelectNone, "Select _None", new KeyGesture(Key.Escape), () => _view.ClearSelection()));
        edit.Items.Add(new Separator());
        edit.Items.Add(newSector);
        edit.Items.Add(delSector);
        _view.History.Changed += () =>
        {
            undo.IsEnabled = _view.History.CanUndo;
            redo.IsEnabled = _view.History.CanRedo;
            undo.Header = _view.History.CanUndo ? $"_Undo {_view.History.UndoName}" : "_Undo";
            redo.Header = _view.History.CanRedo ? $"_Redo {_view.History.RedoName}" : "_Redo";
        };
        undo.IsEnabled = false;
        redo.IsEnabled = false;

        var game = new MenuItem { Header = "_Game" };
        foreach (var (label, kind) in Games)
        {
            var item = new MenuItem { Header = $"Set _{label} Folder…" };
            item.Click += (_, _) => SetGameFolder(kind);
            game.Items.Add(item);
        }
        game.Items.Add(new Separator());
        game.Items.Add(Item(null, "Test _Setup…", null, ShowTestSetup));

        var view = new MenuItem { Header = "_View" };
        var brightness = new MenuItem { Header = "Cycle _Brightness", InputGesture = new KeyGesture(Key.B), Icon = Glyph("\uE18C") };
        brightness.Click += (_, _) => _view.CycleBrightness();
        view.Items.Add(brightness);

        var geometry = BuildGeometryMenu();
        var texturing = BuildTexturingMenu();
        var tools = BuildToolsMenu();

        var plugins = BuildPluginsMenu();

        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(new MenuItem { Header = "About SED" });

        var menu = new Menu();
        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(geometry);
        menu.Items.Add(texturing);
        menu.Items.Add(tools);
        menu.Items.Add(game);
        menu.Items.Add(view);
        menu.Items.Add(plugins);
        menu.Items.Add(help);
        return menu;
    }

    /// <summary>
    /// Builds the Plugins menu from whatever managed plugins are installed. The
    /// menu is always present so the reload action and any load problems are
    /// discoverable even when nothing is installed.
    /// </summary>
    private MenuItem BuildPluginsMenu()
    {
        var menu = new MenuItem { Header = "_Plugins" };
        RefreshPluginsMenu(menu);
        return menu;
    }

    private void RefreshPluginsMenu(MenuItem menu)
    {
        _plugins.LoadFrom(PluginHost.DefaultDirectory);
        menu.Items.Clear();

        foreach (var plugin in _plugins.Plugins)
        {
            var entry = new MenuItem { Header = plugin.Name };
            foreach (var command in plugin.Commands)
            {
                var item = new MenuItem { Header = command.Label };
                var captured = command;
                item.Click += (_, _) => RunPlugin(captured);
                entry.Items.Add(item);
            }
            if (plugin.Commands.Count == 0)
                entry.Items.Add(new MenuItem { Header = "(no commands)", IsEnabled = false });
            menu.Items.Add(entry);
        }

        foreach (var problem in _plugins.Problems)
            menu.Items.Add(new MenuItem { Header = $"⚠ {problem}", IsEnabled = false });

        if (_plugins.Plugins.Count == 0 && _plugins.Problems.Count == 0)
            menu.Items.Add(new MenuItem
            {
                Header = $"No plugins in {PluginHost.DefaultDirectory}",
                IsEnabled = false,
            });

        menu.Items.Add(new Separator());
        var reload = new MenuItem { Header = "_Reload plugins" };
        reload.Click += (_, _) =>
        {
            RefreshPluginsMenu(menu);
            _status.Text = $"Loaded {_plugins.Plugins.Count} plugin(s), {_plugins.Problems.Count} problem(s).";
        };
        menu.Items.Add(reload);
    }

    /// <summary>Runs a plugin command against the live level, selection and undo stack.</summary>
    private void RunPlugin(Sed.Plugins.PluginCommand command)
    {
        if (_currentLevel is not { } level)
        {
            _status.Text = "Open a level first, then run a plugin.";
            return;
        }

        var context = new Sed.Plugins.PluginContext(
            level, _view.History, _view.Selection, message => _status.Text = message);

        _status.Text = PluginHost.Invoke(command, context);
    }

    /// <summary>Surface-mode geometry operations. All act on the 3D view's selected surface.</summary>
    private MenuItem BuildGeometryMenu()
    {
        var geometry = new MenuItem { Header = "_Geometry" };

        geometry.Items.Add(Item(CommandKeys.Extrude, "_Extrude Surface", new KeyGesture(Key.E, KeyModifiers.Control),
            () => _view.ExtrudeSelectedSurface()));
        geometry.Items.Add(Item(CommandKeys.ExtrudeInward, "Extrude _Inward", new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift),
            () => _view.ExtrudeSelectedSurface(-1.0)));
        geometry.Items.Add(new Separator());
        geometry.Items.Add(Item(CommandKeys.FlipSurface, "_Flip Surface", new KeyGesture(Key.F, KeyModifiers.Control),
            () => _view.FlipSelectedSurface()));
        geometry.Items.Add(Item(CommandKeys.CleaveSurface, "_Cleave Surface", new KeyGesture(Key.K, KeyModifiers.Control),
            () => _view.CleaveSelectedSurface()));
        geometry.Items.Add(new Separator());
        geometry.Items.Add(Item(CommandKeys.MakeAdjoin, "Make _Adjoin (pick two)", new KeyGesture(Key.J, KeyModifiers.Control),
            () => _view.MakeAdjoinStep()));
        geometry.Items.Add(Item(CommandKeys.RemoveAdjoin, "_Remove Adjoin", new KeyGesture(Key.J, KeyModifiers.Control | KeyModifiers.Shift),
            () => _view.RemoveSelectedAdjoin()));
        geometry.Items.Add(Item(CommandKeys.BridgeSurfaces, "_Bridge Two Surfaces", new KeyGesture(Key.B, KeyModifiers.Control),
            () => _view.BridgeSelectedSurfaces()));
        geometry.Items.Add(Item(CommandKeys.ConnectSectors, "Co_nnect Two Sectors", new KeyGesture(Key.B, KeyModifiers.Control | KeyModifiers.Shift),
            () => _view.ConnectSelectedSectors()));

        return geometry;
    }

    /// <summary>Per-surface UV transforms, mirroring the original's &amp;Texturing menu.</summary>
    private MenuItem BuildTexturingMenu()
    {
        var texturing = new MenuItem { Header = "_Texturing" };

        texturing.Items.Add(Item(CommandKeys.ShiftTextureLeft, "Shift _Left", new KeyGesture(Key.Left, KeyModifiers.Control),
            () => _view.ShiftSelectedTexture(-0.125, 0)));
        texturing.Items.Add(Item(CommandKeys.ShiftTextureRight, "Shift _Right", new KeyGesture(Key.Right, KeyModifiers.Control),
            () => _view.ShiftSelectedTexture(0.125, 0)));
        texturing.Items.Add(Item(CommandKeys.ShiftTextureUp, "Shift _Up", new KeyGesture(Key.Up, KeyModifiers.Control),
            () => _view.ShiftSelectedTexture(0, -0.125)));
        texturing.Items.Add(Item(CommandKeys.ShiftTextureDown, "Shift _Down", new KeyGesture(Key.Down, KeyModifiers.Control),
            () => _view.ShiftSelectedTexture(0, 0.125)));
        texturing.Items.Add(new Separator());
        texturing.Items.Add(Item(CommandKeys.ScaleTextureUp, "Scale U_p", new KeyGesture(Key.OemPlus, KeyModifiers.Control),
            () => _view.ScaleSelectedTexture(1.0 / 0.9)));
        texturing.Items.Add(Item(CommandKeys.ScaleTextureDown, "Scale Do_wn", new KeyGesture(Key.OemMinus, KeyModifiers.Control),
            () => _view.ScaleSelectedTexture(0.9)));
        texturing.Items.Add(new Separator());
        texturing.Items.Add(Item(CommandKeys.RotateTextureCw, "Rotate _CW", new KeyGesture(Key.R, KeyModifiers.Control),
            () => _view.RotateSelectedTexture(15)));
        texturing.Items.Add(Item(CommandKeys.RotateTextureCcw, "Rotate CC_W", new KeyGesture(Key.R, KeyModifiers.Control | KeyModifiers.Shift),
            () => _view.RotateSelectedTexture(-15)));
        texturing.Items.Add(new Separator());
        texturing.Items.Add(Item(CommandKeys.AutoFitTexture, "_Auto-fit to Surface", new KeyGesture(Key.T, KeyModifiers.Control),
            () => _view.AutoTextureSelected()));
        texturing.Items.Add(Item(CommandKeys.AlignTextureNeighbour, "Align to _Neighbour", new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift),
            () => _view.AlignSelectedTextureToNeighbour()));

        return texturing;
    }

    private MenuItem BuildToolsMenu()
    {
        var tools = new MenuItem { Header = "T_ools" };
        tools.Items.Add(Item(CommandKeys.CalculateLighting, "Calculate _Lighting", new KeyGesture(Key.F9),
            () => _view.CalculateLighting()));
        tools.Items.Add(Item(CommandKeys.CalculateLightingNoShadows, "Calculate Lighting (_no shadows)", new KeyGesture(Key.F9, KeyModifiers.Shift),
            () => _view.CalculateLighting(castShadows: false)));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item(null, "Level _Header\u2026", null, ShowHeaderEditor));
        tools.Items.Add(Item(CommandKeys.Find, "_Find\u2026", new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift), ShowFind));
        tools.Items.Add(Item(CommandKeys.CheckConsistency, "_Check Consistency\u2026", new KeyGesture(Key.F8), ShowConsistency));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item(null, "_Key Bindings…", null, ShowKeyBindings));
        tools.Items.Add(Item(null, "_Episode Editor…", null, ShowEpisodeEditor));
        tools.Items.Add(Item(null, "3_DO Model Viewer…", null, ShowThreeDoViewer));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item(null, "Import _Dark Forces Level…", null, ImportDfLevel));
        tools.Items.Add(Item(null, "Import _3D Studio ASC…", null, ImportAsc));
        tools.Items.Add(Item(null, "E_xport Sector as 3DO…", null, ExportSectorAs3Do));
        return tools;
    }

    /// <summary>
    /// Imports a Dark Forces .lev (Tools ▸ Import Dark Forces Level…), replacing
    /// the current level. The sibling .O file and the game's df2jk.lst table are
    /// picked up automatically.
    /// </summary>
    private async void ImportDfLevel()
    {
        var dialog = new DfImportWindow(_settings);
        await dialog.ShowDialog(this);
        if (dialog.LevPath is null) return;

        try
        {
            var gameDir = _settings.DirFor(_install?.Game ?? ProjectType.JediKnight) ?? string.Empty;
            var table = gameDir.Length > 0 ? Sed.Formats.Df.DfLevelImporter.LoadLogicTable(gameDir) : null;

            var options = new Sed.Formats.Df.DfImportOptions
            {
                ScaleFactor = dialog.ScaleFactor,
                KeepTextureNames = dialog.KeepTextureNames,
                LogicTable = table,
            };

            var (level, warnings) = Sed.Formats.Df.DfLevelImporter.ImportFile(dialog.LevPath, options);

            // Reuse the session's archives (materials/models still resolve); the
            // document is a fresh one so the material panel reflects the import.
            var doc = new JklDocument(level);
            foreach (var m in level.Sectors.SelectMany(s => s.Surfaces)
                         .Select(s => s.Material)
                         .Where(m => m.Length > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                doc.Materials.Add(m);

            CommitPreparedOpen(PreparedLevelOpen.FromDocument(doc, Path.GetFileName(dialog.LevPath), _settings.DirFor(level.Kind)),
                Path.GetFileName(dialog.LevPath));

            _status.Text = $"Imported {Path.GetFileName(dialog.LevPath)} — {level.Sectors.Count} sectors, " +
                           $"{level.Things.Count} things" +
                           (warnings.Count > 0 ? $"; {warnings.Count} warning(s)" : "");
        }
        catch (Exception ex)
        {
            _status.Text = $"DF import failed: {ex.Message}";
        }
    }

    /// <summary>Imports a 3D Studio ASC file as level geometry (one sector per Tri-mesh).</summary>
    private async void ImportAsc()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open 3D Studio ASCII file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("3D Studio ASCII (*.asc)") { Patterns = new[] { "*.asc" } },
                FilePickerFileTypes.All,
            },
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        try
        {
            var level = Sed.Formats.Asc.AscImporter.Import(File.ReadAllText(file.Path.LocalPath));
            var doc = new JklDocument(level);
            CommitPreparedOpen(PreparedLevelOpen.FromDocument(doc, file.Name, _settings.DirFor(level.Kind)), file.Name);

            _status.Text = $"Imported {file.Name} — {level.Sectors.Count} sector(s), " +
                           $"{level.Things.Count} thing(s)";
        }
        catch (Exception ex)
        {
            _status.Text = $"ASC import failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Exports the selected sectors (or the active one) as a 3DO model — the
    /// original's "Export Sector as 3DO", grouped into one mesh per layer.
    /// </summary>
    private async void ExportSectorAs3Do()
    {
        if (_currentLevel is not { } level)
        {
            _status.Text = "Open a level first, then export a sector.";
            return;
        }

        var sectors = _view.Selection.Sectors.Count > 0
            ? _view.Selection.Sectors.ToList()
            : _view.ActiveSector is { } active ? new List<Sector> { active } : null;
        if (sectors is null || sectors.Count == 0)
        {
            _status.Text = "Select a sector first (or enter Sector mode), then export.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export sector as 3DO",
            DefaultExtension = "3do",
            SuggestedFileName = "sector.3do",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Sith engine model (*.3do)") { Patterns = new[] { "*.3do" } },
            },
        });
        if (file is null) return;

        try
        {
            var model = Sed.Formats.ThreeDo.ThreeDoExport.BuildModel(level, sectors,
                i => (uint)i < (uint)level.Layers.Count ? level.Layers[i] : $"Layer{i}");
            Sed.Formats.ThreeDo.ThreeDoWriter.Save(model, file.Path.LocalPath);

            int faces = model.Meshes.Sum(m => m.Faces.Count);
            _status.Text = $"Exported {sectors.Count} sector(s), {model.Meshes.Count} mesh(es), " +
                           $"{faces} face(s) → {file.Name}";
        }
        catch (Exception ex)
        {
            _status.Text = $"3DO export failed: {ex.Message}";
        }
    }

    /// <summary>Opens the command-key remapper.</summary>
    private void ShowKeyBindings()
    {
        new KeyBindingsWindow(_settings, _bindings).ShowDialog(this);
    }

    /// <summary>Opens the episode editor (episode.jk + cogstrings.uni in the project dir).</summary>
    private void ShowEpisodeEditor()
    {
        new EpisodeEditorWindow(_settings).ShowDialog(this);
    }

    /// <summary>Opens the 3DO model / KEY animation browser.</summary>
    private void ShowThreeDoViewer()
    {
        new ThreeDoViewerWindow().ShowDialog(this);
    }

    /// <summary>Opens the level-header editor for the loaded level.</summary>
    private void ShowHeaderEditor()
    {
        if (_currentLevel is not { } level)
        {
            _status.Text = "Open a level first, then edit its header.";
            return;
        }

        if (_headerWindow is not null)
        {
            _headerWindow.Rebuild();
            _headerWindow.Activate();
            return;
        }

        var window = new HeaderEditorWindow(level.Header, _view.History);
        window.Closed += (_, _) => _headerWindow = null;
        _headerWindow = window;
        window.Show(this);
    }

    /// <summary>
    /// The asset catalog for the open session, built on first use. Both the level
    /// archive and the resource archives are searched, since materials and models
    /// can live in either.
    /// </summary>
    private AssetCatalog Assets() =>
        _assetCatalog ??= new AssetCatalog(new[] { _levelArchive }, _materialArchives);

    /// <summary>
    /// Opens the placed-COG editor. Script resolution needs both the level archive
    /// (level-specific scripts) and the resource archives (shared ones).
    /// </summary>
    private void ShowCogEditor()
    {
        if (_currentLevel is not { } level)
        {
            _status.Text = "Open a level first, then edit its COGs.";
            return;
        }

        if (_cogWindow is not null)
        {
            _cogWindow.Refresh();
            _cogWindow.Activate();
            return;
        }

        _cogScripts ??= new Sed.Formats.Cogs.CogScriptLibrary(
            new[] { _levelArchive }, _materialArchives);

        var window = new CogEditorWindow(level, _view.History, _cogScripts, Assets());
        window.Closed += (_, _) => _cogWindow = null;
        _cogWindow = window;
        window.Show(this);
    }

    /// <summary>Opens the template browser/editor for the loaded level.</summary>
    private void ShowTemplateEditor()
    {
        if (_currentLevel is not { } level)
        {
            _status.Text = "Open a level first, then edit its templates.";
            return;
        }

        if (_templateWindow is not null)
        {
            _templateWindow.Refresh();
            _templateWindow.Activate();
            return;
        }

        var window = new TemplateEditorWindow(level, _view.History, Assets());
        window.Closed += (_, _) => _templateWindow = null;
        _templateWindow = window;
        window.Show(this);
    }

    /// <summary>Opens (or re-runs) the find dialog for the loaded level.</summary>
    private void ShowFind()
    {
        if (_currentLevel is not { } level)
        {
            _status.Text = "Open a level first, then use Find.";
            return;
        }

        if (_findWindow is not null)
        {
            _findWindow.Search();
            _findWindow.Activate();
            return;
        }

        var window = new FindWindow(level)
        {
            ResultChosen = result =>
            {
                _view.RevealFindResult(result);
                UpdateInspectorTarget();
            },
            ResultsSelected = results =>
            {
                _view.SelectFindResults(results);
                UpdateInspectorTarget();
                _status.Text = $"Selected {results.Count} match(es).";
            },
        };
        window.Closed += (_, _) => _findWindow = null;
        _findWindow = window;
        window.Show(this);
    }

    /// <summary>
    /// Builds a menu item for a configurable command and — because Avalonia's
    /// <see cref="MenuItem.InputGesture"/> is display-only — registers matching
    /// window-level key bindings so the advertised shortcut actually fires.
    /// <see cref="VulkanView"/> resolves the same bindings when it has focus and
    /// marks them handled, so they run exactly once.
    /// </summary>
    private MenuItem Item(string? action, string header, KeyGesture? defaultGesture, Action onClick)
    {
        var gestures = action is null ? Array.Empty<KeyGesture>() : _bindings.Gestures(action);
        var item = new MenuItem { Header = header, InputGesture = gestures.FirstOrDefault() ?? defaultGesture };
        item.Click += (_, _) => onClick();
        foreach (var gesture in gestures)
            KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new RelayCommand(onClick) });
        return item;
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _run;
        public RelayCommand(Action run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _run();
    }

    /// <summary>Opens (or re-runs) the consistency checker for the loaded level.</summary>
    private void ShowConsistency()
    {
        if (_currentLevel is not { } level)
        {
            _status.Text = "Open a level first, then run the consistency check.";
            return;
        }

        if (_consistencyWindow is not null)
        {
            _consistencyWindow.Run();
            _consistencyWindow.Activate();
            return;
        }

        _cogScripts ??= new Sed.Formats.Cogs.CogScriptLibrary(new[] { _levelArchive }, _materialArchives);
        var window = new ConsistencyWindow(level, () => Sed.Formats.Cogs.CogValidation.Options(_cogScripts,
            _settings.TestEngineFor(level.Kind) == GameEngineProfile.Retail))
        {
            IssueSelected = (sector, surface) =>
            {
                if (surface is not null) _view.SetExternalSelection(null, null, surface);
                else if (sector is not null) _view.SetExternalSelection(null, null, sector.Surfaces.FirstOrDefault());
                UpdateInspectorTarget();
            },
        };
        window.Closed += (_, _) => _consistencyWindow = null;
        _consistencyWindow = window;
        window.Show(this);
    }

    private static readonly FontFamily Phosphor = new("avares://Sed.App/Assets/Phosphor.ttf#Phosphor");

    private static TextBlock Glyph(string glyph, double size = 13) => new()
    {
        Text = glyph,
        FontFamily = Phosphor,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 4, 0),
    };

    private Control BuildModeToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        bar.Children.Add(new TextBlock
        {
            Text = "Mode: ",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0),
            FontSize = 11,
            Foreground = Brushes.Gray,
        });

        EditMode[] modes = { EditMode.Sector, EditMode.Surface, EditMode.Vertex, EditMode.Thing, EditMode.Light };
        string[] labels = { "Sector", "Surface", "Vertex", "Thing", "Light" };
        string[] keys = { "S", "F", "V", "T", "L" };
        string[] icons = { "\uED0A", "\uE45E", "\uE6CE", "\uECFE", "\uE2DC" };
        var toggleButtons = new List<ToggleButton>();

        for (int i = 0; i < modes.Length; i++)
        {
            var mode = modes[i];
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Glyph(icons[i]));
            content.Children.Add(new TextBlock
            {
                Text = $"{labels[i]} ({keys[i]})",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var btn = new ToggleButton
            {
                Content = content,
                Padding = new Thickness(6, 2),
                IsChecked = mode == EditMode.Surface,
            };
            btn.IsCheckedChanged += (_, _) =>
            {
                if (btn.IsChecked == true)
                {
                    foreach (var other in toggleButtons)
                        if (other != btn) other.IsChecked = false;
                    SetMode(mode);
                }
            };
            toggleButtons.Add(btn);
            bar.Children.Add(btn);
        }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x2b)),
            Child = bar,
            Padding = new Thickness(0, 2),
        };
        return border;
    }

    private void SetMode(EditMode mode)
    {
        _view.Mode = mode;
        _mapView.Mode = mode;
        UpdateInspectorTarget();
        _inspector.Mode = mode;
        _status.Text = $"Mode: {mode}";
    }

    private void UpdateInspectorTarget()
    {
        object? target = _view.Mode switch
        {
            EditMode.Sector => (object?)_view.ActiveSector,
            EditMode.Surface => (object?)_view.SelectedSurface,
            EditMode.Vertex => (object?)_view.SelectedVertex,
            EditMode.Thing => (object?)_view.SelectedThing,
            EditMode.Light => _view.Selection.PrimaryLight,
            _ => null,
        };
        _inspector.SetTarget(target);
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearSession();
        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        try { SaveRecoveryBeforeSwitch(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            _status.Text = "Could not save a recovery snapshot; the document remains open: " + ex.Message;
        }
        base.OnClosing(e);
    }
}
