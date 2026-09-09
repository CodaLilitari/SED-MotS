using Sed.Core.Editing;
using Sed.Core.Math;

namespace Sed.Core.Model;

public static class LevelFactory
{
    /// <summary>An editable empty room, shared by the GUI and headless agent.</summary>
    public static Level CreateUntexturedRoom(ProjectType game)
    {
        if (game is not (ProjectType.JediKnight or ProjectType.MysteriesOfTheSith))
            throw new ArgumentException("New-room creation supports JK and MotS.", nameof(game));
        var level = new Level { Kind = game, PixelsPerUnit = 320, ThingCapacity = 256 };
        level.Header.Version = 1; // Both games use JKL version 1.
        level.Header.Gravity = 4;
        new CreateSectorCommand(level, SectorFactory.CreateBox(level, Vec3.Zero, 1, string.Empty, -1)).Apply();
        return level;
    }
}
