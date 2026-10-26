using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

// Muro-Portable.exe: one file, the whole app inside. scripts/publish.ps1 appends the package (a zip) to the end of
// this exe, followed by a small trailer:  [zip][id][id length: int32][zip length: int64]["MUROPAYL"].
// The first run unpacks it to %LOCALAPPDATA%\Muro Portable\<version>-<hash>\ (a small window says so), every later
// run just starts Muro.exe from there. A newer Muro-Portable.exe unpacks next to it and removes the older folders.
// Nothing is installed: no registry entries, no shortcuts; delete the folder to remove it.
[DllImport("user32", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint owner, string text, string caption, uint type);
[DllImport("user32", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint exStyle, string cls, string text, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
[DllImport("user32")] static extern bool DestroyWindow(nint hwnd);
[DllImport("user32")] static extern int GetSystemMetrics(int index);
[DllImport("user32", CharSet = CharSet.Unicode)] static extern bool PeekMessageW(out Message message, nint hwnd, uint min, uint max, uint remove);
[DllImport("user32")] static extern bool TranslateMessage(ref Message message);
[DllImport("user32", CharSet = CharSet.Unicode)] static extern nint DispatchMessageW(ref Message message);

static void Fail(string text) => MessageBoxW(0, text, "Muro", 0x10);

var self = Environment.ProcessPath!;
var magic = "MUROPAYL"u8.ToArray();
long zipLength, zipStart;
string id;
try
{
    using var file = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    var trailer = new byte[20];
    if (file.Length < trailer.Length) throw new InvalidDataException("too small");
    file.Seek(-trailer.Length, SeekOrigin.End);
    file.ReadExactly(trailer);
    if (!trailer.AsSpan(12, 8).SequenceEqual(magic)) throw new InvalidDataException("no payload");
    var idLength = BitConverter.ToInt32(trailer, 0);
    zipLength = BitConverter.ToInt64(trailer, 4);
    var idStart = file.Length - trailer.Length - idLength;
    zipStart = idStart - zipLength;
    if (idLength is < 1 or > 200 || zipStart < 0) throw new InvalidDataException("bad trailer");
    var idBytes = new byte[idLength];
    file.Seek(idStart, SeekOrigin.Begin);
    file.ReadExactly(idBytes);
    id = Encoding.UTF8.GetString(idBytes);
}
catch (Exception ex) when (ex is InvalidDataException or IOException)
{
    Fail("This Muro-Portable.exe does not have the app inside it. Build it with scripts/publish.ps1, or download Muro-Portable from the release page again.");
    return 1;
}

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Muro Portable");
var target = Path.Combine(root, id);
var marker = Path.Combine(target, ".complete");

try
{
    if (!File.Exists(marker))
    {
        // Two copies started together unpack once: the second waits here and then finds the marker.
        using var gate = new Mutex(false, "Muro.Portable." + id);
        gate.WaitOne();
        try
        {
            if (!File.Exists(marker)) Unpack(self, zipStart, zipLength, root, target);
        }
        finally { gate.ReleaseMutex(); }
    }

    var exe = Path.Combine(target, "Muro.exe");
    if (!File.Exists(exe)) { Fail("Muro.exe is missing from " + target + ". Delete that folder and run this file again."); return 1; }
    var start = new ProcessStartInfo(exe) { WorkingDirectory = target, UseShellExecute = false };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    Process.Start(start);
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Fail("Muro could not be set up in " + target + ".\n\n" + ex.Message + "\n\nIs the disk full, or is the folder in use? Close Muro and try again.");
    return 1;
}

static void Unpack(string self, long zipStart, long zipLength, string root, string target)
{
    var partial = target + ".partial";
    if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
    Directory.CreateDirectory(root);
    var zipFile = partial + ".zip";

    Trace("unpack starts");
    var window = ShowWindow("Setting up Muro for the first time...\r\n(this takes a moment, once)");
    try
    {
        var work = Task.Run(() =>
        {
            // The package out of this exe into a normal file first, then the standard zip extraction from it.
            using (var source = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new FileStream(zipFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                source.Seek(zipStart, SeekOrigin.Begin);
                var buffer = new byte[1 << 20];
                for (var left = zipLength; left > 0;)
                {
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0) throw new InvalidDataException("the package inside this file is cut short");
                    copy.Write(buffer, 0, read);
                    left -= read;
                }
            }
            Trace("package copied out of the exe");
            ZipFile.ExtractToDirectory(zipFile, partial);
            Trace("package extracted");
        });
        // The small window needs its messages pumped, or Windows calls it "not responding".
        while (!work.IsCompleted)
        {
            while (PeekMessageW(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessageW(ref message); }
            Thread.Sleep(30);
        }
        work.GetAwaiter().GetResult();
    }
    finally
    {
        if (window != 0) DestroyWindow(window);
        try { File.Delete(zipFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    File.WriteAllText(Path.Combine(partial, ".complete"), DateTime.UtcNow.ToString("O"));
    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);   // an interrupted earlier unpack
    Directory.Move(partial, target);
    Trace("moved into place");

    // Older versions are no longer needed. One that is still running simply stays until it is closed.
    foreach (var old in Directory.GetDirectories(root))
        if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
            try { Directory.Delete(old, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
}

// MURO_PORTABLE_TRACE=<file> logs the phases of a first-run unpack with times, for finding where a setup is slow.
static void Trace(string what)
{
    if (Environment.GetEnvironmentVariable("MURO_PORTABLE_TRACE") is { } file)
        File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + what + Environment.NewLine);
}

static nint ShowWindow(string text)
{
    const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_BORDER = 0x00800000, SS_CENTER = 0x1, SS_CENTERIMAGE = 0x200;
    const int width = 380, height = 90;
    var x = (GetSystemMetrics(0) - width) / 2;
    var y = (GetSystemMetrics(1) - height) / 2;
    return CreateWindowExW(0x8 /* topmost */, "STATIC", text, WS_POPUP | WS_VISIBLE | WS_BORDER | SS_CENTER | SS_CENTERIMAGE, x, y, width, height, 0, 0, 0, 0);
}

[StructLayout(LayoutKind.Sequential)]
struct Message
{
    public nint Hwnd; public uint Msg; public nint WParam, LParam; public uint Time; public int X, Y; public uint Private;
}
