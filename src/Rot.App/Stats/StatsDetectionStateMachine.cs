namespace Rot.App.Stats;

public enum StatsDetectionState
{
    Disconnected,
    ConnectedIdle,
    Local,
    Transition,
    Online
}

public sealed record StatsStateTransition(
    StatsDetectionState Previous,
    StatsDetectionState Current,
    long Epoch,
    string Trigger);

public sealed class StatsDetectionStateMachine
{
    private int _consecutiveEmptyUpdateStates;

    public StatsDetectionState State { get; private set; } = StatsDetectionState.Disconnected;
    public long Epoch { get; private set; }

    public StatsStateTransition? SetConnected(bool connected)
    {
        _consecutiveEmptyUpdateStates = 0;
        return TransitionTo(
            connected ? StatsDetectionState.ConnectedIdle : StatsDetectionState.Disconnected,
            connected ? "socket-connected" : "socket-disconnected");
    }

    public StatsStateTransition? Observe(StatsApiEvent statsEvent)
    {
        ArgumentNullException.ThrowIfNull(statsEvent);
        var name = statsEvent.Name;

        if (!statsEvent.HasMatchGuidField)
        {
            if (string.Equals(name, "UpdateState", StringComparison.OrdinalIgnoreCase))
            {
                _consecutiveEmptyUpdateStates = 0;
            }

            return null;
        }

        if (!statsEvent.HasOnlineMatchGuid && !statsEvent.HasKnownEmptyMatchGuid)
        {
            if (string.Equals(name, "UpdateState", StringComparison.OrdinalIgnoreCase))
            {
                _consecutiveEmptyUpdateStates = 0;
            }

            return null;
        }

        if (string.Equals(name, "MatchDestroyed", StringComparison.OrdinalIgnoreCase))
        {
            _consecutiveEmptyUpdateStates = 0;
            if (statsEvent.HasOnlineMatchGuid)
            {
                return TransitionTo(StatsDetectionState.ConnectedIdle, "online-match-destroyed");
            }

            if (State is StatsDetectionState.Local or StatsDetectionState.ConnectedIdle)
            {
                return TransitionTo(StatsDetectionState.Transition, "local-match-destroyed");
            }

            return null;
        }

        if (statsEvent.HasOnlineMatchGuid)
        {
            _consecutiveEmptyUpdateStates = 0;
            return TransitionTo(StatsDetectionState.Online, $"populated-{name}");
        }

        if (string.Equals(name, "MatchInitialized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "RoundStarted", StringComparison.OrdinalIgnoreCase))
        {
            _consecutiveEmptyUpdateStates = 0;
            return State == StatsDetectionState.Online
                ? null
                : TransitionTo(StatsDetectionState.Local, $"empty-{name}");
        }

        if (string.Equals(name, "UpdateState", StringComparison.OrdinalIgnoreCase))
        {
            if (State == StatsDetectionState.ConnectedIdle)
            {
                _consecutiveEmptyUpdateStates++;
                if (_consecutiveEmptyUpdateStates >= 2)
                {
                    _consecutiveEmptyUpdateStates = 0;
                    return TransitionTo(StatsDetectionState.Local, "two-empty-update-states");
                }
            }
            else if (State != StatsDetectionState.Local)
            {
                _consecutiveEmptyUpdateStates = 0;
            }

            return null;
        }

        return null;
    }

    private StatsStateTransition? TransitionTo(StatsDetectionState next, string trigger)
    {
        if (State == next)
        {
            return null;
        }

        var previous = State;
        State = next;
        Epoch++;
        return new StatsStateTransition(previous, next, Epoch, trigger);
    }
}
