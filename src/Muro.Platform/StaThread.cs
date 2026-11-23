using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Muro.Platform;

/// A thread of its own that pumps Windows messages in a single-threaded apartment, for things that must live on one
/// (a WebView2). Work is handed over with <see cref="Post"/>; a SynchronizationContext makes <c>await</c> inside
/// that work come back to this thread, which WebView2 requires. The engine's main thread is not an STA, and making it
/// one would reach into everything else it does.
public sealed class StaThread : IDisposable
{
    [DllImport("user32")] private static extern bool PeekMessage(out Native.MSG m, nint h, uint min, uint max, uint remove);

    private const uint WM_APP = 0x8000, WM_QUIT = 0x0012;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private volatile bool _stopped;

    public StaThread(string name)
    {
        _thread = new Thread(Run) { Name = name, IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// Runs on the STA thread, in order. Safe from any thread; ignored once stopped.
    public void Post(Action action)
    {
        if (_stopped) return;
        _queue.Enqueue(action);
        Native.PostThreadMessage(_threadId, WM_APP, 0, 0);
    }

    private sealed class Context(StaThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == owner._thread.ManagedThreadId) { d(state); return; }
            using var done = new ManualResetEventSlim();
            owner.Post(() => { try { d(state); } finally { done.Set(); } });
            done.Wait();
        }
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        // Force the thread's message queue to exist before anyone posts to it.
        PeekMessage(out _, 0, 0, 0, 0);
        _ready.Set();
        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_APP && msg.hwnd == 0)
            {
                while (_queue.TryDequeue(out var work))
                {
                    try { work(); } catch { /* one failed job must not stop the pump */ }
                }
                continue;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        Native.PostThreadMessage(_threadId, WM_QUIT, 0, 0);
    }
}
