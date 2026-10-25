namespace Muro.Platform;

public static class MessageLoop
{
    private const uint WM_QUIT = 0x0012;
    private static uint _threadId;

    /// Pumps messages on the calling thread until <see cref="Quit"/>.
    public static void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    /// Safe from any thread: PostQuitMessage only reaches the caller's own queue.
    public static void Quit() => Native.PostThreadMessage(_threadId, WM_QUIT, 0, 0);
}
