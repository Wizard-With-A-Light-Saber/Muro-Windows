using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Muro.Core;

namespace Muro.Platform;

/// A hidden top-level window that receives everything the engine reacts to: display changes,
/// power and session events, shell restarts, timers and work posted from other threads. Hidden
/// top-level (not message-only) because message-only windows never get WM_DISPLAYCHANGE.
/// Create and use it on the thread that runs <see cref="MessageLoop"/>.
public sealed class SystemEvents : IDisposable
{
    private const string ClassName = "MuroEngineEvents";
    private static readonly Guid DisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid PowerSaving = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");
    private static readonly Guid PowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private static readonly Guid BatteryPercent = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");

    private readonly Native.WndProc _proc;
    private readonly List<Native.WinEventProc> _hookProcs = [];
    private readonly List<nint> _hooks = [];
    private readonly Dictionary<nuint, (Action Action, bool Repeat)> _timers = [];
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly uint _taskbarCreated;
    private nuint _nextTimer = 1;
    private nint _hwnd;
    private SystemState _state = new();

    public SystemState State => _state;
    /// Display topology or resolution changed.
    public event Action? DisplayChanged;
    /// Power or session state changed; read <see cref="State"/>.
    public event Action? StateChanged;
    /// Foreground window changed, a window was minimised/restored/moved, or a virtual desktop switched.
    public event Action? WindowActivity;
    /// Explorer restarted, so the desktop host window is gone.
    public event Action? ShellRestarted;
    /// The PC woke from sleep or hibernation. Schedules re-evaluate here and not on every battery tick.
    public event Action? Resumed;
    /// The system clock or time zone changed.
    public event Action? TimeChanged;
    /// A handler threw. The engine must outlive any single failing handler, so it is reported here.
    public event Action<Exception>? Error;

    public SystemEvents()
    {
        _proc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        _hwnd = Native.CreateWindowEx(0, ClassName, "Muro events", 0x80000000 /* WS_POPUP, never shown */, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
        if (_hwnd == 0) throw new InvalidOperationException("event window failed");
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        foreach (var g in new[] { DisplayState, PowerSaving, PowerSource, BatteryPercent })
        {
            var guid = g;
            Native.RegisterPowerSettingNotification(_hwnd, ref guid, 0);
        }
        Native.WTSRegisterSessionNotification(_hwnd, 0);

        // Foreground change, move/size end, minimise start/end, and (un)cloak for virtual desktops.
        foreach (var (min, max) in new (uint, uint)[] { (0x0003, 0x0003), (0x000B, 0x000B), (0x0016, 0x0017), (0x8017, 0x8018) })
        {
            Native.WinEventProc cb = (_, _, _, _, _, _, _) => WindowActivity?.Invoke();
            _hookProcs.Add(cb);
            _hooks.Add(Native.SetWinEventHook(min, max, 0, cb, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS));
        }
        RefreshPower(notify: false);
    }

    /// Runs <paramref name="action"/> on the message-loop thread. Safe from any thread.
    public void Post(Action action)
    {
        _posted.Enqueue(action);
        Native.PostMessage(_hwnd, Native.WM_APP, 0, 0);
    }

    /// Timers fire on the message-loop thread. Returns an id for <see cref="StopTimer"/>.
    public nuint StartTimer(int milliseconds, Action action, bool repeat = false)
    {
        var id = _nextTimer++;
        _timers[id] = (action, repeat);
        Native.SetTimer(_hwnd, id, (uint)milliseconds, 0);
        return id;
    }

    public void StopTimer(nuint id)
    {
        if (_timers.Remove(id)) Native.KillTimer(_hwnd, id);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try { return Dispatch(hwnd, msg, wParam, lParam); }
        catch (Exception ex) { Error?.Invoke(ex); return Native.DefWindowProc(hwnd, msg, wParam, lParam); }
    }

    private nint Dispatch(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;
            case 0x001E:
                TimeChanged?.Invoke();
                break;
            case Native.WM_POWERBROADCAST:
                // 0x8013 setting change, 0xA status change, 0x12 and 0x7 resume.
                if ((int)wParam is 0x8013 or 0xA or 0x12 or 0x7)
                {
                    if ((int)wParam == 0x8013) ReadDisplayState(lParam);
                    RefreshPower(notify: true);
                    if ((int)wParam is 0x12 or 0x7) Resumed?.Invoke();
                }
                return 1;
            case Native.WM_WTSSESSION_CHANGE:
                if ((int)wParam is 7 or 8)
                {
                    _state = _state with { SessionLocked = (int)wParam == 7 };
                    StateChanged?.Invoke();
                }
                break;
            case Native.WM_TIMER:
                if (_timers.TryGetValue((nuint)wParam, out var t))
                {
                    if (!t.Repeat) StopTimer((nuint)wParam);
                    t.Action();
                }
                break;
            case Native.WM_APP:
                while (_posted.TryDequeue(out var work)) work();
                break;
            default:
                if (msg == _taskbarCreated) ShellRestarted?.Invoke();
                break;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ReadDisplayState(nint lParam)
    {
        var setting = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(lParam);
        if (setting.PowerSetting != DisplayState) return;
        // 0 off, 1 on, 2 dimmed. Dimmed is still visible.
        _state = _state with { DisplayOn = setting.Data != 0 };
    }

    private void RefreshPower(bool notify)
    {
        if (Native.GetSystemPowerStatus(out var s))
        {
            var noBattery = (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255;
            _state = _state with
            {
                BatterySaver = s.SystemStatusFlag == 1,
                OnBattery = s.ACLineStatus == 0 && !noBattery,
                BatteryPercent = noBattery ? null : s.BatteryLifePercent,
            };
        }
        // Always notify: the display-state half of a 0x8013 event changed _state too.
        if (notify) StateChanged?.Invoke();
    }

    public void Dispose()
    {
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hooks.Clear();
        if (_hwnd == 0) return;
        Native.WTSUnRegisterSessionNotification(_hwnd);
        foreach (var id in _timers.Keys.ToList()) StopTimer(id);
        Native.DestroyWindow(_hwnd);
        _hwnd = 0;
    }
}
