using Avalonia.Controls;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.App;

public static class VertexInspector
{
    public static Control Build(Vertex vertex, EditHistory history)
    {
        var p = InspectorPanel.Panel("Vertex");

        p.Children.Add(InspectorPanel.Row("X", InspectorPanel.NumericField(
            vertex.Position.X,
            v => history.Do(new SetVertexPositionCommand(vertex, new Vec3(v, vertex.Position.Y, vertex.Position.Z))))));

        p.Children.Add(InspectorPanel.Row("Y", InspectorPanel.NumericField(
            vertex.Position.Y,
            v => history.Do(new SetVertexPositionCommand(vertex, new Vec3(vertex.Position.X, v, vertex.Position.Z))))));

        p.Children.Add(InspectorPanel.Row("Z", InspectorPanel.NumericField(
            vertex.Position.Z,
            v => history.Do(new SetVertexPositionCommand(vertex, new Vec3(vertex.Position.X, vertex.Position.Y, v))))));

        p.Children.Add(InspectorPanel.Row("Sector", new TextBlock
        {
            Text = vertex.Sector?.Num.ToString() ?? "?",
            FontSize = 11,
        }));

        if (vertex.Sector is { } sector && sector.Surfaces.SelectMany(s => s.Corners).FirstOrDefault(c => c.Vertex == vertex) is { } corner)
        {
            var components = sector.Level.Kind == ProjectType.JediKnight
                ? new[] { VertexLightComponent.Mono } : Enum.GetValues<VertexLightComponent>();
            foreach (var component in components)
            {
                float value = component switch { VertexLightComponent.Mono => corner.MonoIntensity ?? corner.Intensity.R,
                    VertexLightComponent.Red => corner.Intensity.R, VertexLightComponent.Green => corner.Intensity.G, _ => corner.Intensity.B };
                p.Children.Add(InspectorPanel.Row(component + " light", InspectorPanel.NumericField(value, v =>
                {
                    if (double.IsFinite(v) && v is >= 0 and <= 1000)
                        history.Do(new SetVertexLightComponentCommand(sector, vertex, component, (float)v));
                })));
            }
        }

        return p;
    }
}
