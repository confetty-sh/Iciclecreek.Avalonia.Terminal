using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Terminal.AotSmoke;

// Temporary diagnostic: write then read the clipboard many times and classify every miss.
internal static partial class ClipboardProbe
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern int IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private static string Who(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "-";
        GetWindowThreadProcessId(hwnd, out var pid);
        try { return pid == Environment.ProcessId ? "self" : $"{Process.GetProcessById((int)pid).ProcessName}({pid})"; }
        catch { return $"pid{pid}"; }
    }

    public static async Task RunAsync(IClipboard clipboard, int count, int delayMs)
    {
        var outcomes = new Dictionary<string, int>();
        var holders = new Dictionary<string, int>();
        var recoveries = new List<double>();
        var details = 0;
        var firstMissAt = -1;
        var misses = 0;

        for (var i = 0; i < count; i++)
        {
            var expected = $"probe-{i}";
            await clipboard.SetTextAsync(expected);
            if (delayMs > 0)
                await Task.Delay(delayMs);

            var clock = Stopwatch.StartNew();
            var holderAtRead = Who(GetOpenClipboardWindow());
            var seq = GetClipboardSequenceNumber();
            var available = IsClipboardFormatAvailable(13) != 0;

            var (outcome, formats) = await ReadAsync(clipboard, expected);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;

            if (outcome != "ok")
            {
                misses++;
                if (firstMissAt < 0)
                    firstMissAt = i;
                var holderAfter = Who(GetOpenClipboardWindow());
                holders[$"{holderAtRead}->{holderAfter}"] = holders.GetValueOrDefault($"{holderAtRead}->{holderAfter}") + 1;

                var attempts = 1;
                string again;
                do
                {
                    await Task.Delay(2);
                    attempts++;
                    (again, _) = await ReadAsync(clipboard, expected);
                }
                while (again != "ok" && clock.ElapsedMilliseconds < 2000);
                recoveries.Add(clock.Elapsed.TotalMilliseconds);

                if (details++ < 6)
                    Console.WriteLine($"  miss #{i}: {outcome} formats=[{formats}] cfAvail={available} seq={seq}->{GetClipboardSequenceNumber()} holder={holderAtRead}->{holderAfter} owner={Who(GetClipboardOwner())} recovered={again == "ok"} after {clock.Elapsed.TotalMilliseconds:F0}ms/{attempts} reads");
            }

            await Task.Delay(30);
        }

        recoveries.Sort();
        Console.WriteLine($"PROBE count={count} delayMs={delayMs} misses={misses} firstMissAt={firstMissAt}");
        Console.WriteLine("  outcomes: " + string.Join("  ", outcomes.Select(o => $"{o.Key}={o.Value}")));
        if (holders.Count > 0)
            Console.WriteLine("  holders at read->after: " + string.Join("  ", holders.Select(h => $"{h.Key}={h.Value}")));
        if (recoveries.Count > 0)
            Console.WriteLine($"  recovery ms: min={recoveries[0]:F0} median={recoveries[recoveries.Count / 2]:F0} max={recoveries[^1]:F0}");
    }

    private static async Task<(string Outcome, string Formats)> ReadAsync(IClipboard clipboard, string expected)
    {
        try
        {
            using var data = await clipboard.TryGetDataAsync();
            if (data is null)
                return ("null-data", "");
            var formats = string.Join(",", data.Formats.Select(f => f.ToString()));
            if (!data.Formats.Contains(DataFormat.Text))
                return ("no-text-format", formats);
            var text = await data.TryGetTextAsync();
            if (text is null)
                return ("value-null", formats);
            return (text == expected ? "ok" : "stale", formats);
        }
        catch (Exception e)
        {
            return ($"threw-{e.GetType().Name}-{e.HResult:X8}", "");
        }
    }
}
