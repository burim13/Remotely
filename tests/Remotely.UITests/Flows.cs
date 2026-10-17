using System.Drawing;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Remotely.UITests.Imaging;

namespace Remotely.UITests;

/// <summary>
/// Walks the real app like a person would, through UI Automation: the first-run flow, the Home page (a wrong token, sharing this PC and reading the token) and the Settings page.
/// Every step reads what the app shows and fails with the words it found. Screenshots are taken after each step.
/// </summary>
internal sealed class Flows(string exe, string outDir, string maskedDir, Strings text, string theme, int scalePercent)
{
    private static readonly AppKind Kind = AppKind.Viewer;
    private static readonly Regex TokenPattern = new(@"^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", RegexOptions.Compiled);
    private readonly List<Outcome> _results = [];
    private string Exe => exe;
    private string OutDir => outDir;
    private string MaskedDir => maskedDir;
    private Strings Texts => text;

    public List<Outcome> RunAll()
    {
        Directory.CreateDirectory(outDir);
        Run("first-run onboarding", Onboarding);
        Run("home: wrong token, share this PC, settings", Home);
        return _results;
    }

    private void Run(string name, Action<Flow> body)
    {
        AppSession.KillAll(Kind);
        Kind.ClearCrashLog();
        var flow = new Flow(this, name);
        try { body(flow); }
        catch (FlowAbort) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            flow.Fail("flow stopped by " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { flow.Close(); }
        var crash = Kind.ReadCrashLog();
        if (crash.Length > 0) flow.Fail("crash log not empty: " + (crash.Length > 600 ? crash[..600] : crash).Replace("\r", " ").Replace("\n", " "));
        var ok = flow.Failures.Count == 0;
        _results.Add(new Outcome("flow " + name, ok, ok ? string.Join("; ", flow.Notes) : string.Join(" | ", flow.Failures) + " [" + string.Join("; ", flow.Notes) + "]"));
        Console.WriteLine($"FLOW {(ok ? "OK" : "FAIL")}: {name}");
        foreach (var n in flow.Notes) Console.WriteLine("  ok: " + n);
        foreach (var f in flow.Failures) Console.WriteLine("  FAILED: " + f);
    }

    private sealed class FlowAbort : Exception;

    /// <summary>One flow's running state: the app, the UIA connection, the notes (what was verified) and the failures.</summary>
    private sealed class Flow(Flows owner, string name) : IDisposable
    {
        private AppSession? _session;
        private Uia? _uia;
        private AutomationElement? _root;
        private IntPtr _hwnd;
        private int _shot;

        public List<string> Notes { get; } = [];
        public List<string> Failures { get; } = [];
        public Strings Text => owner.Texts;

        public void Fail(string message) => Failures.Add(message);

        public void Launch(params string[] args)
        {
            _session = AppSession.Start(Kind, owner.Exe, args);
            _hwnd = _session.WaitForMainWindow(TimeSpan.FromSeconds(60), out var why);
            if (_hwnd == IntPtr.Zero) { Fail("window did not appear: " + why); throw new FlowAbort(); }
            _uia = new Uia();
            _root = _uia.FromHandle(_hwnd);
            AppSession.BringToFront(_hwnd);
        }

        public List<UNode> Tree() => _uia!.Snapshot(_root!);

        /// <summary>Polls the UI Automation tree until the finder returns something; on timeout records a failure that lists what the window did show, and stops the flow.</summary>
        public UNode Wait(string what, Func<List<UNode>, UNode?> find, int seconds = 20)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            List<UNode> nodes;
            while (true)
            {
                if (_session!.HasExited) { Fail($"the app exited (code {_session.ExitCode}) while waiting for: {what}"); throw new FlowAbort(); }
                nodes = Tree();
                if (find(nodes) is { } hit) return hit;
                if (DateTime.UtcNow > deadline) break;
                Thread.Sleep(400);
            }
            var seen = string.Join(" | ", nodes.Where(n => n.Name.Length > 0 && !n.Offscreen).Select(n => n.Name).Distinct().Take(25));
            File.WriteAllText(Path.Combine(owner.OutDir, "tree-" + Slug(name) + "-failed-" + Slug(what) + ".txt"), Uia.Dump(nodes));
            Fail($"not found within {seconds} s: {what}. Visible names were: {seen}");
            Shot("failed-" + Slug(what));
            throw new FlowAbort();
        }

        public void Ok(string note) => Notes.Add(note);

        public void Click(UNode button)
        {
            var el = button.Element;
            if (el.Patterns.Invoke.IsSupported) el.Patterns.Invoke.Pattern.Invoke();
            else el.Click();
        }

        public void SetText(UNode box, string value)
        {
            var el = box.Element;
            if (el.Patterns.Value.IsSupported) el.Patterns.Value.Pattern.SetValue(value);
            else { el.Focus(); Keyboard.Type(value); }
        }

        public void Toggle(UNode node)
        {
            if (node.Element.Patterns.Toggle.IsSupported) node.Element.Patterns.Toggle.Pattern.Toggle();
            else node.Element.Click();
        }

        public void CheckNoSurpriseWindows()
        {
            var others = _session!.OtherWindows(_hwnd);
            if (others.Count > 0) Fail("unexpected extra window: " + string.Join("; ", others));
            var dialog = Tree().FirstOrDefault(n => n.Class.Contains("ContentDialog", StringComparison.OrdinalIgnoreCase));
            if (dialog is not null) Fail($"unexpected dialog in the window ({dialog.Class}, name \"{dialog.Name}\")");
        }

        public void Shot(string label)
        {
            if (_session is null || _session.HasExited) return;
            try
            {
                Thread.Sleep(900);
                AppSession.BringToFront(_hwnd);
                Thread.Sleep(250);
                var nodes = Tree();
                var image = Capture.Window(_hwnd);
                var masks = Masking.For(nodes, Capture.FrameOf(_hwnd), image.Width, image.Height);
                var file = $"flow-{Slug(name)}-{++_shot:00}-{Slug(label)}.png";
                Capture.Save(image.DownscaleToWidth(Capture.MaxWidth), Path.Combine(owner.OutDir, file));
                Capture.Save(Capture.Mask(image, masks).DownscaleToWidth(Capture.MaxWidth), Path.Combine(owner.MaskedDir, file));
                if (!Capture.LooksRendered(image).HasContent) Fail("screenshot looks blank after step: " + label);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException)
            {
                Fail($"screenshot '{label}' failed: {ex.Message}");
            }
        }

        public void Close()
        {
            _session?.Stop();
            _uia?.Dispose();
            _session?.Dispose();
            _session = null;
            _uia = null;
        }

        public void Dispose() => Close();

        public static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    }

    // ---- the flows ----

    private void Onboarding(Flow f)
    {
        Kind.ClearSettings();
        Kind.WriteSettings(theme, onboardingDone: false, scalePercent, o => { o.Remove("DeviceName"); return o; });
        f.Launch();

        var step1 = f.Wait("'Step 1 of 4' (the first-run flow)", n => Uia.FindMatching(n, StepRegex(1)));
        f.Ok($"first-run flow shown, step text \"{step1.Name}\"");
        f.Wait("the PC name box", n => Uia.Find(n, names: [text.Get("Onboarding_Name_Header")], type: "Edit"));
        f.Shot("step1-name");
        var nameBox = f.Wait("the PC name box", n => Uia.Find(n, names: [text.Get("Onboarding_Name_Header")], type: "Edit"));
        f.SetText(nameBox, "UITest-PC");
        f.CheckNoSurpriseWindows();
        f.Click(f.Wait("the Next button", n => Uia.Find(n, ids: ["NextButton"], names: [text.Get("Onboarding_Next")], type: "Button")));

        f.Wait("'Step 2 of 4'", n => Uia.FindMatching(n, StepRegex(2)));
        f.Wait("the share question", n => Uia.FindContaining(n, text.Get("Onboarding_Share_Title")));
        f.Ok("step 2 shows: " + text.Get("Onboarding_Share_Title"));
        f.Shot("step2-share");
        f.Click(f.Wait("the Next button", n => Uia.Find(n, ids: ["NextButton"], names: [text.Get("Onboarding_Next")], type: "Button")));

        f.Wait("'Step 3 of 4'", n => Uia.FindMatching(n, StepRegex(3)));
        f.Wait("the server step", n => Uia.FindContaining(n, text.Get("Onboarding_Server_Title")));
        f.Ok("step 3 shows: " + text.Get("Onboarding_Server_Title"));
        f.Shot("step3-server");
        f.Click(f.Wait("the Next button", n => Uia.Find(n, ids: ["NextButton"], names: [text.Get("Onboarding_Next")], type: "Button")));

        f.Wait("'Step 4 of 4'", n => Uia.FindMatching(n, StepRegex(4)));
        f.Wait("the summary", n => Uia.FindContaining(n, text.Get("Onboarding_Done_Title")));
        var summary = f.Wait("the PC name in the summary", n => Uia.FindContaining(n, "UITest-PC"));
        f.Ok($"step 4 summary mentions the chosen name: \"{summary.Name}\"");
        f.Shot("step4-done");
        f.Click(f.Wait("the Finish button", n => Uia.Find(n, ids: ["NextButton"], names: [text.Get("Onboarding_Finish")], type: "Button")));

        f.Wait("the Home page after the flow", n => Uia.Find(n, ids: ["ShareSwitch"]));
        f.Ok("after Finish the Home page with the Share switch is shown");
        f.CheckNoSurpriseWindows();
        f.Shot("after-finish");

        var saved = ReadSettings();
        if (saved?["OnboardingDone"]?.GetValue<bool>() != true) f.Fail("viewer.json does not say OnboardingDone after Finish");
        else f.Ok("viewer.json has OnboardingDone = true");
        if ((string?)saved?["DeviceName"] != "UITest-PC") f.Fail($"viewer.json DeviceName is \"{(string?)saved?["DeviceName"]}\", expected the typed UITest-PC");
        else f.Ok("viewer.json has the typed PC name");
    }

    private void Home(Flow f)
    {
        Kind.WriteSettings(theme, onboardingDone: true, scalePercent);
        f.Launch();

        var box = f.Wait("the token box", n => Uia.Find(n, ids: ["TokenBox"], names: [text.Get("Connect_TokenName")], type: "Edit"));
        f.Shot("home");

        f.SetText(box, "ABC");
        var hint = f.Wait("the 'x of 12 characters' hint", n => Uia.FindContaining(n, "of 12 characters"));
        f.Ok($"typing an incomplete token shows the hint \"{hint.Name}\"");
        f.Shot("token-incomplete");

        f.SetText(f.Wait("the token box", n => Uia.Find(n, ids: ["TokenBox"], names: [text.Get("Connect_TokenName")], type: "Edit")), "ZZZZ-ZZZZ-ZZZZ");
        f.Click(f.Wait("the Connect button", n => Uia.Find(n, ids: ["ConnectButton"], names: [text.Get("Connect_Button")], type: "Button")));
        var error = f.Wait("a friendly error after connecting with a token nobody shows",
            n => Uia.FindContaining(n, "No PC on this network is showing a token", "not connected to a network", "only an IPv6", text.Get("Connect_Timeout")), seconds: 40);
        f.Ok($"a wrong token ends with a readable error: \"{error.Name}\"");
        if (Regex.IsMatch(error.Name, @"Exception|\bat \w+\.\w+\(|HRESULT|0x[0-9A-Fa-f]{6,}")) f.Fail("the error text looks technical: " + error.Name);
        f.CheckNoSurpriseWindows();
        f.Shot("wrong-token-error");

        var share = f.Wait("the Share this PC switch", n => Uia.Find(n, ids: ["ShareSwitch"]));
        f.Toggle(share);
        f.Wait("the token field after switching sharing on", n => Uia.Find(n, ids: ["TokenText"]), seconds: 30);
        // The token text is exposed to UI Automation only under the name "Token", so read it the way a person would: press Copy and look at the clipboard.
        f.Click(f.Wait("the Copy button", n => Uia.Find(n, ids: ["CopyButton"], names: [text.Get("Share_Copy")], type: "Button")));
        Thread.Sleep(600);
        var copied = Native.ClipboardText()?.Trim() ?? "";
        if (!TokenPattern.IsMatch(copied)) f.Fail($"Copy did not put a token of the form XXXX-XXXX-XXXX on the clipboard (clipboard: \"{(copied.Length > 40 ? copied[..40] : copied)}\")");
        else f.Ok($"sharing on shows a token and Copy puts it on the clipboard in the form XXXX-XXXX-XXXX (starts {copied[..4]})");
        var ready = Uia.FindContaining(f.Tree(), text.Get("Share_State_Ready").Split('.')[0]);
        if (ready is null) f.Fail("the 'Ready' state line is not shown while sharing");
        else f.Ok($"state line while sharing: \"{ready.Name}\"");
        f.CheckNoSurpriseWindows();
        f.Shot("sharing-on");

        f.Toggle(f.Wait("the Share this PC switch", n => Uia.Find(n, ids: ["ShareSwitch"])));
        var off = f.Wait("the 'not sharing' state after switching off", n => Uia.FindContaining(n, text.Get("Share_State_Off")));
        f.Ok($"switching off shows \"{off.Name}\"");
        f.Shot("sharing-off");

        var settings = f.Wait("the Settings entry in the navigation", n => Uia.Find(n, ids: ["SettingsItem"], names: [text.Get("Nav_Settings")]));
        if (settings.Element.Patterns.SelectionItem.IsSupported) settings.Element.Patterns.SelectionItem.Pattern.Select();
        else settings.Element.Click();
        f.Wait("the Settings page", n => Uia.Find(n, names: ["Appearance", "This PC's name"]));
        f.Ok("the Settings page opens (found its Appearance / PC name field)");
        f.CheckNoSurpriseWindows();
        f.Shot("settings");
    }

    private Regex StepRegex(int step) => new("^" + Regex.Escape(text.Format("Onboarding_StepOf", step, 4)) + "$");

    private static JsonObject? ReadSettings()
    {
        try { return JsonNode.Parse(File.ReadAllText(Kind.SettingsPath)) as JsonObject; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }
}
