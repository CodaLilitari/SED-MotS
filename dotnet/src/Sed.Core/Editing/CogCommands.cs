using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Sets one positional value of a placed COG. Values are positional in the JKL,
/// so the list is padded rather than left short if an index beyond the end is
/// written — a gap would silently shift every later symbol's meaning.
/// </summary>
public sealed class SetCogValueCommand : IEditCommand
{
    private readonly Cog _cog;
    private readonly int _index;
    private readonly string _new;
    private readonly List<string> _old;

    public SetCogValueCommand(Cog cog, int index, string value)
    {
        _cog = cog;
        _index = index;
        _new = value;
        _old = cog.Values.ToList();
    }

    public string Name => "Set COG value";

    public void Apply()
    {
        while (_cog.Values.Count <= _index) _cog.Values.Add("0");
        _cog.Values[_index] = _new;
    }

    public void Revert()
    {
        _cog.Values.Clear();
        _cog.Values.AddRange(_old);
    }
}

/// <summary>Replaces a placed COG's whole value list (used when its script changes).</summary>
public sealed class SetCogValuesCommand : IEditCommand
{
    private readonly Cog _cog;
    private readonly List<string> _new;
    private readonly List<string> _old;

    public SetCogValuesCommand(Cog cog, IEnumerable<string> values)
    {
        _cog = cog;
        _new = values.ToList();
        _old = cog.Values.ToList();
    }

    public string Name => "Set COG values";

    public void Apply() { _cog.Values.Clear(); _cog.Values.AddRange(_new); }
    public void Revert() { _cog.Values.Clear(); _cog.Values.AddRange(_old); }
}

/// <summary>
/// Points a placed COG at a different script. The value list belongs to the old
/// script's symbol layout, so it is replaced wholesale rather than reinterpreted
/// against symbols it was never written for.
/// </summary>
public sealed class SetCogScriptCommand : IEditCommand
{
    private readonly Cog _cog;
    private readonly string _newName, _oldName;
    private readonly List<string> _newValues, _oldValues;

    public SetCogScriptCommand(Cog cog, string scriptName, IEnumerable<string>? values = null)
    {
        _cog = cog;
        _newName = scriptName;
        _oldName = cog.Name;
        _newValues = values?.ToList() ?? new List<string>();
        _oldValues = cog.Values.ToList();
    }

    public string Name => $"Set COG script {_newName}";

    public void Apply()
    {
        _cog.Name = _newName;
        _cog.Values.Clear();
        _cog.Values.AddRange(_newValues);
    }

    public void Revert()
    {
        _cog.Name = _oldName;
        _cog.Values.Clear();
        _cog.Values.AddRange(_oldValues);
    }
}

/// <summary>Places a COG in the level.</summary>
public sealed class CreateCogCommand : IEditCommand
{
    private readonly Level _level;
    private readonly int _oldNumber;

    public Cog Cog { get; }

    public CreateCogCommand(Level level, Cog cog)
    {
        _level = level;
        Cog = cog;
        _oldNumber = cog.Num;
    }

    public string Name => $"Add COG {Cog.Name}";

    public void Apply()
    {
        Cog.Num = _level.Cogs.Count;
        _level.Cogs.Add(Cog);
    }

    public void Revert()
    {
        _level.Cogs.Remove(Cog);
        Cog.Num = _oldNumber;
    }
}

/// <summary>
/// Removes a placed COG, remembering its index so undo restores it in place —
/// scripts refer to each other by COG number, so appending it back at the end
/// would repoint those references at the wrong script.
/// </summary>
public sealed class DeleteCogCommand : IEditCommand
{
    private readonly Level _level;
    private readonly Cog _cog;
    private int _index = -1;

    public DeleteCogCommand(Level level, Cog cog)
    {
        _level = level;
        _cog = cog;
    }

    public string Name => $"Delete COG {_cog.Name}";

    public void Apply()
    {
        _index = _level.Cogs.IndexOf(_cog);
        if (_index < 0) return;
        _level.Cogs.RemoveAt(_index);
        Renumber();
    }

    public void Revert()
    {
        if (_index < 0) return;
        _level.Cogs.Insert(System.Math.Min(_index, _level.Cogs.Count), _cog);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < _level.Cogs.Count; i++) _level.Cogs[i].Num = i;
    }
}
