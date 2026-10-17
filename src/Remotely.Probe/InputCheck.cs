using System.Text;
using Remotely.HostCore;
using Remotely.ViewerCore;

// Drives the input-fidelity and rich-clipboard extension through a real host so a CI step can read the result back on the host machine.
//
//   Remotely.Probe.exe input-check <host id or prefix> [--text "Grüße"] [--layout] [--html "<b>x</b>"] [--png FILE] [--files A,B]
//                      [--touch] [--pen] [--hold-key VK [--vanish]] [--wait 3]
//
// Exit code 0 if every requested step was accepted by the host, 5 if the host does not advertise the extension (RichClipboard) or a step failed.
// Suggested CI read-back on the Windows runner (not part of any workflow yet):
//   text:      show a WinForms TextBox with focus, run `input-check HOST --text "Grüße €ñ"`, compare TextBox.Text with the string;
//   html/png:  run `input-check HOST --html "<b>bold</b>"` / `--png x.png`, then Clipboard.ContainsText(TextDataFormat.Html) / ContainsImage();
//   release:   run `input-check HOST --hold-key 65 --vanish` and poll GetAsyncKeyState(0x41): it must be down while the probe lives and up within 10 s of it ending.
internal static class InputCheck
{
    public static async Task<int> RunAsync(ViewerSession session, string[] a)
    {
        var failures = 0;
        Console.WriteLine($"extended input supported: {session.SupportsExtendedInput}; rich clipboard: {session.HasRichClipboard}");
        if (!session.SupportsExtendedInput) return 5;

        if (Option(a, "--text") is { } text)
        {
            foreach (var unit in text)
            {
                session.SendInput(InputEvent.Text(unit, true));
                session.SendInput(InputEvent.Text(unit, false));
            }
            Console.WriteLine($"sent {text.Length} text code unit(s)");
        }
        if (a.Contains("--layout"))
        {
            var answered = new TaskCompletionSource<uint>();
            session.HostKeyboardLayoutChanged += l => answered.TrySetResult(l);
            await session.QueryHostKeyboardLayoutAsync();
            var done = await Task.WhenAny(answered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (done == answered.Task) Console.WriteLine($"host keyboard layout: 0x{answered.Task.Result:X8}");
            else { Console.WriteLine("host keyboard layout: no answer"); failures++; }
        }
        if (a.Contains("--touch"))
        {
            session.SendInput(InputEvent.Touch(TouchPhase.Down, 1, 30000, 30000));
            session.SendInput(InputEvent.Touch(TouchPhase.Move, 1, 31000, 31000));
            session.SendInput(InputEvent.Touch(TouchPhase.Up, 1, 31000, 31000));
            Console.WriteLine("sent touch down, move, up");
        }
        if (a.Contains("--pen"))
        {
            session.SendInput(InputEvent.Pen(PenPhase.Hover, 20000, 20000, 0, 0, 0));
            session.SendInput(InputEvent.Pen(PenPhase.Down, 20000, 20000, 600, 10, -10));
            session.SendInput(InputEvent.Pen(PenPhase.Move, 21000, 20500, 700, 10, -10));
            session.SendInput(InputEvent.Pen(PenPhase.Up, 21000, 20500, 0, 0, 0));
            session.SendInput(InputEvent.Pen(PenPhase.Leave, 21000, 20500, 0, 0, 0));
            Console.WriteLine("sent pen hover, down, move, up, leave");
        }
        if (Option(a, "--hold-key") is { } hold)
        {
            session.SendInput(InputEvent.Key(true, ushort.Parse(hold), 0, false));
            Console.WriteLine($"holding key {hold}");
            if (a.Contains("--vanish"))
            {
                await Task.Delay(1500); // let the input loop deliver the key and the first heartbeat
                Console.WriteLine("vanishing without releasing");
                Environment.Exit(0);
            }
        }

        var bundle = BuildBundle(Option(a, "--text"), Option(a, "--html"), Option(a, "--png"), Option(a, "--files"));
        if (bundle is { HasRich: true })
        {
            await session.SubscribeRichClipboardAsync(true);
            var outcome = await session.SendRichClipboardAsync(bundle);
            Console.WriteLine($"rich clipboard: {outcome.Status} {outcome.Message}");
            if (!outcome.Ok) failures++;
        }

        await Task.Delay(TimeSpan.FromSeconds(int.Parse(Option(a, "--wait") ?? "3")));
        return failures == 0 ? 0 : 5;
    }

    private static ClipBundle? BuildBundle(string? text, string? html, string? png, string? files)
    {
        var items = new List<ClipItem>();
        if (text is not null) items.Add(new ClipItem(ClipItemKind.Text, Encoding.UTF8.GetBytes(text)));
        if (html is not null) items.Add(new ClipItem(ClipItemKind.Html, Encoding.UTF8.GetBytes(html)));
        if (png is not null) items.Add(new ClipItem(ClipItemKind.Png, File.ReadAllBytes(png)));
        if (files is not null)
            items.Add(new ClipItem(ClipItemKind.Files, ClipFiles.Encode([.. files.Split(',').Select(f => new ClipFile(Path.GetFileName(f), File.ReadAllBytes(f)))])));
        return items.Count == 0 ? null : new ClipBundle(items);
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>Clipboard of the fake host: plain text like the original fake, plus the rich formats, printing what it receives.</summary>
internal sealed class RichFakeClipboard : IClipboardSync, IRichClipboardSync
{
    private long _sequence = 1;
    private string _text = "fake host clipboard";

    public Task<(long, string?)> ReadAsync(CancellationToken ct) => Task.FromResult((_sequence, (string?)_text));

    public Task<long> WriteAsync(string text, CancellationToken ct)
    {
        _text = text;
        Console.WriteLine("CLIPBOARD set: " + text);
        return Task.FromResult(++_sequence);
    }

    public Task<ClipReadResult> ReadBundleAsync(long knownSequence, ClipLimits limits, CancellationToken ct) =>
        Task.FromResult(new ClipReadResult(_sequence, null));

    public Task<long> WriteBundleAsync(ClipBundle bundle, CancellationToken ct)
    {
        foreach (var item in bundle.Items)
            Console.WriteLine($"CLIPBOARD rich item {item.Kind}: {item.Data.Length} bytes" + (item.Kind == ClipItemKind.Html ? " " + Encoding.UTF8.GetString(item.Data) : ""));
        if (bundle.Text is { } text) _text = text;
        return Task.FromResult(++_sequence);
    }
}
