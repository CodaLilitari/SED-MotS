using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class PlacementArrayCapacityFormatTests
{
    [Theory]
    [InlineData(ProjectType.JediKnight)]
    [InlineData(ProjectType.MysteriesOfTheSith)]
    public void EmptyScaffoldRetainsDeclaredThingCapacityWhenSavedAndReopened(ProjectType game)
    {
        var level = LevelFactory.CreateUntexturedRoom(game);
        Assert.Empty(level.Things); Assert.Equal(256, level.ThingCapacity);
        string text = JklWriter.Build(new JklDocument(level));
        Assert.Contains("World things 256", text);
        var reopened = JklParser.ParseDocument(text);
        Assert.Empty(reopened.Level.Things); Assert.Equal(256, reopened.Level.ThingCapacity);
        string second = JklWriter.Build(reopened);
        Assert.Equal(text, second); Assert.Equal(256, JklParser.ParseDocument(second).Level.ThingCapacity);
    }

    [Fact]
    public void EmptyZeroCapacityDocumentDoesNotAcquireAnUnrequestedThingSection()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.ThingCapacity = 0;
        string text = JklWriter.Build(new JklDocument(level));
        Assert.DoesNotContain("SECTION: THINGS", text);
        Assert.Equal(0, JklParser.ParseDocument(text).Level.ThingCapacity);
    }
}
