using System.Text.Json;
using Muro.Core;
using Muro.Platform;

namespace Muro.Engine;

/// <c>Muro.Engine --interactive-selftest &lt;index.html&gt;</c>: starts an interactive wallpaper's page on the first
/// monitor, waits for it to load, feeds it a mouse move, a click and a sound frame, and reads back what the page
/// reports. For checking the WebView2 layer without looking at the screen (a locked PC draws nothing visible). Prints
/// PASS or FAIL and exits 0 or 1.
internal static class InteractiveSelfTest
{
    public static int Run(string page)
    {
        page = Path.GetFullPath(page);
        if (!File.Exists(page)) { Console.WriteLine("FAIL: no such page " + page); return 1; }
        var target = DesktopHost.Find();
        var monitor = Monitors.All().FirstOrDefault();
        if (target is null || monitor is null) { Console.WriteLine("FAIL: no desktop host or monitor"); return 1; }

        using var layer = new InteractiveLayer(target, monitor.Bounds, page);
        var failures = new List<string>();
        try
        {
            string state = "";
            for (var i = 0; i < 80 && state != "\"complete\""; i++)
            {
                Thread.Sleep(250);
                state = layer.EvaluateAsync("document.readyState").GetAwaiter().GetResult();
            }
            Console.WriteLine("readyState: " + state);
            if (state != "\"complete\"") failures.Add("page did not finish loading (" + state + ")");

            Console.WriteLine("page: " + layer.EvaluateAsync("JSON.stringify({w:innerWidth,h:innerHeight,canvases:document.querySelectorAll('canvas').length,title:document.title,host:location.host})").GetAwaiter().GetResult());
            layer.EvaluateAsync("window.__errs=[];addEventListener('error',e=>__errs.push(String(e.message)));0").GetAwaiter().GetResult();

            layer.Dispatch("muro:mouse", "{x:0.5,y:0.5,inside:true}");
            layer.Dispatch("muro:click", "{x:0.5,y:0.5}");
            var bands = string.Join(",", Enumerable.Range(0, 64).Select(i => (i % 8 / 8.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
            layer.Dispatch("muro:audio", "{bands:[" + bands + "],bass:0.6,mid:0.4,treble:0.2,level:0.7}");
            Thread.Sleep(1500);   // let a few frames run on the events

            var errors = layer.EvaluateAsync("JSON.stringify(window.__errs)").GetAwaiter().GetResult();
            Console.WriteLine("page errors after events: " + errors);
            if (errors != "\"[]\"") failures.Add("the page threw after the events: " + errors);

            // Locked in: only the page's own host answers.
            var host = layer.EvaluateAsync("location.host").GetAwaiter().GetResult();
            if (host != "\"muro.local\"") failures.Add("unexpected host " + host);
        }
        catch (Exception ex) { failures.Add(ex.GetType().Name + ": " + ex.Message); }

        Console.WriteLine(failures.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", failures));
        return failures.Count == 0 ? 0 : 1;
    }
}
