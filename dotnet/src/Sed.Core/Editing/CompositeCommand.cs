namespace Sed.Core.Editing;

/// <summary>
/// Groups several commands into one undo step — moving a multi-selection of five
/// things should be one Ctrl+Z, not five. Applies in order and reverts in
/// reverse order, so commands that depend on each other unwind correctly.
/// </summary>
public sealed class CompositeCommand : IEditCommand
{
    private readonly IEditCommand[] _commands;

    public CompositeCommand(string name, IEnumerable<IEditCommand> commands)
    {
        Name = name;
        _commands = commands.ToArray();
    }

    public string Name { get; }

    /// <summary>Number of sub-commands; zero means applying is a no-op.</summary>
    public int Count => _commands.Length;

    public void Apply()
    {
        int applied = 0;
        try { for (; applied < _commands.Length; applied++) _commands[applied].Apply(); }
        catch
        {
            // Each child must make its own failed Apply atomic. Roll back the
            // children that completed so a rejected group leaves no partial edit.
            for (int i = applied - 1; i >= 0; i--) _commands[i].Revert();
            throw;
        }
    }

    public void Revert()
    {
        int index = _commands.Length - 1;
        try { for (; index >= 0; index--) _commands[index].Revert(); }
        catch
        {
            for (int i = index + 1; i < _commands.Length; i++) _commands[i].Apply();
            throw;
        }
    }
}
