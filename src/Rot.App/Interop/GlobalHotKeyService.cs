using System.ComponentModel;
using System.Windows.Interop;
using Rot.App.Models;

namespace Rot.App.Interop;

public sealed record HotKeyRegistrationFailure(string Action, string Chord, string Message);

internal sealed class HotKeyPressedEventArgs(string action, HotKeyChord chord) : EventArgs
{
    public string Action { get; } = action;
    public HotKeyChord Chord { get; } = chord;
}

internal interface IGlobalHotKeyService : IDisposable
{
    event EventHandler<HotKeyPressedEventArgs>? Pressed;

    IReadOnlyList<HotKeyRegistrationFailure> Register(
        IReadOnlyDictionary<string, HotKeyChord> bindings);

    void UnregisterAll();
}

internal sealed class GlobalHotKeyService : IGlobalHotKeyService
{
    private const int FirstIdentifier = 0x5200;
    private readonly HwndSource _messageSource;
    private readonly Dictionary<int, string> _actionsByIdentifier = [];
    private bool _disposed;

    public GlobalHotKeyService()
    {
        var parameters = new HwndSourceParameters("Rot.GlobalHotKeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = unchecked((int)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate))
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WindowProcedure);
    }

    public event EventHandler<HotKeyPressedEventArgs>? Pressed;

    public IReadOnlyList<HotKeyRegistrationFailure> Register(IReadOnlyDictionary<string, HotKeyChord> bindings)
    {
        ThrowIfDisposed();
        UnregisterAll();
        var failures = new List<HotKeyRegistrationFailure>();
        var identifier = FirstIdentifier;

        foreach (var (action, chord) in bindings
                     .Where(item => HotKeyCatalog.IsKnownAction(item.Key))
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var modifiers = (uint)(chord.Modifiers | HotKeyModifiers.NoRepeat);
            if (!NativeMethods.RegisterHotKey(_messageSource.Handle, identifier, modifiers, chord.VirtualKey))
            {
                var error = new Win32Exception().Message;
                failures.Add(new HotKeyRegistrationFailure(action, chord.DisplayText, error));
            }
            else
            {
                _actionsByIdentifier[identifier] = action;
            }

            identifier++;
        }

        return failures;
    }

    public void UnregisterAll()
    {
        foreach (var identifier in _actionsByIdentifier.Keys)
        {
            NativeMethods.UnregisterHotKey(_messageSource.Handle, identifier);
        }

        _actionsByIdentifier.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _messageSource.RemoveHook(WindowProcedure);
        _messageSource.Dispose();
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotKey || !_actionsByIdentifier.TryGetValue(wParam.ToInt32(), out var action))
        {
            return 0;
        }

        handled = true;
        var packed = unchecked((ulong)lParam.ToInt64());
        var modifiers = (HotKeyModifiers)(packed & 0xFFFF);
        modifiers &= ~HotKeyModifiers.NoRepeat;
        var virtualKey = (uint)((packed >> 16) & 0xFFFF);
        Pressed?.Invoke(this, new HotKeyPressedEventArgs(
            action,
            new HotKeyChord(modifiers, virtualKey)));
        return 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
