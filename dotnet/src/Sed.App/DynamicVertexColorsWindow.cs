using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Sed.Formats.Jkl;

namespace Sed.App;

/// <summary>Read-only candidate-output report; the document provider follows the active editor document.</summary>
public sealed class DynamicVertexColorsWindow : Window
{
    private readonly Func<JklDocument?> _document;
    private readonly TextBlock _summary = new() { Margin = new(10), TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new() { Name = "VertexColorRows", ItemTemplate = new FuncDataTemplate<string>((row, _) =>
        new TextBlock { Text = row, TextWrapping = TextWrapping.Wrap, Margin = new(6) }) };

    public DynamicVertexColorsWindow(Func<JklDocument?> document)
    {
        _document = document;
        Title = "MotS Dynamic Vertex Colors";
        Width = 850; Height = 540;
        var recheck = new Button { Name = "RecheckVertexColors", Content = "Re-check proposed output", Margin = new(10) };
        recheck.Click += (_, _) => Run();
        var panel = new DockPanel();
        DockPanel.SetDock(_summary, Dock.Top); panel.Children.Add(_summary);
        DockPanel.SetDock(recheck, Dock.Bottom); panel.Children.Add(recheck);
        panel.Children.Add(_list); Content = panel;
        Run();
    }

    public void Run()
    {
        var document = _document();
        if (document is null)
        { _summary.Text = "No active level document. Open a MotS level and re-check."; _list.ItemsSource = Array.Empty<string>(); return; }
        var result = JklDynamicVertexColors.Evaluate(document);
        _summary.Text = $"{result.Status}; geometry valid: {result.GeometryValid?.ToString() ?? "unverified"}. " +
            $"{result.Surfaces.Count} surfaces; retail compacted: {result.Surfaces.Count(s => s.Retail == "Compacted")}; " +
            $"retail retained: {result.Surfaces.Count(s => s.Retail == "Retained")}; retail unknown: {result.Surfaces.Count(s => s.Retail == "Unknown")}.\n" +
            $"Proposed UTF-8 output SHA256: {result.ProposedOutputSha256 ?? "unavailable"}\n{result.Detail}";
        _list.ItemsSource = result.Surfaces.Select(s =>
            $"Sector {s.SectorIndex}, surface {s.SurfaceIndex} (global {s.GlobalSurfaceIndex}), {s.Layout}, {s.CornerCount} corners\n" +
            $"Retail: {s.Retail}; OpenMotS: {s.OpenMots}; geometry mode {s.GeometryMode}, lighting mode {s.LightingMode}, adjoin {s.Adjoined}\n{s.Reason}").ToArray();
    }
}
