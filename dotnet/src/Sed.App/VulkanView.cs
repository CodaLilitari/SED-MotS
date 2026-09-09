using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sed.Core;
using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.ThreeDo;
using Sed.Rendering;
using Sed.Rendering.Vulkan;

namespace Sed.App;

/// <summary>
/// A live Vulkan 3D viewport with a fly camera (WASD + Q/E, mouse-look on drag,
/// wheel to dolly) and click-to-pick surface selection. Renders the level to an
/// offscreen image blitted into a bitmap.
/// </summary>
public sealed class VulkanView : Control
{
    private readonly VulkanContext? _ctx;
    private readonly VulkanDevice? _device;
    private readonly SceneRenderer? _renderer;
    private readonly string? _error;

    private Level? _level;

    // Fly camera state
    private Vec3 _camPos;
    private double _yaw, _pitch;
    private double _fov = 65, _near = 0.05, _far = 500;
    private double _moveSpeed = 0.2;

    private WriteableBitmap? _bitmap;
    private PixelSize _lastSize;

    private readonly HashSet<Key> _pressed = new();
    private readonly DispatcherTimer _moveTimer;
    private Point? _lastPointer;
    private Point _pressOrigin;
    private bool _dragging;

    // Selection + editing
    private double _markerSize = 0.1;
    private Sector? _activeSector;        // vertices of this sector are shown for editing

    /// <summary>The current editing mode — controls what the picker selects.</summary>
    public EditMode Mode { get; set; } = EditMode.Surface;

    /// <summary>
    /// The shared multi-selection. <see cref="MainWindow"/> assigns one instance to
    /// both this view and the <see cref="MapView"/>, so the two stay in lock-step.
    /// The single-item properties below expose the <em>primary</em> (most recently
    /// picked) member, which is what the inspector and the surface-mode operations
    /// act on.
    /// </summary>
    public SelectionSet Selection { get; } = new();

    /// <summary>
    /// Shared layer visibility. Hidden layers are omitted from the scene and from
    /// picking, so you cannot select geometry you cannot see.
    /// </summary>
    public LayerVisibility Layers { get; } = new();

    public Sector? ActiveSector => _activeSector;
    public Surface? SelectedSurface => Selection.PrimarySurface;
    public Vertex? SelectedVertex => Selection.PrimaryVertex;
    public Thing? SelectedThing => Selection.PrimaryThing;

    private TextureLookup? _textures;
    private Func<string, ThreeDoModel?>? _models;
    private HashSet<string> _sceneMaterialNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Material names available for assignment (for cycling a surface's material).</summary>
    public List<string> Materials { get; set; } = new();

    /// <summary>Undo/redo history for edits made in this viewport.</summary>
    public EditHistory History { get; } = new();

    /// <summary>Raised when the selection changes; argument is a human-readable description (or null).</summary>
    public Action<string?>? SelectionChanged;

    /// <summary>Called when an external view (MapView) changes the selection. Updates 3D highlight.</summary>
    public Action<Vertex?, Thing?, Surface?, Sector?>? SelectionFromExternal;

    public VulkanView(Level level)
    {
        try
        {
            _ctx = VulkanContext.Create("SED");
            _device = VulkanDevice.Create(_ctx);
            _renderer = new SceneRenderer(_device);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }

        ClipToBounds = true;
        Focusable = true;
        _moveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _moveTimer.Tick += (_, _) => MoveTick();
        History.Changed += OnHistoryChanged;
        Selection.Changed += OnSelectionChanged;
        Layers.Changed += OnLayersChanged;
        SetLevel(level);
    }

    private List<Thing> _markerThings = new();

    public void SetLevel(Level level, TextureLookup? textures = null, Func<string, ThreeDoModel?>? models = null,
        byte[]? paletteRgb = null, byte[]? lightTable = null)
    {
        if (_renderer is null) return;
        _loadingLevel = true;
        _level = level;
        _textures = textures;
        _models = models;
        Selection.Clear();
        _activeSector = null;
        _renderer.SetSelection(null);
        History.Clear();

        if (paletteRgb is not null && lightTable is not null)
            _renderer.SetColormap(paletteRgb, lightTable);
        _renderer.SetSky(level.Header.CeilingSky.Height, (float)level.Header.CeilingSky.Offset.X, (float)level.Header.CeilingSky.Offset.Y);

        if (textures is not null)
        {
            var assembler = new SceneAssembler();
            assembler.AddLevel(level, Layers);
            // Things with a 3DO model render as geometry; the rest get markers.
            _markerThings = models is not null
                ? assembler.AddThings(level, models, Layers)
                : level.Things.Where(Layers.IsVisible).ToList();
            var scene = assembler.Build();
            _renderer.SetScene(scene, textures);
            _sceneMaterialNames = scene.Submeshes.Select(s => s.Material).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _renderer.SetMesh(SceneBuilder.FromLevel(level));
            _sceneMaterialNames.Clear();
            _markerThings = level.Things.ToList();
        }

        FrameLevel(level);
        _markerSize = System.Math.Max(0.02, _moveSpeed * 0.5);
        _loadingLevel = false;
        RefreshMarkers();
        RenderFrame();
    }

    private static readonly ColorF MarkerColor = new(0.2f, 0.9f, 1f);    // cyan things
    private static readonly ColorF VertexColor = new(1f, 0.5f, 0.1f);    // orange vertices
    private static readonly ColorF SelectColor = new(1f, 0.9f, 0.2f);    // yellow selection
    private static readonly ColorF LightColor = new(1f, 0.95f, 0.6f);    // warm white lights

    private void RefreshMarkers()
    {
        if (_renderer is null) return;
        var markers = new Mesh();
        foreach (var thing in _markerThings)
            markers.Append(SceneBuilder.BuildMarker(thing.Position, _markerSize, MarkerColor));

        // Lights only get markers in Light mode — a busy level has hundreds and
        // they would bury the geometry otherwise.
        if (_level is not null && Mode == EditMode.Light)
            foreach (var light in _level.Lights)
            {
                if (!Layers.IsVisible(light)) continue;
                markers.Append(SceneBuilder.BuildMarker(light.Position, _markerSize * 0.9, LightColor));
            }

        if (_activeSector is not null && Mode == EditMode.Vertex)
            foreach (var v in _activeSector.Vertices)
                markers.Append(SceneBuilder.BuildMarker(v.Position, _markerSize * 0.7, VertexColor));
        _renderer.SetMarkers(markers.IsEmpty ? null : markers);
    }

    /// <summary>Rebuilds the rendered geometry from the (possibly edited) level, reusing textures.</summary>
    private void RebuildScene()
    {
        if (_renderer is null || _level is null) return;
        if (_textures is null)
        {
            _renderer.SetMesh(SceneBuilder.FromLevel(_level, Layers.IsVisible));
            _markerThings = _level.Things.Where(Layers.IsVisible).ToList();
            return;
        }
        var assembler = new SceneAssembler();
        assembler.AddLevel(_level, Layers);
        _markerThings = _models is not null
            ? assembler.AddThings(_level, _models, Layers)
            : _level.Things.Where(Layers.IsVisible).ToList();
        var scene = assembler.Build();
        var materialNames = scene.Submeshes.Select(s => s.Material).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_sceneMaterialNames.IsSupersetOf(materialNames))
        {
            _renderer.SetScene(scene, _textures);
            _sceneMaterialNames = materialNames;
        }
        else _renderer.UpdateGeometry(scene);
    }

    private void OnHistoryChanged()
    {
        // An undo may have removed objects that are still selected.
        if (_level is not null) Selection.Prune(_level);
        RebuildScene();
        RefreshMarkers();
        UpdateSelectionHighlight();
        RenderFrame();
        NotifySelection();
    }

    /// <summary>
    /// Re-highlights whenever the shared selection changes — whether the change
    /// came from this view, the map view, or the consistency window. Suppressed
    /// while a level is loading, since the scene is still being rebuilt.
    /// </summary>
    private void OnSelectionChanged()
    {
        if (_loadingLevel) return;
        RefreshMarkers();
        UpdateSelectionHighlight();
        RenderFrame();
        NotifySelection();
    }

    private bool _loadingLevel;

    /// <summary>Rebuilds the scene when a layer is shown or hidden.</summary>
    private void OnLayersChanged()
    {
        if (_loadingLevel) return;
        RebuildScene();
        RefreshMarkers();
        UpdateSelectionHighlight();
        RenderFrame();
    }

    private Camera Cam() => new()
    {
        Position = _camPos, Yaw = _yaw, Pitch = _pitch,
        FieldOfViewDegrees = _fov, NearPlane = _near, FarPlane = _far,
    };

    private void FrameLevel(Level level)
    {
        var box = Box.Empty;
        foreach (var sector in level.Sectors)
            foreach (var v in sector.Vertices)
                box.Encapsulate(v.Position);
        if (box.Max.X < box.Min.X) { _camPos = new Vec3(0, -6, 0); _yaw = 0; _pitch = 0; return; }

        var center = box.Center;
        double radius = System.Math.Max(1.0, (box.Max - center).Length);
        _camPos = center + new Vec3(radius * 0.4, -radius * 1.3, radius * 0.5);
        var dir = (center - _camPos).Normalized();
        _pitch = System.Math.Asin(System.Math.Clamp(dir.Z, -1, 1));
        _yaw = System.Math.Atan2(dir.X, dir.Y);
        _moveSpeed = radius * 0.015;
        _far = radius * 6;
        _near = System.Math.Max(0.01, radius * 0.001);
    }

    private void RenderFrame()
    {
        if (_renderer is null || _lastSize.Width < 1 || _lastSize.Height < 1) return;
        uint w = (uint)_lastSize.Width, h = (uint)_lastSize.Height;
        var mvp = Cam().ViewProjection((double)w / h);
        var deg = 180.0 / System.Math.PI;
        var pixels = _renderer.Render(mvp, _camPos, new Vec3(_yaw * deg, _pitch * deg, 0), w, h);
        _bitmap = VulkanViewport.ToBitmap(pixels, (int)w, (int)h);
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var px = new PixelSize(
            (int)System.Math.Max(1, finalSize.Width * scale),
            (int)System.Math.Max(1, finalSize.Height * scale));
        if (px != _lastSize) { _lastSize = px; RenderFrame(); }
        return result;
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
            return;
        }
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x0d, 0x0d, 0x14)), new Rect(Bounds.Size));
        if (_error is not null)
        {
            var text = new FormattedText($"Vulkan unavailable:\n{_error}",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 14, Brushes.OrangeRed);
            context.DrawText(text, new Point(20, 20));
        }
    }

    // ---- keyboard fly movement ----

    /// <summary>
    /// The configurable command-key table (menu commands). When set, the menu
    /// chords below resolve through it so remapped shortcuts behave identically
    /// whether the view or the window has focus.
    /// </summary>
    public CommandKeys? Bindings { get; set; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;

        // Configurable menu commands — exact gesture match, handled before the
        // hardcoded chords below.
        if (Bindings is { } kb)
        {
            bool shift = mods.HasFlag(KeyModifiers.Shift);

            if (kb.Pressed(CommandKeys.Redo, e.Key, mods)) { History.Redo(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.Undo, e.Key, mods)) { History.Undo(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.Copy, e.Key, mods)) { CopySelection(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.Paste, e.Key, mods)) { PasteClipboard(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.Duplicate, e.Key, mods)) { DuplicateSelection(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.ConnectSectors, e.Key, mods)) { ConnectSelectedSectors(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.BridgeSurfaces, e.Key, mods)) { BridgeSelectedSurfaces(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.Extrude, e.Key, mods)) { ExtrudeSelectedSurface(1.0); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.ExtrudeInward, e.Key, mods)) { ExtrudeSelectedSurface(-1.0); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.FlipSurface, e.Key, mods)) { FlipSelectedSurface(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.CleaveSurface, e.Key, mods)) { CleaveSelectedSurface(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.MakeAdjoin, e.Key, mods)) { MakeAdjoinStep(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.RemoveAdjoin, e.Key, mods)) { RemoveSelectedAdjoin(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.RotateTextureCw, e.Key, mods)) { RotateSelectedTexture(15); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.RotateTextureCcw, e.Key, mods)) { RotateSelectedTexture(-15); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.AlignTextureNeighbour, e.Key, mods)) { AlignSelectedTextureToNeighbour(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.AutoFitTexture, e.Key, mods)) { AutoTextureSelected(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.ScaleTextureUp, e.Key, mods)) { ScaleSelectedTexture(1.0 / 0.9); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.ScaleTextureDown, e.Key, mods)) { ScaleSelectedTexture(0.9); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.CalculateLighting, e.Key, mods)) { CalculateLighting(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.CalculateLightingNoShadows, e.Key, mods)) { CalculateLighting(castShadows: false); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.SelectAllInSector, e.Key, mods)) { SelectActiveSectorSurfaces(); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.SelectNone, e.Key, mods)) { ClearSelection(); e.Handled = true; return; }

            // Texture shift arrows (Ctrl+arrows).
            const double texStep = 0.125;
            if (kb.Pressed(CommandKeys.ShiftTextureLeft, e.Key, mods)) { ShiftSelectedTexture(-texStep, 0); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.ShiftTextureRight, e.Key, mods)) { ShiftSelectedTexture(texStep, 0); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.ShiftTextureUp, e.Key, mods)) { ShiftSelectedTexture(0, -texStep); e.Handled = true; return; }
            if (kb.Pressed(CommandKeys.ShiftTextureDown, e.Key, mods)) { ShiftSelectedTexture(0, texStep); e.Handled = true; return; }
        }

        // Esc backs out of a pending adjoin first, then clears the selection.
        if (e.Key == Key.Escape)
        {
            if (_pendingAdjoin is not null) CancelPendingAdjoin();
            else ClearSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.B) { CycleBrightness(); e.Handled = true; return; }

        if (e.Key == Key.Insert)
        {
            if (Mode == EditMode.Light) CreateLight();
            else if (SelectedSurface is { } insertTarget) History.Do(new InsertSurfaceVertexCommand(insertTarget, 0));
            else CreateThing();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; return; }
        if (e.Key == Key.N) { CreateSector(); e.Handled = true; return; }
        if (e.Key == Key.OemOpenBrackets) { Rotate(-15); e.Handled = true; return; }
        if (e.Key == Key.OemCloseBrackets) { Rotate(15); e.Handled = true; return; }
        if (e.Key == Key.OemComma) { ScaleSelection(0.9); e.Handled = true; return; }
        if (e.Key == Key.OemPeriod) { ScaleSelection(1.0 / 0.9); e.Handled = true; return; }
        if (e.Key == Key.M) { CycleMaterial(); e.Handled = true; return; }
        if (e.Key == Key.OemSemicolon) { AdjustLight(-0.1f); e.Handled = true; return; }
        if (e.Key == Key.OemQuotes) { AdjustLight(0.1f); e.Handled = true; return; }

        if (HasSelection && TryMoveDelta(e.Key, out var delta))
        {
            MoveSelection(delta);
            e.Handled = true;
            return;
        }

        if (IsMoveKey(e.Key))
        {
            _pressed.Add(e.Key);
            if (!_moveTimer.IsEnabled) _moveTimer.Start();
            e.Handled = true;
        }
    }

    private float _brightness;

    /// <summary>Cycles editor brightness: real → medium → full → real.</summary>
    public void CycleBrightness()
    {
        _brightness = _brightness < 0.25f ? 0.6f : _brightness < 0.85f ? 1f : 0f;
        _renderer?.SetBrightness(_brightness);
        RenderFrame();
        var label = _brightness == 0 ? "real lighting" : _brightness >= 1f ? "full bright" : "medium";
        SelectionChanged?.Invoke($"Brightness: {label} (press B to cycle)");
    }

    private void CreateThing()
    {
        if (_level is null || _renderer is null) return;
        Thing t;
        if (SelectedThing is { } sel)
        {
            t = new Thing
            {
                Template = sel.Template, Name = sel.Name, Sector = sel.Sector,
                Position = sel.Position + new Vec3(_moveSpeed * 4, 0, 0),
                Pitch = sel.Pitch, Yaw = sel.Yaw, Roll = sel.Roll,
                Level = _level, Layer = sel.Layer, Flags = sel.Flags, BBox = sel.BBox,
                ArchLightingIndex = sel.ArchLightingIndex, ArchLightingSourceModel = sel.ArchLightingSourceModel,
                ArchLightingSourceModelHash = sel.ArchLightingSourceModelHash,
            };
            foreach (var (k, v) in sel.Values) t.Values[k] = v;
            t.SourceParameters.AddRange(sel.SourceParameters);
        }
        else
        {
            t = new Thing
            {
                Template = string.Empty, Name = "newthing",
                Position = _camPos + Cam().Forward * (_moveSpeed * 15),
                Sector = _activeSector ?? _level.Sectors.FirstOrDefault(),
            };
        }
        Selection.SelectOnly(t);
        History.Do(new CreateThingCommand(_level, t));
    }

    /// <summary>
    /// Adds a light in front of the camera, cloning the selected light's settings
    /// when there is one so a room can be lit consistently.
    /// </summary>
    public void CreateLight()
    {
        if (_level is null) return;

        var position = _camPos + Cam().Forward * (_moveSpeed * 15);
        var light = Selection.PrimaryLight is { } template
            ? new Light
            {
                Position = position,
                Range = template.Range,
                Intensity = template.Intensity,
                Color = template.Color,
                Flags = template.Flags,
                Layer = template.Layer,
            }
            : new Light
            {
                Position = position,
                Range = System.Math.Max(1.0, _moveSpeed * 40),
                Intensity = 1.0,
                Color = ColorF.White,
            };

        Selection.SelectOnly(light);
        History.Do(new CreateLightCommand(_level, light));
        SelectionChanged?.Invoke($"Created light #{light.Num} — F9 to re-bake lighting, Ctrl+Z to undo");
    }

    /// <summary>Creates a default box-room sector in front of the camera.</summary>
    public void CreateSector()
    {
        if (_level is null || _renderer is null) return;
        var sample = _level.Sectors.SelectMany(s => s.Surfaces).FirstOrDefault(s => !string.IsNullOrEmpty(s.Material));
        var center = _camPos + Cam().Forward * (_moveSpeed * 15);
        double size = System.Math.Max(0.5, _moveSpeed * 8);
        var sector = SectorFactory.CreateBox(_level, center, size, sample?.Material ?? string.Empty, sample?.MaterialIndex ?? 0);
        Selection.Clear();
        _activeSector = sector;
        History.Do(new CreateSectorCommand(_level, sector));
    }

    /// <summary>Deletes the sector of the current selection (surface/vertex) or the active sector.</summary>
    public void DeleteSector()
    {
        if (_level is null) return;
        var sec = SelectedSurface?.Sector ?? _activeSector;
        if (sec is null) { SelectionChanged?.Invoke("Select a surface first, then Delete Sector"); return; }
        Selection.Clear();
        _activeSector = null;
        History.Do(new DeleteSectorCommand(_level, sec));
    }

    /// <summary>
    /// Moves the whole selection by a delta as a single undo step: every selected
    /// thing, plus every vertex implied by the selected vertices/surfaces/sectors.
    /// </summary>
    public void MoveSelection(Vec3 delta)
    {
        var parts = new List<IEditCommand>();

        var verts = Selection.AffectedVertices();
        if (verts.Count > 0)
            parts.Add(new TransformVerticesCommand(verts, TransformVerticesCommand.Translate(delta),
                verts.Count == 1 ? "Move vertex" : $"Move {verts.Count} vertices"));

        foreach (var t in Selection.Things)
            parts.Add(new MoveThingCommand(t, delta));

        foreach (var l in Selection.Lights)
            parts.Add(new MoveLightCommand(l, delta));

        if (parts.Count == 0) return;
        History.Do(parts.Count == 1 ? parts[0] : new CompositeCommand(DescribeSelection("Move"), parts));
    }

    /// <summary>
    /// Rotates about Z: selected things spin on their own yaw, selected geometry
    /// turns about the selection centroid. Falls back to the active sector when
    /// nothing is explicitly selected.
    /// </summary>
    private void Rotate(double degrees)
    {
        var parts = new List<IEditCommand>();

        foreach (var t in Selection.Things)
            parts.Add(new RotateThingCommand(t, degrees));

        var verts = Selection.AffectedVertices();
        if (verts.Count == 0 && parts.Count == 0 && _activeSector is { } fallback && fallback.Vertices.Count > 0)
            verts = fallback.Vertices.ToList();

        if (verts.Count > 0)
            parts.Add(new TransformVerticesCommand(verts,
                TransformVerticesCommand.RotateZ(TransformVerticesCommand.Centroid(verts), degrees * System.Math.PI / 180.0),
                "Rotate geometry"));

        if (parts.Count == 0) return;
        History.Do(parts.Count == 1 ? parts[0] : new CompositeCommand(DescribeSelection("Rotate"), parts));
    }

    /// <summary>Scales the selected geometry about its centroid (active sector if nothing is selected).</summary>
    private void ScaleSelection(double factor)
    {
        var verts = Selection.AffectedVertices();
        if (verts.Count == 0 && _activeSector is { } fallback && fallback.Vertices.Count > 0)
            verts = fallback.Vertices.ToList();
        if (verts.Count == 0) return;

        History.Do(new TransformVerticesCommand(verts,
            TransformVerticesCommand.Scale(TransformVerticesCommand.Centroid(verts), factor), "Scale geometry"));
    }

    private string DescribeSelection(string verb) =>
        Selection.IsMultiple ? $"{verb} {Selection.Count} items" : verb;

    /// <summary>Assigns a material to every selected surface (no-op if none are selected).</summary>
    public bool SetSelectedSurfaceMaterial(string material, int index)
    {
        if (Selection.Surfaces.Count == 0) return false;

        var parts = Selection.Surfaces
            .Select(IEditCommand (s) => new SetMaterialCommand(s, material, index))
            .ToList();
        History.Do(parts.Count == 1 ? parts[0] : new CompositeCommand($"Set material on {parts.Count} surfaces", parts));

        SelectionChanged?.Invoke(parts.Count == 1
            ? $"Material → {material}"
            : $"Material → {material} on {parts.Count} surfaces");
        return true;
    }

    /// <summary>Cycles the primary surface's material, applying the result to the whole selection.</summary>
    private void CycleMaterial()
    {
        if (SelectedSurface is not { } s || Materials.Count == 0) return;
        int cur = Materials.FindIndex(m => string.Equals(m, s.Material, StringComparison.OrdinalIgnoreCase));
        int next = (cur + 1) % Materials.Count;
        if (SetSelectedSurfaceMaterial(Materials[next], next))
            SelectionChanged?.Invoke($"Material → {Materials[next]} (M to cycle)");
    }

    /// <summary>
    /// Bakes static lighting from the level's LIGHTS into per-vertex intensities.
    /// Runs over the selected sectors when there is a selection, otherwise the
    /// whole level. One undo step.
    /// </summary>
    public void CalculateLighting(bool castShadows = true)
    {
        if (_level is null) return;
        if (_level.Lights.Count == 0)
        {
            SelectionChanged?.Invoke("Calculate Lighting: this level has no lights in its LIGHTS section.");
            return;
        }

        // Scope to a selection when there is one — a full bake on a large level
        // with shadows is slow, and lighting one room at a time is the common case.
        var scope = SelectedSectors();
        var options = new LightingOptions { CastShadows = castShadows };
        var command = new CalculateLightingCommand(_level, scope, options);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        History.Do(command);
        clock.Stop();

        var stats = command.Stats;
        string where = scope is null ? "whole level" : $"{scope.Count} selected sector(s)";
        SelectionChanged?.Invoke(stats is null
            ? "Calculate Lighting: nothing to do."
            : $"Lit {where}: {stats.Vertices} vertices from {stats.Lights} lights" +
              $"{(castShadows ? $", {stats.Shadowed} shadowed" : ", no shadows")} " +
              $"in {clock.ElapsedMilliseconds} ms — Ctrl+Z to undo");
    }

    /// <summary>Sectors implied by the selection, or null when nothing is selected.</summary>
    private List<Sector>? SelectedSectors()
    {
        var seen = new HashSet<Sector>();
        var result = new List<Sector>();

        foreach (var s in Selection.Sectors)
            if (seen.Add(s)) result.Add(s);
        foreach (var s in Selection.Surfaces)
            if (seen.Add(s.Sector)) result.Add(s.Sector);
        foreach (var v in Selection.Vertices)
            if (v.Sector is { } sec && seen.Add(sec)) result.Add(sec);

        return result.Count > 0 ? result : null;
    }

    /// <summary>Brightens/darkens the selected vertex light, or the active sector's ambient.</summary>
    private void AdjustLight(float delta)
    {
        if (SelectedVertex is { } v && _activeSector is { } sec)
            History.Do(SetVertexLightCommand.Adjust(sec, v, delta));
        else if (_activeSector is { } s)
            History.Do(SetSectorAmbientCommand.Adjust(s, delta));
    }

    // ---- geometry operations (surface mode) ----

    /// <summary>The first surface of a pending two-step adjoin, if any.</summary>
    private Surface? _pendingAdjoin;

    private Surface? RequireSurface(string action)
    {
        if (SelectedSurface is { } s) return s;
        SelectionChanged?.Invoke($"{action}: select a surface first (Surface mode, click a face).");
        return null;
    }

    /// <summary>Extrudes the selected surface along its normal; negative pushes inward.</summary>
    public void ExtrudeSelectedSurface(double sign = 1.0)
    {
        if (RequireSurface("Extrude") is not { } s) return;
        double distance = System.Math.Max(0.05, _moveSpeed * 8) * sign;
        History.Do(new ExtrudeSurfaceCommand(s, distance));
        _activeSector = s.Sector;
        SelectionChanged?.Invoke($"Extruded surface {s.Num} by {distance:0.###} — Ctrl+Z to undo");
    }

    /// <summary>Reverses the selected surface's winding and normal.</summary>
    public void FlipSelectedSurface()
    {
        if (RequireSurface("Flip") is not { } s) return;
        History.Do(new FlipSurfaceCommand(s));
        SelectionChanged?.Invoke($"Flipped surface {s.Num} (normal reversed)");
    }

    /// <summary>
    /// Splits the selected surface in half with a plane through its centroid,
    /// perpendicular to the surface's longest in-plane axis (cuts the long way).
    /// </summary>
    public void CleaveSelectedSurface()
    {
        if (RequireSurface("Cleave") is not { } s) return;
        if (s.Corners.Count < 3) { SelectionChanged?.Invoke("Cleave: surface has too few vertices."); return; }

        var (normal, point) = GeometryOps.MidCleavePlane(s);

        int before = s.Sector.Surfaces.Count;
        var command = new CleaveSurfaceCommand(s, normal, point);
        History.Do(command);
        _activeSector = s.Sector;

        if (command.FailureReason is not null)
        {
            SelectionChanged?.Invoke("Cleave: " + command.FailureReason);
            return;
        }

        SelectionChanged?.Invoke(s.Sector.Surfaces.Count > before
            ? $"Cleaved surface {s.Num} into two — Ctrl+Z to undo"
            : "Cleave: the plane did not intersect the surface (no change).");
    }

    /// <summary>
    /// Two-step adjoin: the first call remembers the selected surface, the second
    /// joins it to the newly selected one as a mirror pair.
    /// </summary>
    public void MakeAdjoinStep()
    {
        if (RequireSurface("Adjoin") is not { } s) return;

        if (_pendingAdjoin is null || ReferenceEquals(_pendingAdjoin, s))
        {
            _pendingAdjoin = s;
            SelectionChanged?.Invoke(
                $"Adjoin 1/2: sector {s.Sector.Num} surface {s.Num} — now select the facing surface and repeat.");
            return;
        }

        var first = _pendingAdjoin;
        _pendingAdjoin = null;
        History.Do(new MakeAdjoinCommand(first, s));
        SelectionChanged?.Invoke(
            $"Adjoined sector {first.Sector.Num}/surface {first.Num} ↔ sector {s.Sector.Num}/surface {s.Num}");
    }

    /// <summary>
    /// Connects the two selected surfaces into a portal, trimming them to their
    /// shared region first. Needs exactly two surfaces selected — Ctrl+click the
    /// second one.
    /// </summary>
    public void BridgeSelectedSurfaces()
    {
        if (Selection.Surfaces.Count != 2)
        {
            SelectionChanged?.Invoke(
                $"Bridge: select exactly two facing surfaces (Ctrl+click the second) — {Selection.Surfaces.Count} selected.");
            return;
        }

        var a = Selection.Surfaces[0];
        var b = Selection.Surfaces[1];

        if (BridgeSurfacesCommand.Validate(a, b) is { } problem)
        {
            SelectionChanged?.Invoke($"Bridge: {problem}");
            return;
        }

        var command = new BridgeSurfacesCommand(a, b);
        History.Do(command);

        SelectionChanged?.Invoke(command.Failure is null
            ? $"Bridged sector {a.Sector.Num}/surface {a.Num} ↔ sector {b.Sector.Num}/surface {b.Num}"
            : $"Bridge: {command.Failure}");
    }

    /// <summary>
    /// Joins two overlapping sectors into a portalled set. Needs two sectors
    /// selected (Sector mode, Ctrl+click the second).
    /// </summary>
    public void ConnectSelectedSectors()
    {
        if (_level is null) return;

        // Selecting surfaces implies their sectors, which is how you usually
        // point at a room.
        var sectors = new List<Sector>();
        foreach (var sec in Selection.Sectors) if (!sectors.Contains(sec)) sectors.Add(sec);
        foreach (var surf in Selection.Surfaces) if (!sectors.Contains(surf.Sector)) sectors.Add(surf.Sector);

        if (sectors.Count != 2)
        {
            SelectionChanged?.Invoke(
                $"Connect Sectors: select exactly two overlapping sectors (Ctrl+click the second) — {sectors.Count} implied.");
            return;
        }

        if (ConnectSectorsCommand.Validate(sectors[0], sectors[1]) is { } problem)
        {
            SelectionChanged?.Invoke($"Connect Sectors: {problem}");
            return;
        }

        int before = _level.Sectors.Count;
        var command = new ConnectSectorsCommand(_level, sectors[0], sectors[1]);
        History.Do(command);

        Selection.Clear();
        _activeSector = sectors[0];

        SelectionChanged?.Invoke(command.Failure is null
            ? $"Connected sectors — {before} → {_level.Sectors.Count} sectors, {command.PortalsCreated} portal(s)"
            : $"Connect Sectors: {command.Failure}");
    }

    /// <summary>Clears the selected surface's adjoin (and its mirror).</summary>
    public void RemoveSelectedAdjoin()
    {
        if (RequireSurface("Remove adjoin") is not { } s) return;
        if (s.Adjoin is null) { SelectionChanged?.Invoke("Remove adjoin: this surface has no adjoin."); return; }
        History.Do(new RemoveAdjoinCommand(s));
        SelectionChanged?.Invoke($"Removed adjoin on sector {s.Sector.Num}/surface {s.Num}");
    }

    /// <summary>Cancels a pending adjoin pick.</summary>
    public void CancelPendingAdjoin()
    {
        if (_pendingAdjoin is null) return;
        _pendingAdjoin = null;
        SelectionChanged?.Invoke("Adjoin cancelled.");
    }

    /// <summary>Empties the shared selection (Esc, or Edit ▸ Select None).</summary>
    public void ClearSelection() => Selection.Clear();

    /// <summary>
    /// Moves the camera to look at a world point from a comfortable distance,
    /// keeping the current view direction so the jump doesn't disorient. Used by
    /// Find and the consistency window to reveal a result.
    /// </summary>
    public void JumpTo(Vec3 target, double distance = 0)
    {
        if (distance <= 0) distance = System.Math.Max(1.0, _moveSpeed * 25);

        var dir = Cam().Forward;
        if (dir.LengthSquared < 1e-9) dir = new Vec3(0, 1, 0);

        _camPos = target - dir.Normalized() * distance;
        RenderFrame();
    }

    /// <summary>Selects a found object and frames the camera on it.</summary>
    public void RevealFindResult(Sed.Core.Query.FindResult result)
    {
        using (Selection.Defer())
        {
            Selection.Clear();
            if (result.Surface is { } s) { Selection.Add(s); _activeSector = s.Sector; }
            else if (result.Thing is { } t) Selection.Add(t);
            else if (result.Light is { } l) Selection.Add(l);
            else if (result.Sector is { } sec) { Selection.Add(sec); _activeSector = sec; }
        }

        JumpTo(result.Position);
        SelectionChanged?.Invoke(result.Label);
    }

    /// <summary>Adds every result to the selection without moving the camera.</summary>
    public void SelectFindResults(IEnumerable<Sed.Core.Query.FindResult> results)
    {
        using (Selection.Defer())
        {
            Selection.Clear();
            foreach (var r in results)
            {
                if (r.Surface is { } s) { Selection.Add(s); _activeSector = s.Sector; }
                else if (r.Thing is { } t) Selection.Add(t);
                else if (r.Light is { } l) Selection.Add(l);
                else if (r.Sector is { } sec) { Selection.Add(sec); _activeSector = sec; }
            }
        }
    }

    // ---- clipboard ----

    private LevelFragment? _clipboard;

    /// <summary>Snapshots the selection into the clipboard (sectors and things).</summary>
    public void CopySelection()
    {
        if (_level is null) return;

        var fragment = LevelFragment.Capture(Selection, _level);
        if (fragment.IsEmpty)
        {
            SelectionChanged?.Invoke("Copy: select sectors, surfaces or things first.");
            return;
        }

        _clipboard = fragment;
        SelectionChanged?.Invoke(
            $"Copied {fragment.Sectors.Count} sector(s), {fragment.Things.Count} thing(s), " +
            $"{fragment.Lights.Count} light(s) — Ctrl+V to paste");
    }

    /// <summary>
    /// Pastes the clipboard beside the original — offset by the fragment's own
    /// width so a duplicated room lands next to its source rather than inside it —
    /// then selects what was pasted so it can be dragged into place immediately.
    /// </summary>
    public void PasteClipboard()
    {
        if (_level is null) return;
        if (_clipboard is not { } fragment || fragment.IsEmpty)
        {
            SelectionChanged?.Invoke("Paste: the clipboard is empty (Ctrl+C to copy a selection).");
            return;
        }

        if (fragment.CompatibilityError(_level) is { } error)
        {
            SelectionChanged?.Invoke(error);
            return;
        }
        var bounds = fragment.Bounds;
        double width = bounds.Max.X - bounds.Min.X;
        var offset = new Vec3(width > 1e-6 ? width * 1.1 : System.Math.Max(0.5, _moveSpeed * 8), 0, 0);

        var paste = new PasteFragmentCommand(_level, fragment, offset);
        History.Do(paste);

        using (Selection.Defer())
        {
            Selection.Clear();
            foreach (var s in paste.PastedSectors) Selection.Add(s);
            foreach (var t in paste.PastedThings) Selection.Add(t);
            foreach (var l in paste.PastedLights) Selection.Add(l);
        }
        _activeSector = paste.PastedSectors.FirstOrDefault() ?? _activeSector;

        SelectionChanged?.Invoke(
            $"Pasted {paste.PastedSectors.Count} sector(s), {paste.PastedThings.Count} thing(s), " +
            $"{paste.PastedLights.Count} light(s) — arrows to move, Ctrl+Z to undo");
    }

    /// <summary>Copy then paste in one step — duplicates the selection in place.</summary>
    public void DuplicateSelection()
    {
        CopySelection();
        if (_clipboard is { IsEmpty: false }) PasteClipboard();
    }

    /// <summary>Selects every surface of the active sector — a quick way to grab a room.</summary>
    public void SelectActiveSectorSurfaces()
    {
        if (_activeSector is not { } sec)
        {
            SelectionChanged?.Invoke("Select All in Sector: pick a surface first.");
            return;
        }

        using (Selection.Defer())
        {
            Selection.Clear();
            foreach (var s in sec.Surfaces) Selection.Add(s);
        }
    }

    // ---- texture mapping operations ----

    /// <summary>Texel size of the selected surface's material, defaulting to 64×64.</summary>
    private (int w, int h) SelectedTextureSize(Surface s)
    {
        var tex = _textures?.Invoke(s.Material);
        return tex is { } t && t.Width > 0 && t.Height > 0 ? (t.Width, t.Height) : (64, 64);
    }

    /// <summary>Shifts the selected surface's UVs by a fraction of the material size.</summary>
    public void ShiftSelectedTexture(double duFraction, double dvFraction)
    {
        if (RequireSurface("Shift texture") is not { } s) return;
        var (w, h) = SelectedTextureSize(s);
        History.Do(new ShiftTextureCommand(s, duFraction * w, dvFraction * h));
        SelectionChanged?.Invoke($"Shifted texture on surface {s.Num}");
    }

    /// <summary>Scales the selected surface's UVs about its first corner.</summary>
    public void ScaleSelectedTexture(double factor)
    {
        if (RequireSurface("Scale texture") is not { } s) return;
        History.Do(new ScaleTextureCommand(s, factor, factor));
        SelectionChanged?.Invoke($"Scaled texture on surface {s.Num} by {factor:0.##}");
    }

    /// <summary>Rotates the selected surface's UVs about its first corner.</summary>
    public void RotateSelectedTexture(double degrees)
    {
        if (RequireSurface("Rotate texture") is not { } s) return;
        History.Do(new RotateTextureCommand(s, degrees));
        SelectionChanged?.Invoke($"Rotated texture on surface {s.Num} by {degrees:0.#}°");
    }

    /// <summary>
    /// Aligns the second-selected surface's UVs to the first, so the texture runs
    /// continuously across the edge they share.
    /// </summary>
    public void AlignSelectedTextureToNeighbour()
    {
        if (Selection.Surfaces.Count != 2)
        {
            SelectionChanged?.Invoke(
                "Align texture: select the reference surface, then Ctrl+click the one to align.");
            return;
        }

        var reference = Selection.Surfaces[0];
        var target = Selection.Surfaces[1];

        if (AlignTextureToNeighbourCommand.Validate(target, reference) is { } problem)
        {
            SelectionChanged?.Invoke($"Align texture: {problem}");
            return;
        }

        History.Do(new AlignTextureToNeighbourCommand(target, reference));
        SelectionChanged?.Invoke(
            $"Aligned surface {target.Num} to surface {reference.Num} across their shared edge");
    }

    /// <summary>Auto-fits the selected surface's UVs to its material's texel extents.</summary>
    public void AutoTextureSelected()
    {
        if (RequireSurface("Auto-fit texture") is not { } s) return;
        var (w, h) = SelectedTextureSize(s);
        History.Do(new AutoTextureCommand(s, w, h));
        SelectionChanged?.Invoke($"Auto-fitted texture on surface {s.Num} ({w}×{h})");
    }

    /// <summary>Deletes every selected thing and vertex as one undo step.</summary>
    private void DeleteSelected()
    {
        if (_level is null) return;

        var parts = new List<IEditCommand>();

        foreach (var v in Selection.Vertices)
            if (v.Sector is { } owner)
                parts.Add(new DeleteVertexCommand(owner, v));

        foreach (var t in Selection.Things)
            parts.Add(new DeleteThingCommand(_level, t));

        foreach (var l in Selection.Lights)
            parts.Add(new DeleteLightCommand(_level, l));

        if (parts.Count == 0) return;

        Selection.Clear();
        History.Do(parts.Count == 1 ? parts[0] : new CompositeCommand($"Delete {parts.Count} items", parts));
    }

    private bool HasSelection => !Selection.IsEmpty;

    private bool TryMoveDelta(Key key, out Vec3 delta)
    {
        delta = Vec3.Zero;
        double step = System.Math.Max(_markerSize * 2, _moveSpeed);
        var cam = Cam();
        var fwd = new Vec3(cam.Forward.X, cam.Forward.Y, 0).Normalized();
        var right = new Vec3(cam.Right.X, cam.Right.Y, 0).Normalized();
        switch (key)
        {
            case Key.Right: delta = right * step; break;
            case Key.Left: delta = right * -step; break;
            case Key.Up: delta = fwd * step; break;
            case Key.Down: delta = fwd * -step; break;
            case Key.PageUp: delta = Camera.Up * step; break;
            case Key.PageDown: delta = Camera.Up * -step; break;
            default: return false;
        }
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _pressed.Remove(e.Key);
        if (_pressed.Count == 0) _moveTimer.Stop();
    }

    private static bool IsMoveKey(Key k) =>
        k is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E;

    private void MoveTick()
    {
        if (_pressed.Count == 0) { _moveTimer.Stop(); return; }
        var cam = Cam();
        var move = Vec3.Zero;
        if (_pressed.Contains(Key.W)) move += cam.Forward;
        if (_pressed.Contains(Key.S)) move -= cam.Forward;
        if (_pressed.Contains(Key.D)) move += cam.Right;
        if (_pressed.Contains(Key.A)) move -= cam.Right;
        if (_pressed.Contains(Key.E)) move += Camera.Up;
        if (_pressed.Contains(Key.Q)) move -= Camera.Up;

        if (move.LengthSquared > 0)
        {
            _camPos += move.Normalized() * _moveSpeed;
            RenderFrame();
        }
    }

    // ---- mouse look + picking ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        _lastPointer = e.GetPosition(this);
        _pressOrigin = _lastPointer.Value;
        _dragging = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_lastPointer is not { } last) return;
        var pos = e.GetPosition(this);
        double dx = pos.X - last.X, dy = pos.Y - last.Y;
        _lastPointer = pos;

        if (System.Math.Abs(pos.X - _pressOrigin.X) + System.Math.Abs(pos.Y - _pressOrigin.Y) > 3)
            _dragging = true;

        if (_dragging)
        {
            _yaw -= dx * 0.005;
            _pitch = System.Math.Clamp(_pitch - dy * 0.005, -1.5, 1.5);
            RenderFrame();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // Ctrl (or Cmd on macOS) extends the selection instead of replacing it.
        if (!_dragging)
            PickAt(e.GetPosition(this),
                e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        _lastPointer = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _camPos += Cam().Forward * (e.Delta.Y * _moveSpeed * 6);
        RenderFrame();
    }

    /// <summary>
    /// Picks whatever the active mode targets under the cursor.
    /// <paramref name="extend"/> (Ctrl/Cmd held) toggles the hit in the selection;
    /// otherwise the hit replaces the selection, and a miss clears it.
    /// </summary>
    private void PickAt(Point p, bool extend)
    {
        if (_renderer is null || _level is null || _lastSize.Width < 1) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var ray = Picker.ScreenPointToRay(Cam(), p.X * scale, p.Y * scale, _lastSize.Width, _lastSize.Height);

        using (Selection.Defer())
        {
            switch (Mode)
            {
                case EditMode.Thing:
                    {
                        var hit = Picker.PickThing(_level, ray, _markerSize * 1.8, Layers);
                        if (hit is null) { if (!extend) Selection.Clear(); break; }
                        if (extend) Selection.Toggle(hit.Thing);
                        else Selection.SelectOnly(hit.Thing);
                        break;
                    }
                case EditMode.Vertex:
                    {
                        var hit = Picker.PickVertex(_level, ray, _markerSize, Layers);
                        if (hit is null) { if (!extend) Selection.Clear(); break; }
                        _activeSector = hit.Sector;
                        if (extend) Selection.Toggle(hit.Vertex);
                        else Selection.SelectOnly(hit.Vertex);
                        break;
                    }
                case EditMode.Light:
                    {
                        var hit = Picker.PickLight(_level, ray, _markerSize * 1.8, Layers);
                        if (hit is null) { if (!extend) Selection.Clear(); break; }
                        if (extend) Selection.Toggle(hit.Light);
                        else Selection.SelectOnly(hit.Light);
                        break;
                    }
                case EditMode.Sector:
                case EditMode.Surface:
                default:
                    {
                        var sHit = Picker.Pick(_level, ray, Layers);
                        if (sHit is null) { if (!extend) Selection.Clear(); break; }
                        _activeSector = sHit.Sector;
                        if (extend) Selection.Toggle(sHit.Surface);
                        else Selection.SelectOnly(sHit.Surface);
                        break;
                    }
            }
        }
        // Mutating Selection raises Changed → OnSelectionChanged re-highlights.
    }

    private void NotifySelection()
    {
        SelectionChanged?.Invoke(SelectionText());
        SelectionFromExternal?.Invoke(SelectedVertex, SelectedThing, SelectedSurface, _activeSector);
    }

    /// <summary>Replaces the selection from outside this view (map pick, jump-to).</summary>
    public void SetExternalSelection(Vertex? v, Thing? t, Surface? s)
    {
        if (v?.Sector is { } vs) _activeSector = vs;
        else if (s is not null) _activeSector = s.Sector;

        using (Selection.Defer())
        {
            Selection.Clear();
            if (v is not null) Selection.Add(v);
            if (t is not null) Selection.Add(t);
            if (s is not null) Selection.Add(s);
        }
    }

    private void UpdateSelectionHighlight()
    {
        if (_renderer is null) return;

        double t = _markerSize * 0.08;
        var sel = new Mesh();

        foreach (var v in Selection.Vertices)
            sel.Append(SceneBuilder.BuildMarker(v.Position, _markerSize * 1.1, SelectColor));
        foreach (var th in Selection.Things)
            sel.Append(SceneBuilder.BuildMarker(th.Position, _markerSize * 1.3, SelectColor));
        foreach (var li in Selection.Lights)
            sel.Append(SceneBuilder.BuildMarker(li.Position, _markerSize * 1.3, SelectColor));
        foreach (var s in Selection.Surfaces)
            sel.Append(SceneBuilder.BuildEdgeHighlight(s, t, SelectColor, VertexColor));
        foreach (var sec in Selection.Sectors)
            sel.Append(SceneBuilder.BuildSectorEdgeHighlight(sec, t, SelectColor, VertexColor));

        if (sel.IsEmpty && _activeSector is not null && Mode == EditMode.Sector)
            sel.Append(SceneBuilder.BuildSectorEdgeHighlight(_activeSector, t, SelectColor, VertexColor));

        _renderer.SetSelection(sel.IsEmpty ? null : sel);
    }

    private string SelectionText()
    {
        if (Selection.IsMultiple)
        {
            var parts = new List<string>();
            if (Selection.Surfaces.Count > 0) parts.Add($"{Selection.Surfaces.Count} surfaces");
            if (Selection.Vertices.Count > 0) parts.Add($"{Selection.Vertices.Count} vertices");
            if (Selection.Things.Count > 0) parts.Add($"{Selection.Things.Count} things");
            if (Selection.Sectors.Count > 0) parts.Add($"{Selection.Sectors.Count} sectors");
            if (Selection.Lights.Count > 0) parts.Add($"{Selection.Lights.Count} lights");
            return $"Selected {string.Join(", ", parts)} — arrows move all, [ ] rotate, , . scale, Ctrl+click to add/remove";
        }

        if (SelectedVertex is { } v)
            return $"Vertex @ {v.Position}  — arrows/PgUp-PgDn move, Ctrl+click to add to selection, Ctrl+Z undo";
        if (SelectedThing is { } t)
        {
            var name = t.Name.Length > 0 ? t.Name : "?";
            return $"Thing #{t.Num} '{name}' @ {t.Position}  — arrows/PgUp-PgDn to move, Ctrl+click to add, Ctrl+Z undo";
        }
        if (Selection.PrimaryLight is { } li)
            return $"Light #{li.Num} @ {li.Position} — range {li.Range:0.##}, intensity {li.Intensity:0.##}" +
                   "  (arrows move, Delete removes, Insert adds another)";
        if (SelectedSurface is { } s)
        {
            var mat = string.IsNullOrEmpty(s.Material) ? "(no material)" : s.Material;
            return $"Sector {s.Sector.Num}, surface {s.Num} — {mat}  (Ctrl+click to add to selection; arrows move it)";
        }
        return "Nothing selected";
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _moveTimer.Stop();
        _renderer?.Dispose();
        _device?.Dispose();
        _ctx?.Dispose();
    }
}
