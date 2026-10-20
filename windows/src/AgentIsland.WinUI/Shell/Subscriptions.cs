using System.ComponentModel;

namespace AgentIsland.WinUI.Shell;

/// What a window or page subscribed to, so it can let go of all of it
/// together when it closes: a store's changes, a system event, anything with
/// an undo. Disposing undoes each in the order it was added, and the set can
/// be filled again.
internal sealed class Subscriptions : IDisposable
{
    private readonly List<Action> _undo = new();

    public void Add(Action undo) => _undo.Add(undo);

    public void Watch(INotifyPropertyChanged source, PropertyChangedEventHandler handler)
    {
        source.PropertyChanged += handler;
        _undo.Add(() => source.PropertyChanged -= handler);
    }

    public void Dispose()
    {
        foreach (var undo in _undo) undo();
        _undo.Clear();
    }
}
