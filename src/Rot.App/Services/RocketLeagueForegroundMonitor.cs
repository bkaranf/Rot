using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Rot.App.Interop;

namespace Rot.App.Services;

internal enum ForegroundOwner
{
    External,
    Rot,
    RocketLeague
}

internal static class FocusLeaseVersioning
{
    internal static bool PredatesRevocation(long resourceLeaseEpoch, long revokedLeaseEpoch) =>
        resourceLeaseEpoch >= 0 && resourceLeaseEpoch < revokedLeaseEpoch;
}

internal static class ProcessEpochVersioning
{
    internal static bool PredatesChange(long resourceProcessEpoch, long changedProcessEpoch) =>
        resourceProcessEpoch >= 0 && resourceProcessEpoch < changedProcessEpoch;
}

internal sealed record RocketLeagueProcessSession(
    string ProcessName,
    int ProcessId,
    long StartTimeUtcTicks);

internal sealed record RocketLeagueProcessCandidate(
    RocketLeagueProcessSession Session,
    IDisposable Resource);

internal sealed class RocketLeagueProcessLookup : IDisposable
{
    private IReadOnlyList<RocketLeagueProcessCandidate>? _candidates;

    internal RocketLeagueProcessLookup(IEnumerable<RocketLeagueProcessCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _candidates = candidates.ToArray();
    }

    internal IReadOnlyList<RocketLeagueProcessSession> Sessions =>
        _candidates?.Select(candidate => candidate.Session).ToArray() ?? [];

    public void Dispose()
    {
        var candidates = Interlocked.Exchange(ref _candidates, null);
        if (candidates is null)
        {
            return;
        }

        Exception? firstFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                candidate.Resource.Dispose();
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }
    }
}

internal sealed class RocketLeagueProcessProbe
{
    internal static IReadOnlyList<string> ExactProcessNames { get; } =
        Array.AsReadOnly(["RocketLeague", "RocketLeague_EAC"]);

    private readonly Func<string, RocketLeagueProcessLookup> _lookupByExactName;

    internal RocketLeagueProcessProbe(
        Func<string, RocketLeagueProcessLookup>? lookupByExactName = null)
    {
        _lookupByExactName = lookupByExactName ?? LookupByExactName;
    }

    internal RocketLeagueProcessSession? GetCurrentSession()
    {
        using (var mainLookup = _lookupByExactName(ExactProcessNames[0]))
        {
            var main = SelectNewest(mainLookup.Sessions, ExactProcessNames[0]);
            if (main is not null)
            {
                return main;
            }
        }

        using var eacLookup = _lookupByExactName(ExactProcessNames[1]);
        return SelectNewest(eacLookup.Sessions, ExactProcessNames[1]);
    }

    internal bool IsRunning() => GetCurrentSession() is not null;

    private static RocketLeagueProcessSession? SelectNewest(
        IEnumerable<RocketLeagueProcessSession> sessions,
        string processName) =>
        sessions
            .Where(session => string.Equals(
                session.ProcessName,
                processName,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.StartTimeUtcTicks)
            .ThenByDescending(session => session.ProcessId)
            .FirstOrDefault();

    private static RocketLeagueProcessLookup LookupByExactName(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            var candidates = processes.Select(process => new RocketLeagueProcessCandidate(
                new RocketLeagueProcessSession(
                    processName,
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks),
                process));
            return new RocketLeagueProcessLookup(candidates);
        }
        catch
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }

            throw;
        }
    }
}

internal readonly record struct RocketLeagueInteractionGrant(
    long LeaseEpoch,
    long ProcessEpoch);

internal sealed record RocketLeagueForegroundChange(
    ForegroundOwner Owner,
    bool HasRocketLeagueFocusLease,
    bool LeaseChanged,
    long Epoch,
    long LeaseEpoch,
    bool IsProcessRunning,
    bool ProcessChanged,
    long ProcessEpoch,
    long? CurrentProcessStartedAt,
    RocketLeagueProcessSession? ProcessSession,
    long ObservedAt)
{
    internal bool AllowsPlayerPresentation => IsProcessRunning && HasRocketLeagueFocusLease;
}

internal sealed class RocketLeagueFocusPolicy
{
    private static readonly RocketLeagueProcessSession LegacyPresentSession =
        new("RocketLeague", -1, 0);

    private bool _hasObservation;

    internal ForegroundOwner Owner { get; private set; } = ForegroundOwner.External;

    internal bool HasRocketLeagueFocusLease { get; private set; }

    internal long Epoch { get; private set; }

    internal long LeaseEpoch { get; private set; }

    internal bool IsProcessRunning { get; private set; }

    internal RocketLeagueProcessSession? ProcessSession { get; private set; }

    internal long ProcessEpoch { get; private set; }

    internal long? CurrentProcessStartedAt { get; private set; }

    internal long LastObservedAt { get; private set; }

    internal RocketLeagueForegroundChange? Observe(
        ForegroundOwner owner,
        bool isProcessRunning,
        long observedAt) =>
        Observe(owner, isProcessRunning ? LegacyPresentSession : null, observedAt);

    internal RocketLeagueForegroundChange? Observe(
        ForegroundOwner owner,
        RocketLeagueProcessSession? processSession,
        long observedAt)
    {
        if (_hasObservation && observedAt <= LastObservedAt)
        {
            return null;
        }

        LastObservedAt = observedAt;
        var previousFocusLease = HasRocketLeagueFocusLease;
        var processChanged = !Equals(processSession, ProcessSession);
        var isProcessRunning = processSession is not null;
        var leaseBeforeOwnerReconciliation = processChanged
            ? false
            : HasRocketLeagueFocusLease;
        var focusLease = !isProcessRunning
            ? false
            : owner switch
            {
                ForegroundOwner.RocketLeague => true,
                ForegroundOwner.Rot => leaseBeforeOwnerReconciliation,
                _ => false
            };

        if (_hasObservation &&
            owner == Owner &&
            focusLease == HasRocketLeagueFocusLease &&
            !processChanged)
        {
            return null;
        }

        _hasObservation = true;
        Owner = owner;
        HasRocketLeagueFocusLease = focusLease;
        IsProcessRunning = isProcessRunning;
        ProcessSession = processSession;
        Epoch++;
        if (focusLease != previousFocusLease)
        {
            LeaseEpoch++;
        }
        if (processChanged)
        {
            ProcessEpoch++;
            CurrentProcessStartedAt = isProcessRunning ? observedAt : null;
        }
        return new RocketLeagueForegroundChange(
            owner,
            focusLease,
            focusLease != previousFocusLease,
            Epoch,
            LeaseEpoch,
            isProcessRunning,
            processChanged,
            ProcessEpoch,
            CurrentProcessStartedAt,
            ProcessSession,
            observedAt);
    }
}

internal sealed class RocketLeagueForegroundMonitor : IDisposable
{
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly RocketLeagueProcessSession LegacyPresentSession =
        new("RocketLeague", -1, 0);

    private readonly object _sync = new();
    private readonly Func<nint> _getForegroundWindow;
    private readonly Func<nint, int?> _getWindowProcessId;
    private readonly Func<int, string?> _getProcessName;
    private readonly Func<RocketLeagueProcessSession?> _getProcessSession;
    private readonly Func<long> _getTimestamp;
    private readonly TimeSpan _pollInterval;
    private readonly int _rotProcessId;
    private readonly RocketLeagueFocusPolicy _policy = new();
    private Timer? _timer;
    private int _polling;
    private int _disposed;
    private int _desktopSettingsActive;

    internal RocketLeagueForegroundMonitor(
        Func<nint>? getForegroundWindow = null,
        Func<nint, int?>? getWindowProcessId = null,
        Func<int, string?>? getProcessName = null,
        Func<bool>? getProcessPresence = null,
        Func<RocketLeagueProcessSession?>? getProcessSession = null,
        Func<long>? getTimestamp = null,
        TimeSpan? pollInterval = null,
        int? rotProcessId = null)
    {
        _getForegroundWindow = getForegroundWindow ?? NativeMethods.GetForegroundWindow;
        _getWindowProcessId = getWindowProcessId ?? ResolveWindowProcessId;
        _getProcessName = getProcessName ?? ResolveProcessName;
        if (getProcessPresence is not null && getProcessSession is not null)
        {
            throw new ArgumentException(
                "Specify either a process-presence getter or a process-session getter, not both.");
        }

        _getProcessSession = getProcessSession ?? (getProcessPresence is null
            ? new RocketLeagueProcessProbe().GetCurrentSession
            : () => getProcessPresence() ? LegacyPresentSession : null);
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _rotProcessId = rotProcessId ?? Environment.ProcessId;
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
    }

    internal event EventHandler<RocketLeagueForegroundChange>? Changed;

    internal void SetDesktopSettingsActive(bool active)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref _desktopSettingsActive, active ? 1 : 0);
    }

    internal bool IsDesktopSettingsActive => Volatile.Read(ref _desktopSettingsActive) != 0;

    internal ForegroundOwner Owner
    {
        get
        {
            lock (_sync)
            {
                return _policy.Owner;
            }
        }
    }

    internal bool HasRocketLeagueFocusLease
    {
        get
        {
            lock (_sync)
            {
                return _policy.HasRocketLeagueFocusLease;
            }
        }
    }

    internal bool AllowsPlayerPresentation
    {
        get
        {
            lock (_sync)
            {
                return _policy.IsProcessRunning && _policy.HasRocketLeagueFocusLease;
            }
        }
    }

    internal long Epoch
    {
        get
        {
            lock (_sync)
            {
                return _policy.Epoch;
            }
        }
    }

    internal long LeaseEpoch
    {
        get
        {
            lock (_sync)
            {
                return _policy.LeaseEpoch;
            }
        }
    }

    internal bool IsProcessRunning
    {
        get
        {
            lock (_sync)
            {
                return _policy.IsProcessRunning;
            }
        }
    }

    internal long ProcessEpoch
    {
        get
        {
            lock (_sync)
            {
                return _policy.ProcessEpoch;
            }
        }
    }

    internal long? CurrentProcessStartedAt
    {
        get
        {
            lock (_sync)
            {
                return _policy.CurrentProcessStartedAt;
            }
        }
    }

    internal bool IsEvidenceForCurrentProcess(long observedAt)
        => TryGetProcessEpochForEvidence(observedAt, out _);

    internal bool TryGetProcessEpochForEvidence(long observedAt, out long processEpoch)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            processEpoch = 0;
            return false;
        }

        RocketLeagueForegroundChange? change;
        bool valid;
        lock (_sync)
        {
            var processSession = ReadProcessSession();
            var currentOwner = ReadOwner();
            var sampledAt = _getTimestamp();
            change = Volatile.Read(ref _disposed) == 0
                ? _policy.Observe(currentOwner, processSession, sampledAt)
                : null;
            if (_policy.IsProcessRunning &&
                _policy.CurrentProcessStartedAt is { } startedAt &&
                observedAt >= startedAt)
            {
                processEpoch = _policy.ProcessEpoch;
                valid = true;
            }
            else
            {
                processEpoch = 0;
                valid = false;
            }
        }

        DispatchChange(change);
        return valid;
    }

    internal bool IsCurrentProcessEpoch(long processEpoch)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        RocketLeagueForegroundChange? change;
        bool current;
        lock (_sync)
        {
            var processSession = ReadProcessSession();
            var currentOwner = ReadOwner();
            var observedAt = _getTimestamp();
            change = Volatile.Read(ref _disposed) == 0
                ? _policy.Observe(currentOwner, processSession, observedAt)
                : null;
            current = _policy.IsProcessRunning && processEpoch == _policy.ProcessEpoch;
        }

        DispatchChange(change);
        return current;
    }

    internal bool IsCurrentObservedProcessEpoch(long processEpoch)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        lock (_sync)
        {
            return Volatile.Read(ref _disposed) == 0 &&
                   _policy.IsProcessRunning &&
                   processEpoch == _policy.ProcessEpoch;
        }
    }

    internal bool TryGetForegroundInteractionGrant(out RocketLeagueInteractionGrant grant)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            grant = default;
            return false;
        }

        RocketLeagueForegroundChange? change;
        bool allowed;
        lock (_sync)
        {
            var processSession = ReadProcessSession();
            var currentOwner = ReadOwner();
            var observedAt = _getTimestamp();
            change = Volatile.Read(ref _disposed) == 0
                ? _policy.Observe(currentOwner, processSession, observedAt)
                : null;
            allowed = Volatile.Read(ref _disposed) == 0 &&
                      processSession is not null &&
                      (currentOwner == ForegroundOwner.RocketLeague ||
                       (currentOwner == ForegroundOwner.Rot &&
                        _policy.HasRocketLeagueFocusLease));
            grant = allowed
                ? new RocketLeagueInteractionGrant(_policy.LeaseEpoch, _policy.ProcessEpoch)
                : default;
        }

        DispatchChange(change);
        return allowed;
    }

    internal bool AllowsForegroundInteractionNow() =>
        TryGetForegroundInteractionGrant(out _);

    internal bool IsCurrentInteractionGrant(RocketLeagueInteractionGrant grant) =>
        TryGetForegroundInteractionGrant(out var current) && current == grant;

    internal bool CanRestoreFocusToRocketLeague(nint targetWindow)
    {
        if (targetWindow == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        RocketLeagueForegroundChange? change;
        bool canRestore;
        lock (_sync)
        {
            var processSession = ReadProcessSession();
            var currentOwner = ReadOwner();
            var observedAt = _getTimestamp();
            change = Volatile.Read(ref _disposed) == 0
                ? _policy.Observe(currentOwner, processSession, observedAt)
                : null;
            canRestore = Volatile.Read(ref _disposed) == 0 &&
                         processSession is not null &&
                         currentOwner == ForegroundOwner.Rot &&
                         _policy.HasRocketLeagueFocusLease;
        }

        DispatchChange(change);
        return canRestore && ReadOwner(targetWindow) == ForegroundOwner.RocketLeague;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        PollNow();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _timer ??= new Timer(
                static state => ((RocketLeagueForegroundMonitor)state!).PollNow(),
                this,
                _pollInterval,
                _pollInterval);
        }
    }

    internal void PollNow()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            RocketLeagueForegroundChange? change;
            lock (_sync)
            {
                var processSession = ReadProcessSession();
                var owner = ReadOwner();
                var observedAt = _getTimestamp();
                change = Volatile.Read(ref _disposed) == 0
                    ? _policy.Observe(owner, processSession, observedAt)
                    : null;
            }

            if (change is not null)
            {
                DispatchChange(change);
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    internal static bool IsRocketLeagueProcessName(string? processName) =>
        string.Equals(processName, "RocketLeague", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "RocketLeague_EAC", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Timer? timer;
        lock (_sync)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    private ForegroundOwner ReadOwner() => ReadOwner(_getForegroundWindow());

    private RocketLeagueProcessSession? ReadProcessSession()
    {
        try
        {
            return _getProcessSession();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Rocket League process-session lookup failed closed: {exception.Message}");
            return null;
        }
    }

    private void DispatchChange(RocketLeagueForegroundChange? change)
    {
        if (change is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, change);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Foreground change dispatch failed: {exception.Message}");
        }
    }

    private ForegroundOwner ReadOwner(nint windowHandle)
    {
        try
        {
            if (windowHandle == 0)
            {
                return ForegroundOwner.External;
            }

            var processId = _getWindowProcessId(windowHandle);
            if (processId is not > 0)
            {
                return ForegroundOwner.External;
            }

            if (processId == _rotProcessId)
            {
                return Volatile.Read(ref _desktopSettingsActive) != 0
                    ? ForegroundOwner.External
                    : ForegroundOwner.Rot;
            }

            return IsRocketLeagueProcessName(_getProcessName(processId.Value))
                ? ForegroundOwner.RocketLeague
                : ForegroundOwner.External;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Foreground process lookup failed: {exception.Message}");
            return ForegroundOwner.External;
        }
    }

    private static int? ResolveWindowProcessId(nint windowHandle)
    {
        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return processId is > 0 and <= int.MaxValue ? (int)processId : null;
    }

    private static string? ResolveProcessName(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.ProcessName;
    }
}
