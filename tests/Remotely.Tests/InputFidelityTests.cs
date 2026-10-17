using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.Tests;

/// <summary>Encoders, decoders, limits and state machines of the input-fidelity and rich-clipboard extension (agent A7). None of this touches Windows.</summary>
public class InputFidelityTests
{
    // ---- input events on the wire ----

    private static InputEvent RoundTrip(InputEvent e)
    {
        Assert.True(InputCodec.TryDecode(InputCodec.Encode([e]), out var decoded));
        return Assert.Single(decoded);
    }

    [Fact]
    public void Text_event_round_trips_for_every_code_unit_kind()
    {
        foreach (var unit in new ushort[] { 'a', 0x00FC, 0x20AC, 0x6F22, 0xD83D, 0xDE00, 0xFFFF })
        {
            var down = RoundTrip(InputEvent.Text(unit, true));
            var up = RoundTrip(InputEvent.Text(unit, false));
            Assert.Equal(InputKind.Text, down.Kind);
            Assert.Equal(unit, down.Arg1);
            Assert.False(down.IsKeyUp);
            Assert.True(up.IsKeyUp);
        }
    }

    [Fact]
    public void Touch_event_round_trips_and_validates_phase_and_pressure()
    {
        var e = RoundTrip(InputEvent.Touch(TouchPhase.Move, 7, 1234, 60000, 800));
        Assert.Equal((InputKind.Touch, (byte)TouchPhase.Move, (ushort)7, (ushort)1234, (ushort)60000, 800), (e.Kind, e.Phase, e.Arg1, e.X, e.Y, e.Pressure));

        var bad = InputCodec.Encode([InputEvent.Touch(TouchPhase.Down, 1, 0, 0)]);
        bad[3 + 1] = 4; // phase 4 does not exist for touch
        Assert.False(InputCodec.TryDecode(bad, out _));
        bad = InputCodec.Encode([InputEvent.Touch(TouchPhase.Down, 1, 0, 0)]);
        bad[3 + 8] = 0xFF; // pressure above 1024
        bad[3 + 9] = 0xFF;
        Assert.False(InputCodec.TryDecode(bad, out _));
    }

    [Fact]
    public void Touch_pressure_is_clamped_when_built()
    {
        Assert.Equal(1024, InputEvent.Touch(TouchPhase.Down, 1, 0, 0, 99999).Pressure);
        Assert.Equal(0, InputEvent.Touch(TouchPhase.Down, 1, 0, 0, -5).Pressure);
    }

    [Fact]
    public void Pen_event_carries_pressure_tilt_and_buttons()
    {
        var e = RoundTrip(InputEvent.Pen(PenPhase.Move, 100, 200, 512, -45, 90, barrel: true, eraser: false, inverted: true));
        Assert.Equal(InputKind.Pen, e.Kind);
        Assert.Equal((byte)PenPhase.Move, e.Phase);
        Assert.Equal(512, e.Pressure);
        Assert.Equal(-45, e.TiltX);
        Assert.Equal(90, e.TiltY);
        Assert.NotEqual(0, e.Flags & InputEvent.PenBarrel);
        Assert.Equal(0, e.Flags & InputEvent.PenEraser);
        Assert.NotEqual(0, e.Flags & InputEvent.PenInverted);
    }

    [Fact]
    public void Pen_tilt_is_clamped_and_extreme_values_survive()
    {
        var e = RoundTrip(InputEvent.Pen(PenPhase.Hover, 0, 0, 0, -500, 500));
        Assert.Equal(-90, e.TiltX);
        Assert.Equal(90, e.TiltY);
    }

    [Fact]
    public void Pen_with_out_of_range_fields_is_rejected()
    {
        var m = InputCodec.Encode([InputEvent.Pen(PenPhase.Down, 1, 1, 10, 0, 0)]);
        m[3 + 1] = 6; // phase 6 does not exist for pen
        Assert.False(InputCodec.TryDecode(m, out _));
        m = InputCodec.Encode([InputEvent.Pen(PenPhase.Down, 1, 1, 10, 0, 0)]);
        m[3 + 8] = 200; // tilt X byte above 180
        Assert.False(InputCodec.TryDecode(m, out _));
        m = InputCodec.Encode([InputEvent.Pen(PenPhase.Down, 1, 1, 10, 0, 0)]);
        m[3 + 6] = 0x7F; // pressure far above 1024
        Assert.False(InputCodec.TryDecode(m, out _));
    }

    [Fact]
    public void Heartbeat_round_trips_and_unknown_kinds_are_still_rejected()
    {
        Assert.Equal(InputKind.Heartbeat, RoundTrip(InputEvent.Heartbeat()).Kind);
        var m = InputCodec.Encode([InputEvent.Heartbeat()]);
        m[3] = 12;
        Assert.False(InputCodec.TryDecode(m, out _));
        m[3] = 0;
        Assert.False(InputCodec.TryDecode(m, out _));
    }

    [Fact]
    public void Original_events_are_unchanged_on_the_wire()
    {
        // Frozen bytes: kind 5 (key down), flags 1 (extended), x 0, y 0, vk 0x25, scan 0x4B.
        byte[] message = [HostMessages.Input, 0, 1, 5, 1, 0, 0, 0, 0, 0, 0x25, 0, 0x4B];
        Assert.True(InputCodec.TryDecode(message, out var events));
        Assert.Equal(InputEvent.Key(true, 0x25, 0x4B, true), Assert.Single(events));
        Assert.Equal(message, InputCodec.Encode(events));
    }

    [Fact]
    public void X_buttons_and_horizontal_wheel_round_trip()
    {
        Assert.Equal(MouseButton.X1, (MouseButton)RoundTrip(InputEvent.Down(MouseButton.X1, 5, 6)).Arg1);
        Assert.Equal(MouseButton.X2, (MouseButton)RoundTrip(InputEvent.Up(MouseButton.X2, 5, 6)).Arg1);
        var wheel = RoundTrip(InputEvent.Scroll(-30, 1, 2, horizontal: true));
        Assert.Equal(-30, wheel.WheelDelta);
        Assert.NotEqual(0, wheel.Flags & InputEvent.FlagHorizontal);
        var m = InputCodec.Encode([InputEvent.Down(MouseButton.Left, 0, 0)]);
        m[3 + 7] = 5; // button 5 does not exist
        Assert.False(InputCodec.TryDecode(m, out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(120, 1)]
    [InlineData(-30, 1)]
    [InlineData(40000, 2)]
    [InlineData(-100000, 4)]
    public void High_resolution_wheel_is_split_without_losing_or_clamping_anything(int delta, int expectedEvents)
    {
        var events = InputEvent.ScrollMany(delta, 1, 2).ToList();
        Assert.Equal(expectedEvents, events.Count);
        Assert.Equal(delta, events.Sum(e => e.WheelDelta));
    }

    [Fact]
    public void Extension_events_are_recognised_so_old_hosts_are_not_sent_them()
    {
        Assert.False(InputCodec.NeedsExtension([InputEvent.Move(1, 1), InputEvent.Key(true, 65, 30, false), new InputEvent(InputKind.ReleaseAll)]));
        Assert.True(InputCodec.NeedsExtension([InputEvent.Move(1, 1), InputEvent.Text('a', true)]));
        Assert.True(InputCodec.NeedsExtension([InputEvent.Heartbeat()]));
    }

    [Fact]
    public void A_full_message_of_extended_events_fits_the_limit_and_one_more_does_not()
    {
        var full = Enumerable.Range(0, InputCodec.MaxEventsPerMessage).Select(i => InputEvent.Text((ushort)(0x41 + i % 26), true)).ToList();
        Assert.True(InputCodec.TryDecode(InputCodec.Encode(full), out var back));
        Assert.Equal(InputCodec.MaxEventsPerMessage, back.Count);
        full.Add(InputEvent.Text('x', true));
        Assert.Throws<ArgumentException>(() => InputCodec.Encode(full));
    }

    // ---- message ids ----

    [Fact]
    public void Message_ids_stay_in_the_reserved_ranges_and_do_not_collide()
    {
        var toHost = new byte[] { FidelityMessages.ToHost.ClipOffer, FidelityMessages.ToHost.ClipChunk, FidelityMessages.ToHost.ClipEnd, FidelityMessages.ToHost.ClipAnswer,
            FidelityMessages.ToHost.ClipCancel, FidelityMessages.ToHost.LayoutQuery, FidelityMessages.ToHost.ClipResult, FidelityMessages.ToHost.ClipSubscribe };
        var toViewer = new byte[] { FidelityMessages.ToViewer.ClipOffer, FidelityMessages.ToViewer.ClipChunk, FidelityMessages.ToViewer.ClipEnd, FidelityMessages.ToViewer.ClipAnswer,
            FidelityMessages.ToViewer.ClipCancel, FidelityMessages.ToViewer.LayoutReply, FidelityMessages.ToViewer.ClipResult };
        Assert.All(toHost, id => Assert.InRange(id, (byte)0x30, (byte)0x3F));
        Assert.All(toViewer, id => Assert.InRange(id, (byte)0xB0, (byte)0xBF));
        Assert.Equal(toHost.Length, toHost.Distinct().Count());
        Assert.Equal(toViewer.Length, toViewer.Distinct().Count());
        var original = typeof(HostMessages).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(byte)).Select(f => (byte)f.GetRawConstantValue()!).ToHashSet();
        Assert.Empty(original.Intersect(toHost));
        Assert.Empty(original.Intersect(toViewer));
        Assert.Equal(64, (int)HostCapabilities.RichClipboard);
    }

    [Fact]
    public void Layout_reply_round_trips_and_rejects_wrong_lengths()
    {
        Assert.True(FidelityMessages.TryReadLayoutReply(FidelityMessages.LayoutReply(0x04090409), out var layout));
        Assert.Equal(0x04090409u, layout);
        Assert.False(FidelityMessages.TryReadLayoutReply([FidelityMessages.ToViewer.LayoutReply, 0, 0, 0], out _));
        Assert.False(FidelityMessages.TryReadLayoutReply([0x81, 0, 0, 0, 0], out _));
    }

    // ---- held input and the watchdog ----

    [Fact]
    public void Tracker_releases_everything_it_saw_pressed()
    {
        var held = new HeldInputTracker();
        held.Apply(InputEvent.Key(true, 0x41, 0x1E, false));
        held.Apply(InputEvent.Key(true, 0x25, 0x4B, true));
        held.Apply(InputEvent.Down(MouseButton.X1, 100, 200));
        held.Apply(InputEvent.Text('x', true));
        held.Apply(InputEvent.Touch(TouchPhase.Down, 3, 10, 20));
        held.Apply(InputEvent.Pen(PenPhase.Down, 30, 40, 500, 0, 0, barrel: true));
        Assert.True(held.AnyHeld);

        var release = held.TakeReleaseEvents();
        Assert.False(held.AnyHeld);
        Assert.Contains(release, e => e.Kind == InputKind.KeyUp && e.Arg1 == 0x41 && e.Arg2 == 0x1E && (e.Flags & InputEvent.FlagExtended) == 0);
        Assert.Contains(release, e => e.Kind == InputKind.KeyUp && e.Arg1 == 0x25 && (e.Flags & InputEvent.FlagExtended) != 0);
        Assert.Contains(release, e => e.Kind == InputKind.MouseUp && (MouseButton)e.Arg1 == MouseButton.X1 && e.X == 100 && e.Y == 200);
        Assert.Contains(release, e => e.Kind == InputKind.Text && e.IsKeyUp && e.Arg1 == 'x');
        Assert.Contains(release, e => e.Kind == InputKind.Touch && e.Phase == (byte)TouchPhase.Cancel && e.Arg1 == 3);
        Assert.Contains(release, e => e.Kind == InputKind.Pen && e.Phase == (byte)PenPhase.Cancel && (e.Flags & InputEvent.PenBarrel) != 0);
        Assert.Empty(held.TakeReleaseEvents());
    }

    [Fact]
    public void Tracker_forgets_what_was_released_normally()
    {
        var held = new HeldInputTracker();
        held.Apply(InputEvent.Key(true, 0x41, 0x1E, false));
        held.Apply(InputEvent.Key(false, 0x41, 0x1E, false));
        held.Apply(InputEvent.Down(MouseButton.Left, 1, 1));
        held.Apply(InputEvent.Up(MouseButton.Left, 1, 1));
        held.Apply(InputEvent.Touch(TouchPhase.Down, 1, 1, 1));
        held.Apply(InputEvent.Touch(TouchPhase.Up, 1, 1, 1));
        held.Apply(InputEvent.Pen(PenPhase.Down, 1, 1, 1, 0, 0));
        held.Apply(InputEvent.Pen(PenPhase.Up, 1, 1, 0, 0, 0));
        Assert.False(held.AnyHeld);
    }

    [Fact]
    public void Pen_hover_is_not_a_held_contact_and_release_all_clears()
    {
        var held = new HeldInputTracker();
        held.Apply(InputEvent.Pen(PenPhase.Hover, 1, 1, 0, 0, 0));
        Assert.False(held.AnyHeld);
        held.Apply(InputEvent.Key(true, 0x10, 0x2A, false));
        held.Apply(new InputEvent(InputKind.ReleaseAll));
        Assert.False(held.AnyHeld);
    }

    [Fact]
    public void Watchdog_only_acts_for_a_viewer_that_sends_heartbeats()
    {
        long now = 1000;
        var dog = new InputWatchdog(() => now, 5000);
        dog.Activity(InputKind.KeyDown);
        now += 60_000;
        Assert.False(dog.IsStale); // an old viewer without heartbeats may hold a button for as long as it likes
        dog.Activity(InputKind.Heartbeat);
        Assert.True(dog.Armed);
        now += 4000;
        Assert.False(dog.IsStale);
        now += 1500;
        Assert.True(dog.IsStale);
        dog.Activity(InputKind.MouseMove);
        Assert.False(dog.IsStale);
    }

    // ---- keyboard encoder ----

    private sealed class Keys
    {
        public readonly List<InputEvent> Events = [];
        public readonly KeyboardEncoder Encoder;
        public Keys(KeyboardMode mode) => Encoder = new KeyboardEncoder(Events.Add) { Mode = mode };

        public string Describe() => string.Join(" ", Events.Select(e => e.Kind switch
        {
            InputKind.KeyDown => $"v{e.Arg1:X2}",
            InputKind.KeyUp => $"^{e.Arg1:X2}",
            InputKind.Text => (e.IsKeyUp ? "^t:" : "t:") + (char)e.Arg1,
            InputKind.ReleaseAll => "release",
            _ => e.Kind.ToString(),
        }));
    }

    [Fact]
    public void Scan_code_mode_forwards_every_key_and_ignores_the_character_that_follows()
    {
        var k = new Keys(KeyboardMode.ScanCode);
        k.Encoder.KeyDown(0x5A, 0x2C, false);
        k.Encoder.Character('z');
        k.Encoder.KeyUp(0x5A, 0x2C, false);
        Assert.Equal("v5A ^5A", k.Describe());
        Assert.Equal((ushort)0x2C, k.Events[0].Arg2);
    }

    [Fact]
    public void Unicode_mode_sends_the_typed_character_not_the_key_code()
    {
        // A German keyboard typing "z" while the host has a US layout: the key code would type "y" there.
        var k = new Keys(KeyboardMode.Unicode);
        k.Encoder.KeyDown(0x59, 0x15, false);
        k.Encoder.Character('z');
        k.Encoder.KeyUp(0x59, 0x15, false);
        Assert.Equal("t:z ^t:z", k.Describe());
    }

    [Fact]
    public void Unicode_mode_still_sends_shortcuts_as_key_codes()
    {
        var k = new Keys(KeyboardMode.Unicode);
        k.Encoder.KeyDown(0x11, 0x1D, false);
        k.Encoder.KeyDown(0x43, 0x2E, false);
        k.Encoder.Character('\u0003'); // Ctrl+C produces a control character, which is never typed
        k.Encoder.KeyUp(0x43, 0x2E, false);
        k.Encoder.KeyUp(0x11, 0x1D, false);
        Assert.Equal("v11 v43 ^43 ^11", k.Describe());
    }

    [Fact]
    public void Windows_key_and_left_alt_combinations_are_key_codes()
    {
        var k = new Keys(KeyboardMode.Unicode);
        k.Encoder.KeyDown(0x5B, 0x5B, true);
        k.Encoder.KeyDown(0x44, 0x20, false);
        k.Encoder.KeyUp(0x44, 0x20, false);
        k.Encoder.KeyUp(0x5B, 0x5B, true);
        k.Encoder.KeyDown(0x12, 0x38, false);
        k.Encoder.KeyDown(0x46, 0x21, false);
        k.Encoder.KeyUp(0x46, 0x21, false);
        k.Encoder.KeyUp(0x12, 0x38, false);
        Assert.Equal("v5B v44 ^44 ^5B v12 v46 ^46 ^12", k.Describe());
    }

    [Fact]
    public void AltGr_characters_are_sent_as_text()
    {
        // AltGr arrives as Ctrl plus the extended (right) Alt. On a German layout AltGr+Q types "@".
        var k = new Keys(KeyboardMode.Unicode);
        k.Encoder.KeyDown(0x11, 0x1D, false);
        k.Encoder.KeyDown(0x12, 0x38, true);
        k.Encoder.KeyDown(0x51, 0x10, false);
        k.Encoder.Character('@');
        k.Encoder.KeyUp(0x51, 0x10, false);
        k.Encoder.KeyUp(0x12, 0x38, true);
        k.Encoder.KeyUp(0x11, 0x1D, false);
        Assert.Equal("v11 v12 t:@ ^t:@ ^12 ^11", k.Describe());
    }

    [Fact]
    public void Dead_key_sends_nothing_until_the_composed_character_arrives()
    {
        // Press the acute accent (dead), release it, then press e: the system composes "é".
        var k = new Keys(KeyboardMode.Unicode);
        k.Encoder.KeyDown(0xDB, 0x0D, false);
        k.Encoder.KeyUp(0xDB, 0x0D, false);
        Assert.Empty(k.Events);
        k.Encoder.KeyDown(0x45, 0x12, false);
        k.Encoder.Character('é');
        k.Encoder.KeyUp(0x45, 0x12, false);
        Assert.Equal("t:é ^t:é", k.Describe());
    }

    [Fact]
    public void Shift_does_not_turn_a_typed_character_into_a_shortcut_and_is_still_sent()
    {
        var k = new Keys(KeyboardMode.Unicode);
        k.Encoder.KeyDown(0x10, 0x2A, false);
        k.Encoder.KeyDown(0x31, 0x02, false);
        k.Encoder.Character('!');
        k.Encoder.KeyUp(0x31, 0x02, false);
        k.Encoder.KeyUp(0x10, 0x2A, false);
        Assert.Equal("v10 t:! ^t:! ^10", k.Describe());
    }

    [Fact]
    public void Navigation_and_editing_keys_are_key_codes_even_though_they_produce_control_characters()
    {
        var k = new Keys(KeyboardMode.Unicode);
        foreach (ushort vk in new ushort[] { 0x0D, 0x08, 0x09, 0x1B, 0x25, 0x70 })
        {
            k.Encoder.KeyDown(vk, 0, false);
            if (vk is 0x0D) k.Encoder.Character('\r');
            if (vk is 0x08) k.Encoder.Character('\b');
            if (vk is 0x09) k.Encoder.Character('\t');
            k.Encoder.KeyUp(vk, 0, false);
        }
        Assert.Equal("v0D ^0D v08 ^08 v09 ^09 v1B ^1B v25 ^25 v70 ^70", k.Describe());
    }

    [Fact]
    public void A_character_with_no_key_is_input_method_text_and_is_sent_in_both_modes()
    {
        foreach (var mode in new[] { KeyboardMode.ScanCode, KeyboardMode.Unicode })
        {
            var k = new Keys(mode);
            k.Encoder.Character('漢');
            k.Encoder.Character('字');
            Assert.Equal("t:漢 ^t:漢 t:字 ^t:字", k.Describe());
        }
    }

    [Fact]
    public void Surrogate_pairs_from_an_emoji_are_sent_unit_by_unit_in_order()
    {
        var k = new Keys(KeyboardMode.Unicode);
        foreach (var unit in "😀") k.Encoder.Character(unit);
        var units = k.Events.Where(e => e.Kind == InputKind.Text && !e.IsKeyUp).Select(e => (char)e.Arg1).ToArray();
        Assert.Equal("😀", new string(units));
    }

    [Fact]
    public void Input_method_keys_and_packets_are_not_forwarded()
    {
        var k = new Keys(KeyboardMode.ScanCode);
        k.Encoder.KeyDown(0xE5, 0, false);
        k.Encoder.KeyUp(0xE5, 0, false);
        k.Encoder.KeyDown(0xE7, 0, false);
        k.Encoder.KeyDown(0, 0, false);
        Assert.Empty(k.Events);
    }

    [Fact]
    public void Held_keys_repeat_and_release_on_focus_loss()
    {
        var k = new Keys(KeyboardMode.ScanCode);
        k.Encoder.KeyDown(0x27, 0x4D, true);
        k.Encoder.KeyDown(0x27, 0x4D, true);
        k.Encoder.KeyDown(0x10, 0x2A, false);
        k.Encoder.ReleaseAll();
        Assert.Equal("v27 v27 v10 ^27 ^10 release", k.Describe());
        Assert.False(k.Encoder.AnyDown);
        k.Encoder.KeyUp(0x27, 0x4D, true); // a late key-up after the focus loss is ignored
        Assert.Equal(6, k.Events.Count);
    }

    [Fact]
    public void Text_can_be_switched_off_for_hosts_that_cannot_take_it()
    {
        var k = new Keys(KeyboardMode.Unicode);
        k.Encoder.TextEnabled = false;
        k.Encoder.KeyDown(0x41, 0x1E, false);
        k.Encoder.Character('a');
        k.Encoder.KeyUp(0x41, 0x1E, false);
        Assert.Equal("v41 ^41", k.Describe());
    }

    [Fact]
    public void Auto_mode_picks_text_unless_both_layouts_are_known_and_equal()
    {
        const uint de = 0x04070407, us = 0x04090409;
        Assert.Equal(KeyboardMode.ScanCode, KeyboardModeChooser.Resolve(KeyboardModeSetting.Auto, us, us, true));
        Assert.Equal(KeyboardMode.Unicode, KeyboardModeChooser.Resolve(KeyboardModeSetting.Auto, de, us, true));
        Assert.Equal(KeyboardMode.Unicode, KeyboardModeChooser.Resolve(KeyboardModeSetting.Auto, us, 0, true));
        Assert.Equal(KeyboardMode.ScanCode, KeyboardModeChooser.Resolve(KeyboardModeSetting.Auto, de, us, false));
        Assert.Equal(KeyboardMode.ScanCode, KeyboardModeChooser.Resolve(KeyboardModeSetting.Unicode, de, us, false));
        Assert.Equal(KeyboardMode.ScanCode, KeyboardModeChooser.Resolve(KeyboardModeSetting.ScanCode, de, us, true));
        Assert.Equal(KeyboardMode.Unicode, KeyboardModeChooser.Resolve(KeyboardModeSetting.Unicode, us, us, true));
    }

    // ---- clipboard bundles, files, html, limits ----

    private static ClipBundle Bundle(string text = "hello", string? html = "<b>hi</b>", int pngBytes = 100, ClipFile[]? files = null)
    {
        var items = new List<ClipItem>();
        if (text.Length > 0) items.Add(new ClipItem(ClipItemKind.Text, Encoding.UTF8.GetBytes(text)));
        if (html is not null) items.Add(new ClipItem(ClipItemKind.Html, Encoding.UTF8.GetBytes(html)));
        if (pngBytes > 0) items.Add(new ClipItem(ClipItemKind.Png, RandomNumberGenerator.GetBytes(pngBytes)));
        if (files is not null) items.Add(new ClipItem(ClipItemKind.Files, ClipFiles.Encode(files)));
        return new ClipBundle(items);
    }

    [Fact]
    public void Fingerprint_is_stable_order_independent_and_sensitive_to_content()
    {
        var a = new ClipBundle([new ClipItem(ClipItemKind.Text, [1, 2]), new ClipItem(ClipItemKind.Html, [3])]);
        var b = new ClipBundle([new ClipItem(ClipItemKind.Html, [3]), new ClipItem(ClipItemKind.Text, [1, 2])]);
        var c = new ClipBundle([new ClipItem(ClipItemKind.Text, [1, 2]), new ClipItem(ClipItemKind.Html, [4])]);
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
        Assert.NotEqual(a.Fingerprint(), c.Fingerprint());
        Assert.False(ClipBundle.FromText("x").HasRich);
        Assert.True(a.HasRich);
    }

    [Fact]
    public void Files_round_trip_and_names_lose_path_parts_and_become_unique()
    {
        var packed = ClipFiles.Encode([new ClipFile("a.txt", [1, 2, 3]), new ClipFile("..\\..\\evil.dll", [9]), new ClipFile("A.TXT", [4]), new ClipFile("é ñ 漢.txt", [])]);
        Assert.True(ClipFiles.TryDecode(packed, ClipLimits.Default, out var files, out var error), error);
        Assert.Equal(["a.txt", "evil.dll", "A (1).TXT", "é ñ 漢.txt"], files.Select(f => f.Name).ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, files[0].Content);
        Assert.Equal(new byte[] { 9 }, files[1].Content);
        Assert.Empty(files[3].Content);
        Assert.Equal(4, files.Select(f => f.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Malformed_file_lists_are_rejected()
    {
        var good = ClipFiles.Encode([new ClipFile("a.txt", [1, 2, 3])]);
        Assert.False(ClipFiles.TryDecode(good[..^1], ClipLimits.Default, out _, out _));       // content too short
        Assert.False(ClipFiles.TryDecode([.. good, 0], ClipLimits.Default, out _, out _));     // content too long
        Assert.False(ClipFiles.TryDecode([0, 0], ClipLimits.Default, out _, out _));           // zero files
        Assert.False(ClipFiles.TryDecode([0], ClipLimits.Default, out _, out _));              // no count
        Assert.False(ClipFiles.TryDecode([0, 1, 0xFF, 0xFF], ClipLimits.Default, out _, out _)); // name longer than the data
        var many = Enumerable.Range(0, 65).Select(i => new ClipFile($"f{i}", [])).ToList();
        Assert.False(ClipFiles.TryDecode(ClipFiles.Encode(many), ClipLimits.Default, out _, out var why));
        Assert.Contains("Too many", why);
        var big = ClipFiles.Encode([new ClipFile("x", new byte[2000])]);
        Assert.False(ClipFiles.TryDecode(big, ClipLimits.Default with { MaxFilesBytes = 1000 }, out _, out _));
    }

    [Fact]
    public void Html_is_wrapped_in_cf_html_with_correct_byte_offsets_and_extracted_again()
    {
        const string fragment = "<b>Grüße €</b> 漢";
        var cf = ClipHtml.BuildCfHtml(fragment);
        var text = Encoding.UTF8.GetString(cf);
        Assert.StartsWith("Version:0.9\r\nStartHTML:", text);
        int Offset(string key) => int.Parse(text.Split(key)[1][..10]);
        Assert.Equal(fragment, Encoding.UTF8.GetString(cf, Offset("StartFragment:"), Offset("EndFragment:") - Offset("StartFragment:")));
        Assert.Equal(cf.Length, Offset("EndHTML:"));
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(cf, Offset("StartHTML:"), 6));
        Assert.Equal(fragment, ClipHtml.ExtractFragment(cf));
        Assert.Equal(fragment, ClipHtml.ExtractFragment([.. cf, 0, 0])); // Windows pads with NULs
    }

    [Fact]
    public void Html_extraction_survives_missing_or_wrong_offsets()
    {
        Assert.Equal("<i>x</i>", ClipHtml.ExtractFragment(Encoding.UTF8.GetBytes("<html><!--StartFragment--><i>x</i><!--EndFragment--></html>")));
        Assert.Equal("<p>plain</p>", ClipHtml.ExtractFragment(Encoding.UTF8.GetBytes("Version:0.9\r\n<p>plain</p>")));
        Assert.Equal("<p>t</p>", ClipHtml.ExtractFragment(Encoding.UTF8.GetBytes("StartFragment:9999999999\r\nEndFragment:0000000001\r\n<!--StartFragment--><p>t</p><!--EndFragment-->")));
    }

    [Fact]
    public void Limits_refuse_oversize_duplicate_and_unknown_parts()
    {
        var limits = ClipLimits.Default;
        Assert.Null(limits.Check(Bundle(files: [new ClipFile("a", [1])])));
        Assert.NotNull(limits.Check([(ClipItemKind.Png, limits.MaxPngBytes + 1L)]));
        Assert.NotNull(limits.Check([(ClipItemKind.Html, limits.MaxHtmlBytes + 1L)]));
        Assert.NotNull(limits.Check([(ClipItemKind.Text, limits.MaxTextBytes + 1L)]));
        Assert.NotNull(limits.Check([(ClipItemKind.Files, limits.MaxFilesBytes + 1L)]));
        Assert.NotNull(limits.Check([(ClipItemKind.Text, 1), (ClipItemKind.Text, 1)]));
        Assert.NotNull(limits.Check([((ClipItemKind)99, 1)]));
        Assert.NotNull(limits.Check([]));
        Assert.NotNull(limits.Check([(ClipItemKind.Png, limits.MaxPngBytes), (ClipItemKind.Files, limits.MaxFilesBytes), (ClipItemKind.Html, limits.MaxHtmlBytes), (ClipItemKind.Text, limits.MaxTextBytes)])); // together over the total
        Assert.False(limits.NeedsConfirmation(limits.ConfirmAboveBytes));
        Assert.True(limits.NeedsConfirmation(limits.ConfirmAboveBytes + 1));
        Assert.Equal(HostMessages.MaxClipboardChars * 4, limits.MaxTextBytes); // same ceiling as the original text path
    }

    [Fact]
    public void Offer_and_reply_messages_round_trip_and_reject_malformed_input()
    {
        var items = new[] { new ClipItem(ClipItemKind.Text, new byte[10]), new ClipItem(ClipItemKind.Png, new byte[70000]) };
        var offer = ClipCodec.EncodeOffer(ClipWire.ViewerToHost.Offer, 42, items);
        Assert.True(ClipCodec.TryDecodeOffer(offer, out var info));
        Assert.Equal(42u, info.Id);
        Assert.Equal(70010, info.TotalBytes);
        Assert.Equal([(ClipItemKind.Text, 10L), (ClipItemKind.Png, 70000L)], info.Items);
        Assert.False(ClipCodec.TryDecodeOffer(offer[..^1], out _));
        Assert.False(ClipCodec.TryDecodeOffer(offer[..6], out _));
        offer[6] = 0;
        Assert.False(ClipCodec.TryDecodeOffer(offer, out _));

        var reply = ClipCodec.EncodeReply(ClipWire.ViewerToHost.Answer, 7, false, "Zu groß");
        Assert.True(ClipCodec.TryDecodeReply(reply, out var id, out var ok, out var reason));
        Assert.Equal((7u, false, "Zu groß"), (id, ok, reason));
        Assert.False(ClipCodec.TryDecodeReply(reply[..^1], out _, out _, out _));
        var long200 = ClipCodec.EncodeReply(ClipWire.ViewerToHost.Result, 1, true, new string('x', 500));
        Assert.True(ClipCodec.TryDecodeReply(long200, out _, out _, out var truncated));
        Assert.Equal(200, truncated.Length);
    }

    // ---- the transfer protocol, two peers over in-memory channels ----

    private sealed class Link : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Channel<byte[]> _toHost = Channel.CreateUnbounded<byte[]>(), _toViewer = Channel.CreateUnbounded<byte[]>();
        private readonly List<Task> _pumps = [];
        public readonly List<ClipBundle> HostReceived = [], ViewerReceived = [];
        public readonly List<byte[]> Traffic = [];
        public Func<byte[], byte[]?>? Tamper;
        public readonly ClipPeer Host, Viewer;

        public Link(ClipLimits? limits = null)
        {
            Viewer = new ClipPeer(ClipWire.ViewerToHost, ClipWire.HostToViewer, (m, _) => Send(_toHost, m), (b, _) => { lock (ViewerReceived) ViewerReceived.Add(b); return Task.CompletedTask; }, limits);
            Host = new ClipPeer(ClipWire.HostToViewer, ClipWire.ViewerToHost, (m, _) => Send(_toViewer, m), (b, _) => { lock (HostReceived) HostReceived.Add(b); return Task.CompletedTask; }, limits);
            _pumps.Add(Pump(_toHost, Host));
            _pumps.Add(Pump(_toViewer, Viewer));
        }

        private Task Send(Channel<byte[]> to, byte[] message)
        {
            lock (Traffic) Traffic.Add(message);
            var m = Tamper?.Invoke(message) ?? message;
            return to.Writer.WriteAsync(m).AsTask();
        }

        private async Task Pump(Channel<byte[]> from, ClipPeer to)
        {
            try
            {
                await foreach (var m in from.Reader.ReadAllAsync(_cts.Token))
                {
                    Assert.True(to.Handles(m[0]), $"peer was handed message {m[0]:X2} it does not handle");
                    await to.HandleAsync(m, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _toHost.Writer.TryComplete();
            _toViewer.Writer.TryComplete();
            await Task.WhenAll(_pumps).WaitAsync(TimeSpan.FromSeconds(10));
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Small_rich_bundle_travels_both_ways_and_arrives_byte_identical()
    {
        await using var link = new Link();
        var files = new[] { new ClipFile("note.txt", Encoding.UTF8.GetBytes("hi")), new ClipFile("b.bin", RandomNumberGenerator.GetBytes(600_000)) };
        var toHost = Bundle("Grüße", "<b>x</b>", 1_300_000, files);
        var outcome = await link.Viewer.SendAsync(toHost, CancellationToken.None);
        Assert.True(outcome.Ok, outcome.Message);
        var received = Assert.Single(link.HostReceived);
        Assert.Equal(toHost.Fingerprint(), received.Fingerprint());
        Assert.Equal("Grüße", received.Text);
        Assert.Equal(toHost.Get(ClipItemKind.Png)!.Data, received.Get(ClipItemKind.Png)!.Data);

        var toViewer = Bundle("zurück", null, 5000);
        Assert.True((await link.Host.SendAsync(toViewer, CancellationToken.None)).Ok);
        Assert.Equal(toViewer.Fingerprint(), Assert.Single(link.ViewerReceived).Fingerprint());
        // The 1.3 MB image needed three chunks of at most 512 KB.
        Assert.True(link.Traffic.Count(m => m[0] == ClipWire.ViewerToHost.Chunk) >= 4);
        Assert.All(link.Traffic.Where(m => m[0] == ClipWire.ViewerToHost.Chunk), m => Assert.True(m.Length <= 5 + ClipCodec.ChunkBytes));
    }

    [Fact]
    public async Task A_receiver_with_clipboard_sync_off_refuses_before_any_data_is_sent()
    {
        await using var link = new Link();
        link.Host.AcceptIncoming = false;
        link.Host.UnavailableReason = "Clipboard sync is off.";
        var outcome = await link.Viewer.SendAsync(Bundle(), CancellationToken.None);
        Assert.Equal(ClipSendStatus.Refused, outcome.Status);
        Assert.Equal("Clipboard sync is off.", outcome.Message);
        Assert.DoesNotContain(link.Traffic, m => m[0] == ClipWire.ViewerToHost.Chunk);
        Assert.Empty(link.HostReceived);
    }

    [Fact]
    public async Task Oversize_bundles_are_refused_locally_and_by_the_receiver()
    {
        await using var link = new Link();
        var tooBig = new ClipBundle([new ClipItem(ClipItemKind.Png, new byte[ClipLimits.Default.MaxPngBytes + 1])]);
        Assert.Equal(ClipSendStatus.Refused, (await link.Viewer.SendAsync(tooBig, CancellationToken.None)).Status);
        Assert.Empty(link.Traffic); // not even an offer

        // A sender with looser limits than the receiver is still stopped by the receiver.
        var sent = new List<byte[]>();
        var receiver = new ClipPeer(ClipWire.HostToViewer, ClipWire.ViewerToHost, (m, _) => { sent.Add(m); return Task.CompletedTask; }, (_, _) => Task.CompletedTask, ClipLimits.Default with { MaxPngBytes = 10 });
        await receiver.HandleAsync(ClipCodec.EncodeOffer(ClipWire.ViewerToHost.Offer, 1, [new ClipItem(ClipItemKind.Png, new byte[11])]), CancellationToken.None);
        Assert.True(ClipCodec.TryDecodeReply(Assert.Single(sent), out _, out var ok, out var reason));
        Assert.False(ok);
        Assert.Contains("too large", reason);
    }

    [Fact]
    public async Task Large_transfers_need_the_receivers_confirmation()
    {
        var limits = ClipLimits.Default with { ConfirmAboveBytes = 1000 };
        var big = Bundle("t", null, 5000);

        await using (var none = new Link(limits))
        {
            var refused = await none.Viewer.SendAsync(big, CancellationToken.None);
            Assert.Equal(ClipSendStatus.Refused, refused.Status);
            Assert.Contains("confirmation", refused.Message);
            Assert.Empty(none.HostReceived);
        }

        await using (var no = new Link(limits))
        {
            ClipOfferInfo? asked = null;
            no.Host.ConfirmReceive = info => { asked = info; return Task.FromResult(false); };
            var declined = await no.Viewer.SendAsync(big, CancellationToken.None);
            Assert.Equal(ClipSendStatus.Refused, declined.Status);
            Assert.Equal(5001, asked!.TotalBytes);
            Assert.True(asked.Has(ClipItemKind.Png));
            Assert.Empty(no.HostReceived);
        }

        await using (var yes = new Link(limits))
        {
            var gate = new TaskCompletionSource<bool>();
            yes.Host.ConfirmReceive = _ => gate.Task;
            var sending = yes.Viewer.SendAsync(big, CancellationToken.None);
            await Task.Delay(200);
            Assert.False(sending.IsCompleted); // waiting for the person
            gate.SetResult(true);
            Assert.True((await sending).Ok);
            Assert.Equal(big.Fingerprint(), Assert.Single(yes.HostReceived).Fingerprint());
        }

        await using (var small = new Link(limits))
        {
            small.Host.ConfirmReceive = _ => throw new InvalidOperationException("must not be asked for a small transfer");
            Assert.True((await small.Viewer.SendAsync(Bundle("a", "<i>b</i>", 0), CancellationToken.None)).Ok);
        }
    }

    [Fact]
    public async Task The_sender_can_ask_its_own_person_before_sending_a_large_bundle()
    {
        var limits = ClipLimits.Default with { ConfirmAboveBytes = 1000 };
        await using var link = new Link(limits);
        link.Host.ConfirmReceive = _ => Task.FromResult(true);
        link.Viewer.ConfirmSend = _ => Task.FromResult(false);
        var outcome = await link.Viewer.SendAsync(Bundle("t", null, 5000), CancellationToken.None);
        Assert.Equal(ClipSendStatus.Declined, outcome.Status);
        Assert.Empty(link.Traffic);
    }

    [Fact]
    public async Task Damaged_data_is_never_applied_and_the_sender_is_told()
    {
        await using var link = new Link();
        link.Tamper = m => { if (m[0] == ClipWire.ViewerToHost.Chunk && m.Length > 10) { var c = (byte[])m.Clone(); c[^1] ^= 0xFF; return c; } return null; };
        var outcome = await link.Viewer.SendAsync(Bundle("a", null, 100), CancellationToken.None);
        Assert.Equal(ClipSendStatus.Failed, outcome.Status);
        Assert.Contains("damaged", outcome.Message);
        Assert.Empty(link.HostReceived);
    }

    [Fact]
    public async Task A_chunk_beyond_the_announced_size_and_a_stray_chunk_are_ignored()
    {
        var applied = new List<ClipBundle>();
        var replies = new List<byte[]>();
        var peer = new ClipPeer(ClipWire.HostToViewer, ClipWire.ViewerToHost, (m, _) => { replies.Add(m); return Task.CompletedTask; }, (b, _) => { applied.Add(b); return Task.CompletedTask; });
        var problems = new List<string>();
        peer.Problem += problems.Add;
        await peer.HandleAsync(HostMessages.WithId(ClipWire.ViewerToHost.Chunk, 5, [1, 2, 3]), CancellationToken.None); // no offer yet
        await peer.HandleAsync(ClipCodec.EncodeOffer(ClipWire.ViewerToHost.Offer, 9, [new ClipItem(ClipItemKind.Text, new byte[4])]), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeChunk(ClipWire.ViewerToHost.Chunk, 9, new byte[10]), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeEnd(ClipWire.ViewerToHost.End, 9, new byte[32]), CancellationToken.None);
        Assert.Empty(applied);
        Assert.Single(problems);
        Assert.Single(replies); // only the answer to the offer; the transfer was dropped, nothing was applied
    }

    [Fact]
    public async Task A_new_offer_replaces_an_unfinished_transfer_and_cancel_drops_it()
    {
        var applied = new List<ClipBundle>();
        var peer = new ClipPeer(ClipWire.HostToViewer, ClipWire.ViewerToHost, (_, _) => Task.CompletedTask, (b, _) => { applied.Add(b); return Task.CompletedTask; });
        var data = new byte[] { 1, 2, 3 };
        await peer.HandleAsync(ClipCodec.EncodeOffer(ClipWire.ViewerToHost.Offer, 1, [new ClipItem(ClipItemKind.Png, data)]), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeChunk(ClipWire.ViewerToHost.Chunk, 1, data[..2]), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeOffer(ClipWire.ViewerToHost.Offer, 2, [new ClipItem(ClipItemKind.Png, data)]), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeChunk(ClipWire.ViewerToHost.Chunk, 1, data[2..]), CancellationToken.None); // old transfer: ignored
        await peer.HandleAsync(ClipCodec.EncodeEnd(ClipWire.ViewerToHost.End, 1, SHA256.HashData(data)), CancellationToken.None);
        Assert.Empty(applied);
        await peer.HandleAsync(ClipCodec.EncodeChunk(ClipWire.ViewerToHost.Chunk, 2, data), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeCancel(ClipWire.ViewerToHost.Cancel, 2), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeEnd(ClipWire.ViewerToHost.End, 2, SHA256.HashData(data)), CancellationToken.None);
        Assert.Empty(applied);
        await peer.HandleAsync(ClipCodec.EncodeOffer(ClipWire.ViewerToHost.Offer, 3, [new ClipItem(ClipItemKind.Png, data)]), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeChunk(ClipWire.ViewerToHost.Chunk, 3, data), CancellationToken.None);
        await peer.HandleAsync(ClipCodec.EncodeEnd(ClipWire.ViewerToHost.End, 3, SHA256.HashData(data)), CancellationToken.None);
        Assert.Single(applied);
    }

    [Fact]
    public async Task What_was_just_received_is_not_sent_back_but_a_later_copy_of_the_same_thing_is()
    {
        await using var link = new Link();
        var bundle = Bundle("loop", "<b>l</b>", 300);
        Assert.True((await link.Viewer.SendAsync(bundle, CancellationToken.None)).Ok);
        // The host applied it; its clipboard watcher sees that very content change and offers it back.
        var echo = await link.Host.SendAsync(bundle, CancellationToken.None);
        Assert.Equal(ClipSendStatus.SkippedEcho, echo.Status);
        Assert.Empty(link.ViewerReceived);

        // Something else is copied on the host, then the original content again: that is a real copy and travels.
        Assert.True((await link.Host.SendAsync(Bundle("other", null, 0), CancellationToken.None)).Ok);
        Assert.True((await link.Host.SendAsync(bundle, CancellationToken.None)).Ok);
        Assert.Equal(2, link.ViewerReceived.Count);
    }

    [Fact]
    public async Task A_silent_receiver_makes_the_send_fail_instead_of_hanging()
    {
        var sent = new List<byte[]>();
        var peer = new ClipPeer(ClipWire.ViewerToHost, ClipWire.HostToViewer, (m, _) => { sent.Add(m); return Task.CompletedTask; }, (_, _) => Task.CompletedTask)
        { AnswerTimeout = TimeSpan.FromMilliseconds(200) };
        var outcome = await peer.SendAsync(Bundle(), CancellationToken.None);
        Assert.Equal(ClipSendStatus.Failed, outcome.Status);
        Assert.Equal(ClipWire.ViewerToHost.Cancel, sent[^1][0]);
    }

    [Fact]
    public async Task Files_with_hostile_names_cannot_escape_through_the_peer()
    {
        await using var link = new Link();
        var hostile = Bundle("", null, 0, [new ClipFile("..\\..\\Windows\\evil.exe", [1]), new ClipFile("C:\\x\\y.txt", [2])]);
        Assert.True((await link.Viewer.SendAsync(hostile, CancellationToken.None)).Ok);
        var received = Assert.Single(link.HostReceived);
        Assert.True(ClipFiles.TryDecode(received.Get(ClipItemKind.Files)!.Data, ClipLimits.Default, out var files, out _));
        Assert.Equal(["evil.exe", "y.txt"], files.Select(f => f.Name).ToArray());
    }

    // ---- host side of the extension, with fakes ----

    private sealed class FakeRichClipboard : IClipboardSync, IRichClipboardSync
    {
        private long _sequence = 1;
        public ClipBundle? Current;
        public readonly List<ClipBundle> Written = [];
        public Task<(long, string?)> ReadAsync(CancellationToken ct) => Task.FromResult((_sequence, Current?.Text));
        public Task<long> WriteAsync(string text, CancellationToken ct) => Task.FromResult(++_sequence);
        public void Copy(ClipBundle b) { lock (this) { Current = b; _sequence++; } }
        public Task<ClipReadResult> ReadBundleAsync(long known, ClipLimits limits, CancellationToken ct)
        {
            lock (this) return Task.FromResult(_sequence == known ? new ClipReadResult(_sequence, null) : new ClipReadResult(_sequence, Current));
        }
        public Task<long> WriteBundleAsync(ClipBundle b, CancellationToken ct)
        {
            lock (this) { Written.Add(b); Current = b; return Task.FromResult(++_sequence); }
        }
    }

    private sealed class FakeInput : IInputSink, IHostKeyboardInfo
    {
        public readonly List<InputEvent> Events = [];
        public uint Layout = 0x04070407;
        public Task InjectAsync(IReadOnlyList<InputEvent> events, CancellationToken ct) { lock (Events) Events.AddRange(events); return Task.CompletedTask; }
        public Task<uint> GetLayoutAsync(CancellationToken ct) => Task.FromResult(Layout);
        public InputEvent[] Snapshot() { lock (Events) return [.. Events]; }
    }

    private sealed class FakeFrames : IFrameSource
    {
        public Task<FrameInfo> CaptureAsync(CancellationToken ct) => Task.FromResult(new FrameInfo(1, 1, "Default", [1]));
    }

    private static HostServices Services(IInputSink? input, IClipboardSync? clipboard) => new(new FakeFrames())
    {
        Input = input,
        Clipboard = clipboard,
        IsTrusted = _ => true,
        CanControl = _ => true,
    };

    private static async Task<T> Eventually<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(20);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    [Fact]
    public void Host_advertises_the_extension_when_it_can_inject_input_or_has_a_rich_clipboard()
    {
        Assert.Equal(HostCapabilities.None, FidelityHostSession.Advertise(Services(null, null)));
        Assert.Equal(HostCapabilities.None, FidelityHostSession.Advertise(Services(null, new HostCoreTests.FakeClipboard())));
        Assert.Equal(HostCapabilities.RichClipboard, FidelityHostSession.Advertise(Services(new FakeInput(), null)));
        Assert.Equal(HostCapabilities.RichClipboard, FidelityHostSession.Advertise(Services(null, new FakeRichClipboard())));
    }

    [Fact]
    public async Task Host_answers_layout_queries_and_says_unknown_when_it_cannot_tell()
    {
        var sent = new List<byte[]>();
        using var known = new FidelityHostSession(Services(new FakeInput { Layout = 0x04090409 }, null), (m, _) => { sent.Add(m); return Task.CompletedTask; }, CancellationToken.None, _ => { });
        await known.HandleAsync([FidelityMessages.ToHost.LayoutQuery], CancellationToken.None);
        Assert.True(FidelityMessages.TryReadLayoutReply(sent[0], out var layout));
        Assert.Equal(0x04090409u, layout);

        sent.Clear();
        using var unknown = new FidelityHostSession(Services(new HostCoreTests.RecordingInput(), null), (m, _) => { sent.Add(m); return Task.CompletedTask; }, CancellationToken.None, _ => { });
        await unknown.HandleAsync([FidelityMessages.ToHost.LayoutQuery], CancellationToken.None);
        Assert.True(FidelityMessages.TryReadLayoutReply(sent[0], out layout));
        Assert.Equal(0u, layout);
    }

    [Fact]
    public async Task Host_session_releases_exactly_what_the_viewer_held_when_the_session_ends()
    {
        var input = new FakeInput();
        using var cts = new CancellationTokenSource();
        using var host = new FidelityHostSession(Services(input, null), (_, _) => Task.CompletedTask, cts.Token, _ => { });
        var passed = host.Observe([InputEvent.Key(true, 0x41, 0x1E, false), InputEvent.Key(true, 0x42, 0x30, false), InputEvent.Key(false, 0x42, 0x30, false), InputEvent.Down(MouseButton.Left, 5, 6)]);
        Assert.Equal(4, passed.Count);
        cts.Cancel();
        var released = await Eventually(() => input.Snapshot() is { Length: > 0 } s ? s : null, "key and button releases after the session ended");
        Assert.Equal(2, released.Length);
        Assert.Contains(released, e => e.Kind == InputKind.KeyUp && e.Arg1 == 0x41);
        Assert.Contains(released, e => e.Kind == InputKind.MouseUp && (MouseButton)e.Arg1 == MouseButton.Left);

        var quiet = new FakeInput();
        using var cts2 = new CancellationTokenSource();
        using var silent = new FidelityHostSession(Services(quiet, null), (_, _) => Task.CompletedTask, cts2.Token, _ => { });
        silent.Observe([InputEvent.Key(true, 0x41, 0x1E, false), InputEvent.Key(false, 0x41, 0x1E, false)]);
        cts2.Cancel();
        await Task.Delay(300);
        Assert.Empty(quiet.Snapshot()); // nothing was held: nothing is injected
    }

    [Fact]
    public void Heartbeats_are_consumed_by_the_host_and_never_reach_the_input_sink()
    {
        using var host = new FidelityHostSession(Services(new FakeInput(), null), (_, _) => Task.CompletedTask, CancellationToken.None, _ => { });
        var passed = host.Observe([InputEvent.Heartbeat(), InputEvent.Move(1, 2), InputEvent.Heartbeat()]);
        Assert.Equal(InputKind.MouseMove, Assert.Single(passed).Kind);
    }

    [Fact]
    public async Task A_viewer_that_sends_heartbeats_and_then_goes_silent_has_its_input_released()
    {
        long now = 0;
        var input = new FakeInput();
        var log = new List<string>();
        using var cts = new CancellationTokenSource();
        using var host = new FidelityHostSession(Services(input, null), (_, _) => Task.CompletedTask, cts.Token, log.Add, () => Interlocked.Read(ref now), TimeSpan.FromMilliseconds(20));
        host.Observe([InputEvent.Heartbeat(), InputEvent.Key(true, 0x41, 0x1E, false)]);
        await Task.Delay(150);
        Assert.Empty(input.Snapshot()); // still within the grace period
        Interlocked.Add(ref now, 6000);
        var released = await Eventually(() => input.Snapshot() is { Length: > 0 } s ? s : null, "release after silence");
        Assert.Equal(InputKind.KeyUp, Assert.Single(released).Kind);
        Assert.Contains(log, l => l.Contains("silent"));
        cts.Cancel();
    }

    [Fact]
    public async Task A_viewer_without_heartbeats_is_never_released_for_being_quiet()
    {
        long now = 0;
        var input = new FakeInput();
        using var cts = new CancellationTokenSource();
        using var host = new FidelityHostSession(Services(input, null), (_, _) => Task.CompletedTask, cts.Token, _ => { }, () => Interlocked.Read(ref now), TimeSpan.FromMilliseconds(20));
        host.Observe([InputEvent.Down(MouseButton.Left, 1, 1)]);
        Interlocked.Add(ref now, 600_000);
        await Task.Delay(300);
        Assert.Empty(input.Snapshot()); // an older viewer may drag with the button held and the mouse still
        cts.Cancel();
    }

    [Fact]
    public async Task A_viewer_that_keeps_sending_heartbeats_while_holding_is_left_alone()
    {
        long now = 0;
        var input = new FakeInput();
        using var cts = new CancellationTokenSource();
        using var host = new FidelityHostSession(Services(input, null), (_, _) => Task.CompletedTask, cts.Token, _ => { }, () => Interlocked.Read(ref now), TimeSpan.FromMilliseconds(20));
        host.Observe([InputEvent.Heartbeat(), InputEvent.Key(true, 0x41, 0x1E, false)]);
        for (var i = 0; i < 10; i++)
        {
            Interlocked.Add(ref now, 1000);
            host.Observe([InputEvent.Heartbeat()]);
            await Task.Delay(30);
        }
        Assert.Empty(input.Snapshot());
        cts.Cancel();
    }

    [Fact]
    public async Task Host_offers_new_rich_copies_to_a_subscribed_viewer_and_does_not_echo_what_it_applied()
    {
        var clipboard = new FakeRichClipboard();
        var viewerReceived = new List<ClipBundle>();
        var toViewer = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        FidelityHostSession? host = null;
        var viewer = new ClipPeer(ClipWire.ViewerToHost, ClipWire.HostToViewer, (m, ct) => host!.HandleAsync(m, ct), (b, _) => { lock (viewerReceived) viewerReceived.Add(b); return Task.CompletedTask; })
        { AcceptIncoming = true };
        host = new FidelityHostSession(Services(null, clipboard), (m, ct) =>
        {
            lock (toViewer) toViewer.Add(m);
            return Task.Run(() => viewer.HandleAsync(m, ct), ct); // delivered on another thread, like the network would
        }, cts.Token, _ => { });

        // Nothing is offered before the viewer subscribes.
        clipboard.Copy(Bundle("first", "<b>1</b>", 0));
        await Task.Delay(FidelityHostSession.ClipboardPollInterval * 2);
        Assert.Empty(viewerReceived);

        await host.HandleAsync([FidelityMessages.ToHost.ClipSubscribe, 1], cts.Token);
        var first = await Eventually(() => { lock (viewerReceived) return viewerReceived.FirstOrDefault(); }, "the first rich copy");
        Assert.Equal("first", first.Text);

        // The viewer sends something rich to the host; the host writes it and must not offer it back.
        var fromViewer = Bundle("from viewer", null, 200);
        Assert.True((await viewer.SendAsync(fromViewer, cts.Token)).Ok);
        Assert.Equal(fromViewer.Fingerprint(), Assert.Single(clipboard.Written).Fingerprint());
        await Task.Delay(FidelityHostSession.ClipboardPollInterval * 3);
        lock (viewerReceived) Assert.Single(viewerReceived);

        // A genuinely new copy on the host is offered.
        clipboard.Copy(Bundle("second", "<i>2</i>", 0));
        await Eventually(() => { lock (viewerReceived) return viewerReceived.Count == 2 ? viewerReceived : null; }, "the second rich copy");

        // Plain text alone is left to the original text path.
        clipboard.Copy(ClipBundle.FromText("plain"));
        await Task.Delay(FidelityHostSession.ClipboardPollInterval * 3);
        lock (viewerReceived) Assert.Equal(2, viewerReceived.Count);

        // After unsubscribing nothing more is offered.
        await host.HandleAsync([FidelityMessages.ToHost.ClipSubscribe, 0], cts.Token);
        await Task.Delay(FidelityHostSession.ClipboardPollInterval);
        clipboard.Copy(Bundle("third", "<u>3</u>", 0));
        await Task.Delay(FidelityHostSession.ClipboardPollInterval * 3);
        lock (viewerReceived) Assert.Equal(2, viewerReceived.Count);
        cts.Cancel();
        host.Dispose();
    }

    [Fact]
    public async Task A_host_without_a_rich_clipboard_refuses_offers_politely()
    {
        var sent = new List<byte[]>();
        using var host = new FidelityHostSession(Services(new FakeInput(), new HostCoreTests.FakeClipboard()), (m, _) => { sent.Add(m); return Task.CompletedTask; }, CancellationToken.None, _ => { });
        await host.HandleAsync(ClipCodec.EncodeOffer(ClipWire.ViewerToHost.Offer, 1, [new ClipItem(ClipItemKind.Png, [1])]), CancellationToken.None);
        Assert.True(ClipCodec.TryDecodeReply(Assert.Single(sent), out _, out var ok, out var reason));
        Assert.False(ok);
        Assert.NotEmpty(reason);
    }
}
