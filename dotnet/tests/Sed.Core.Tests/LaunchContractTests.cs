using System.Text;
using System.Text.Json;
using Sed.App;
using Sed.Core.Model;
using Sed.Formats.Game;
using Sed.Formats.Gob;
using Sed.Formats.Launching;

namespace Sed.Core.Tests;

public sealed class LaunchContractTests
{
    private sealed class Files : ILaunchFileSystem
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoriesExist = true;
        public string? ShortName;
        public bool DirectoryExists(string path) => DirectoriesExist;
        public bool FileExists(string path) => Existing.Contains(Path.GetFullPath(path));
        public string? ShortPath(string path) => ShortName;
        public Files Add(string directory, params string[] names)
        {
            foreach (string name in names) Existing.Add(Path.GetFullPath(Path.Combine(directory, name)));
            return this;
        }
    }
    private static readonly string Game = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sed-launch-contract", "game"));
    private static LaunchRequest Request(GameEngineProfile engine = GameEngineProfile.Auto, string? executable = null,
        ProjectType game = ProjectType.MysteriesOfTheSith) => new(game, Game, Path.Combine(Game, "project"), "test.jkl", executable, engine);
    private static Files OpenJkFiles(string? game = null) => new Files().Add(game ?? Game,
        "openjkdf2-64.exe", "jkm.exe", Path.Combine("resource", "jk_.cd"));
    private static GameLaunchPlan PrepareWindowsLaunch(LaunchRequest request, ILaunchFileSystem fileSystem) =>
        GameLauncher.PrepareLaunch(request, fileSystem, GameLaunchHost.Windows);

    [Fact]
    public void AutoPrefersOpenJkAndUsesMotsCompatAndLooseFiles()
    {
        var plan = PrepareWindowsLaunch(Request(), OpenJkFiles());
        Assert.Equal(GameEngineProfile.OpenJKDF2, plan.Engine);
        Assert.EndsWith("openjkdf2-64.exe", plan.Executable);
        Assert.Equal(new[] { "-devmode", "-debug", "log", "-motsCompat", "-path", "project" }, plan.Arguments);
        Assert.Equal(new[] { "episode.jk", "jkl\\test.jkl" }, plan.RequiredLooseFiles);
        Assert.DoesNotContain("-map", plan.Arguments);
        Assert.DoesNotContain("-episode", plan.Arguments);
        Assert.Equal(Game, plan.WorkingDirectory);
    }

    [Fact]
    public void JkOpenJkDoesNotEnableMots()
    {
        var plan = PrepareWindowsLaunch(Request(game: ProjectType.JediKnight), OpenJkFiles());
        Assert.DoesNotContain("-motsCompat", plan.Arguments);
    }

    [Fact]
    public void RetailProfileIsStrictAndDoesNotNeedOpenJkMarker()
    {
        var files = new Files().Add(Game, "jkm.exe", "openjkdf2-64.exe");
        var plan = PrepareWindowsLaunch(Request(GameEngineProfile.Retail), files);
        Assert.Equal(GameEngineProfile.Retail, plan.Engine);
        Assert.EndsWith("jkm.exe", plan.Executable);
        Assert.DoesNotContain("-motsCompat", plan.Arguments);
    }

    [Fact]
    public void AutoCanFallbackToRetail()
    {
        var plan = PrepareWindowsLaunch(Request(), new Files().Add(Game, "jkm.exe"));
        Assert.Equal(GameEngineProfile.Retail, plan.Engine);
    }

    [Theory]
    [InlineData(GameEngineProfile.OpenJKDF2, "jkm.exe")]
    [InlineData(GameEngineProfile.Retail, "openjkdf2-64.exe")]
    public void ExplicitProfileNeverFallsBack(GameEngineProfile profile, string onlyExecutable)
        => Assert.Throws<ArgumentException>(() => PrepareWindowsLaunch(Request(profile), new Files().Add(Game, onlyExecutable)));

    [Fact]
    public void CustomExecutableRequiresKnownEngineAndWorksWhenExplicit()
    {
        var files = new Files().Add(Game, "custom.exe", Path.Combine("resource", "jk_.cd"));
        Assert.Throws<ArgumentException>(() => PrepareWindowsLaunch(Request(executable: "custom.exe"), files));
        var plan = PrepareWindowsLaunch(Request(GameEngineProfile.OpenJKDF2, "custom.exe"), files);
        Assert.EndsWith("custom.exe", plan.Executable);
        Assert.Contains("-motsCompat", plan.Arguments);
    }

    [Theory]
    [InlineData("\"jkm.exe\"")]
    [InlineData("jkm.exe -devmode")]
    [InlineData("test.cmd")]
    public void ExecutableFieldRejectsCommands(string executable)
        => Assert.Throws<ArgumentException>(() => PrepareWindowsLaunch(Request(executable: executable), OpenJkFiles()));

    [Fact]
    public void OpenJkRequiresMarkerSoCwdCannotBeSilentlyRerouted()
        => Assert.Contains("jk_.cd", Assert.Throws<ArgumentException>(() => PrepareWindowsLaunch(Request(),
            new Files().Add(Game, "openjkdf2-64.exe"))).Message);

    [Fact]
    public void RelativeProjectWorksUnderSpaceBearingInstall()
    {
        string game = Path.Combine(Game, "game with spaces");
        var plan = PrepareWindowsLaunch(Request() with { GameDirectory = game, ProjectDirectory = Path.Combine(game, "project") }, OpenJkFiles(game));
        Assert.Equal("project", plan.ProjectPathToken);
        Assert.Contains("game with spaces", plan.Executable);
    }

    [Fact]
    public void SameFolderUsesDot()
        => Assert.Equal(".", PrepareWindowsLaunch(Request() with { ProjectDirectory = Game }, OpenJkFiles()).ProjectPathToken);

    [Fact]
    public void ResourceSuffixReservesExact127ByteBoundary()
    {
        string resource = "jkl\\test.jkl";
        int tokenLength = GameLauncher.MaximumResourcePathBytes - resource.Length - 1;
        string token = new('a', tokenLength);
        var request = Request() with { ProjectDirectory = Path.Combine(Game, token) };
        Assert.Equal(token, PrepareWindowsLaunch(request, OpenJkFiles()).ProjectPathToken);
        Assert.Throws<ArgumentException>(() => PrepareWindowsLaunch(request with { ProjectDirectory = Path.Combine(Game, token + "a") }, OpenJkFiles()));
    }

    [Fact]
    public void ShortNameCanRescueSpaceBearingProject()
    {
        var files = OpenJkFiles();
        files.ShortName = "C:\\TEST~1";
        var plan = PrepareWindowsLaunch(Request() with { ProjectDirectory = Path.Combine(Game, "test with spaces") }, files);
        Assert.Equal(files.ShortName, plan.ProjectPathToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("space here")]
    [InlineData("café")]
    [InlineData("quote\"here")]
    [InlineData("tab\there")]
    public void EnginePathTokenRejectsUnparseableText(string value) => Assert.False(GameLauncher.IsEnginePathToken(value));

    [Fact]
    public void ShellMetacharactersRemainLiteralArguments()
    {
        const string token = "test&%!(x)";
        var plan = PrepareWindowsLaunch(Request() with { ProjectDirectory = Path.Combine(Game, token) }, OpenJkFiles());
        Assert.Equal(token, plan.Arguments.Last());
        Assert.DoesNotContain("cmd.exe", JsonSerializer.Serialize(plan));
    }

    [Fact]
    public void MissingDirectoriesAreReportedWithoutWrites()
        => Assert.Throws<ArgumentException>(() => PrepareWindowsLaunch(Request(), new Files { DirectoriesExist = false }));

    [Fact]
    public void PlanAndPerGameProfilesRoundTripAsJson()
    {
        var plan = PrepareWindowsLaunch(Request(), OpenJkFiles());
        var restored = JsonSerializer.Deserialize<GameLaunchPlan>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(plan.Arguments, restored.Arguments);
        var settings = new AppSettings { JediKnightTestExecutable = "jk.exe", MysteriesTestExecutable = "mine.exe",
            JediKnightTestEngine = GameEngineProfile.Retail, MysteriesTestEngine = GameEngineProfile.OpenJKDF2 };
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("mine.exe", copy.TestExecutableFor(ProjectType.MysteriesOfTheSith));
        Assert.Equal(GameEngineProfile.Retail, copy.TestEngineFor(ProjectType.JediKnight));
        Assert.Equal(GameEngineProfile.Auto, JsonSerializer.Deserialize<AppSettings>("{}")!.MysteriesTestEngine);
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, ".gob")]
    [InlineData(ProjectType.MysteriesOfTheSith, ".goo")]
    public void TestArchiveContainsEpisodeAndReferencedLevel(ProjectType game, string extension)
    {
        var package = TestEpisode.Build(game, "demo.jkl", "SECTION: HEADER\nVERSION 3\n");
        Assert.Equal(extension, package.ArchiveExtension);
        var episode = EpisodeFile.Parse(package.EpisodeText);
        Assert.Equal("demo.jkl", Assert.Single(episode.Sequences).File);
        string directory = Path.Combine(Path.GetTempPath(), "sed-package-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "test" + extension);
        try
        {
            GobWriter.Build(output, package.Entries.Select(e => (e.Name, e.Data)));
            using var archive = GobArchive.Open(output);
            Assert.Contains(archive.Entries, e => e.NormalizedName == "episode.jk");
            Assert.Contains(archive.Entries, e => e.NormalizedName == "jkl/demo.jkl");
            Assert.Equal(package.EpisodeText, archive.ReadText(archive.Entries.Single(e => e.NormalizedName == "episode.jk")));
        }
        finally { File.Delete(output); Directory.Delete(directory); }
    }

    [Fact]
    public void ExistingEpisodeIsPreservedOrRejectedWithoutReplacement()
    {
        string existing = TestEpisode.Build(ProjectType.MysteriesOfTheSith, "demo.jkl", "").EpisodeText + "\n# Preserve this comment\n";
        Assert.Equal(existing, TestEpisode.Build(ProjectType.MysteriesOfTheSith, "demo.jkl", "", existing).EpisodeText);
        Assert.Throws<InvalidOperationException>(() => TestEpisode.Build(ProjectType.MysteriesOfTheSith, "other.jkl", "", existing));
    }

    [Fact]
    public void LossyAsciiConversionIsRejected()
        => Assert.Throws<EncoderFallbackException>(() => TestEpisode.Build(ProjectType.MysteriesOfTheSith, "demo.jkl", "café.mat"));

    [Theory]
    [InlineData("../escape.jkl")]
    [InlineData("space here.jkl")]
    [InlineData("bad\".jkl")]
    [InlineData("abcdefghijklmnopqrstuvwxyz12.jkl")]
    public void EpisodeRejectsUnsafeOrOversizedLevelName(string name)
        => Assert.Throws<ArgumentException>(() => TestEpisode.Build(ProjectType.JediKnight, name, ""));

    [Fact]
    public void SafeNamesFitEpisodeFilenameBuffer()
    {
        string name = TestEpisode.SafeLevelName("long café level " + new string('x', 50));
        Assert.Equal(27, name.Length);
        TestEpisode.ValidateLevelFileName(name + ".jkl");
        Assert.Equal("level", TestEpisode.SafeLevelName(""));
    }
}
