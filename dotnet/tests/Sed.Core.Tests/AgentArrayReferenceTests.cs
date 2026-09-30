using System.Text;
using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Xunit;

namespace Sed.Core.Tests;

public sealed class AgentArrayReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sed-array-reference-" + Guid.NewGuid().ToString("N"));
    public AgentArrayReferenceTests() => Directory.CreateDirectory(_root);

    private (string input, string archive) Fixture(string reference)
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.Templates.Add("fixture", new Template { Name = "fixture", Parent = "none" });
        var thing = level.NewThing(); thing.Template = "fixture"; thing.Name = "existing"; thing.Sector = level.Sectors[0];
        var cog = new Cog { Name = "original.cog" }; cog.Values.Add(reference); cog.Values.Add("1"); level.Cogs.Add(cog);
        string input = Path.Combine(_root, "input.jkl"), archive = Path.Combine(_root, "original.goo");
        File.WriteAllText(input, JklWriter.Build(new JklDocument(level)));
        const string script = "symbols\nthing target\nint unrelated\nmessage startup\nend\ncode\nstartup:\nreturn;\nend\n";
        GobWriter.Build(archive, [("cog/original.cog", Encoding.ASCII.GetBytes(script))]);
        return (input, archive);
    }
    private static string Patch(string input, string? archive)
    {
        var request = new Dictionary<string, object> {
            ["schemaVersion"] = 1, ["inputSha256"] = AgentWorkspace.Hash(File.ReadAllBytes(input)),
            ["operations"] = new object[] { new { op = "addThingArray", template = "fixture", namePrefix = "new",
                layout = new { kind = "linear", origin = new[] { .2, 0, 0 }, step = new[] { .2, 0, 0 }, count = 2 } } }
        };
        if (archive is not null) request["resourceArchives"] = new[] { new { path = archive, sha256 = AgentWorkspace.Hash(File.ReadAllBytes(archive)) } };
        return JsonSerializer.Serialize(request);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    public void ArrayCannotCaptureExistingTypedFutureThingReference(string reference)
    {
        var (input, archive) = Fixture(reference);
        byte[] original = File.ReadAllBytes(input);
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(input, Patch(input, archive)));
        Assert.Contains("outside", error.Message);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.False(File.Exists(Path.Combine(_root, "output.jkl")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ExactScriptsPermitExistingOrNullReferencesWithoutGuessingScalarValues(string reference)
    {
        var (input, archive) = Fixture(reference);
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(input, Patch(input, null)));
        var plan = AgentWorkspace.Plan(input, Patch(input, archive));
        string output = Path.Combine(_root, "output.jkl");
        AgentWorkspace.Commit(plan, output);
        var reopened = AgentWorkspace.Load(output).Document;
        Assert.Equal(3, reopened.Level.Things.Count);
        Assert.Equal(new[] { reference, "1" }, Assert.Single(reopened.Level.Cogs).Values);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
