namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// Work a handler wants done once the event's transaction has committed, e.g. telling another
/// part of the app about a row the transaction wrote, which it could not see before the commit.
/// Scoped, and <see cref="EventProcessor"/> uses a scope per attempt, so an attempt that rolls
/// back takes its actions with it. Keep actions quick and non-throwing: they run on the consume
/// loop, and the event is already handled.
/// </summary>
public sealed class AfterCommit
{
    private readonly List<Action> _actions = [];

    public void Add(Action action) => _actions.Add(action);

    internal void Run()
    {
        foreach (var action in _actions)
        {
            action();
        }

        _actions.Clear();
    }
}
