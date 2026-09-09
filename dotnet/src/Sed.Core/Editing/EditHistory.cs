namespace Sed.Core.Editing;

/// <summary>A reversible edit. <see cref="Apply"/> performs it; <see cref="Revert"/> undoes it.</summary>
public interface IEditCommand
{
    string Name { get; }
    void Apply();
    void Revert();
}

/// <summary>
/// Undo/redo stack. <see cref="Do"/> applies a command and records it; a new edit
/// clears the redo stack (standard linear history).
/// </summary>
public sealed class EditHistory
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();

    /// <summary>Raised after any change to the history (do/undo/redo/clear).</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo.Peek().Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo.Peek().Name : null;

    public void Do(IEditCommand command)
    {
        command.Apply();
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.Peek();
        cmd.Revert();
        _undo.Pop();
        _redo.Push(cmd);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.Peek();
        cmd.Apply();
        _redo.Pop();
        _undo.Push(cmd);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
