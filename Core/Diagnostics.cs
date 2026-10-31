using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ZerkSense5.Games;

namespace ZerkSense5;

static class Diagnostics
{
    public static int Probe()
    {
        var info = DualSenseBt.Find();
        if (info == null)
        {
            var any = HidNative.Enumerate(UsbDescriptors.VendorId);
            Log.Error("No DualSense connected over Bluetooth. Sony HID devices present: " +
                      (any.Count == 0 ? "none" : string.Join(", ", any.Select(d => $"PID {d.ProductId:X4} in={d.InputLength}"))));
            return 1;
        }
        Log.Info($"Found PID {info.ProductId:X4}: input {info.InputLength}, output {info.OutputLength}, feature {info.FeatureLength} bytes");
        Log.Info($"Instance: {info.InstanceId}");

        using var bt = DualSenseBt.Open(info);
        if (bt == null)
        {
            Log.Error("Open failed");
            return 1;
        }
        int reports = 0, micReports = 0;
        byte[]? last = null;
        bt.InputReport += (buf, n) =>
        {
            if (buf[0] != 0x31) return;
            if ((buf[1] & 2) != 0) micReports++;
            else
            {
                reports++;
                last = buf[..n];
            }
        };
        bt.Start();

        foreach (byte id in new byte[] { 0x09, 0x20, 0x22, 0x05 })
        {
            var body = bt.GetFeatureLive(id);
            Log.Info($"Feature 0x{id:X2}: {(body == null ? "FAILED" : $"{body.Length} bytes, {Convert.ToHexString(body.AsSpan(0, Math.Min(16, body.Length)))}...")}");
        }

        var sw = Stopwatch.StartNew();
        Thread.Sleep(2000);
        Log.Info($"Input: {reports / sw.Elapsed.TotalSeconds:F0} reports/s (0x31 full mode), mic reports {micReports}");
        if (last != null)
        {
            var s = last.AsSpan(2);
            Log.Info($"Sticks L({s[0]},{s[1]}) R({s[2]},{s[3]}) triggers {s[4]}/{s[5]} battery {Math.Min(100, (s[52] & 0x0F) * 10 + 5)}% " +
                     $"headphones={(s[53] & 1) != 0}");
        }

        var state = new StateData { AllowLedColor = true, LedRed = 0, LedGreen = 0x40, LedBlue = 0xFF };
        bt.SendState(state.ToReport31());
        int sent = 0;
        var t = Stopwatch.StartNew();
        double phase = 0;
        byte counter = 0;
        while (t.ElapsedMilliseconds < 1000 && bt.Alive)
        {
            var pkt = new byte[142];
            pkt[0] = 0x32;
            pkt[2] = 0x11 | 1 << 7;
            pkt[3] = 4;
            pkt[4] = 0b01100110;
            pkt[5] = pkt[6] = 48;
            pkt[7] = counter;
            counter += 2;
            pkt[8] = 0x12 | 1 << 6 | 1 << 7;
            pkt[9] = 64;
            for (int i = 0; i < 64; i++)
            {
                sbyte v = (sbyte)(Math.Sin(phase) * 40);
                pkt[10 + i * 2] = pkt[11 + i * 2] = (byte)v;
                phase += 2 * Math.PI * 160 / 3000;
            }
            bt.SendAudio(pkt);
            sent++;
            Thread.Sleep(21);
        }
        Thread.Sleep(200);
        Log.Info($"Haptics test: {sent} reports queued, link alive={bt.Alive}");
        Log.Info("Output write mode: " + (bt.PadRequired.Count == 0
            ? "exact report length"
            : string.Join(", ", bt.PadRequired.Select(kv => $"{kv.Key}B padded"))));
        return bt.Alive ? 0 : 1;
    }

    public static int Capture(Config cfg, int? pid)
    {
        var audio = new AudioBridge(cfg, () => null);
        using var mixer = new HapticsMixer(cfg, () => null, audio);
        var haptics = new AudioHaptics(cfg, mixer) { Enabled = true };
        long frames = 0;
        double sumSq = 0;
        float peakLevel = 0;
        using (var cap = LoopbackCapture.Start(pid, data =>
               {
                   frames += data.Length / 2;
                   foreach (var v in data) sumSq += v * v;
                   haptics.Feed(data);
                   peakLevel = Math.Max(peakLevel, haptics.Level);
               }))
        {
            Thread.Sleep(3000);
            Console.WriteLine($"capture of {cap.Description}: running={cap.IsRunning}");
        }
        double rms = frames > 0 ? Math.Sqrt(sumSq / (frames * 2)) : 0;
        Console.WriteLine($"frames={frames} ({frames / 48000.0:F2} s of audio), rms={rms:F4}, peak haptics level={peakLevel:F2}");
        return frames > 0 ? 0 : 1;
    }

    public static int Games(string[] extra)
    {
        var games = new List<GameInfo>();
        foreach (var a in extra)
        {
            var parts = a.Split(':', 2);
            games.Add(parts.Length == 2 && int.TryParse(parts[0], out int id)
                ? new GameInfo($"steam:{id}", parts[1], "Steam", id, "", 0)
                : new GameInfo("exe:" + a, a, "Name", null, "", 0));
        }
        if (games.Count == 0)
            games.AddRange(GameLibrary.Scan([], []).Select(l => new GameInfo(l.Key, l.Name, l.Source, l.SteamAppId, "", 0, l.Aliases)));

        var client = new PcgwClient(Path.Combine(Path.GetTempPath(), "zs5-pcgw-test.json"));
        foreach (var g in games)
        {
            var info = client.LookupAsync(new GameRef(g.Key, g.Name, g.SteamAppId, null, g.Aliases), refresh: true).GetAwaiter().GetResult();
            var v = Verdict.For(info);
            Console.WriteLine($"{g.Name} [{g.Source}{(g.SteamAppId is int sid ? $", Steam {sid}" : "")}] -> {(info == null ? "no PCGW page" : info.Page)}");
            if (g.Aliases is { Length: > 1 }) Console.WriteLine($"    aliases: {string.Join(" | ", g.Aliases)}");
            if (info != null)
                Console.WriteLine($"    models={string.Join("/", info.PlayStationModels)} haptics={info.Haptics} triggers={info.AdaptiveTriggers} " +
                                  $"lightbar={info.LightBar} speaker={info.Speaker} xinput={info.XInput} conn=\"{info.ConnectionModes}\" wiredOnly={info.WiredOnly}");
            Console.WriteLine($"    => {v.Recommended}: {v.Summary}");
            if (info?.CoverUrl != null) Console.WriteLine($"    cover: {info.CoverUrl}");
            if (info?.PromptMod is { } mod)
            {
                Console.WriteLine("    PlayStation prompts mod:");
                foreach (var line in mod.Text.Split('\n')) Console.WriteLine("      " + line);
                foreach (var link in mod.Links) Console.WriteLine($"      -> {link.Label}: {link.Url}");
            }
        }
        return 0;
    }

    public static int SelfTest(Config cfg)
    {
        var mock = new MockController();
        var audio = new AudioBridge(cfg, () => mock);
        const int port = 3299;
        using var server = new UsbIpServer(port);
        server.Start();
        server.Device = new VirtualDualSense(mock, audio, cfg);
        int failures = 0;
        void Check(bool ok, string what)
        {
            if (ok) Log.Info("PASS " + what);
            else
            {
                Log.Error("FAIL " + what);
                failures++;
            }
        }

        Check(TestClient.DeviceCount(port) == 1, "OP_REQ_DEVLIST lists one device");

        using (var client = new TestClient(port))
        {
            Check(client.ImportStatus == 0 && client.Vid == 0x054C && client.Pid == 0x0CE6, "OP_REQ_IMPORT 054C:0CE6");

            var (s1, dev) = client.ControlIn(0x80, 6, 0x0100, 0, 18);
            Check(s1 == 0 && dev.Length == 18 && dev[8] == 0x4C && dev[10] == 0xE6, "GET_DESCRIPTOR device");
            var (s2, conf) = client.ControlIn(0x80, 6, 0x0200, 0, 255);
            Check(s2 == 0 && conf.Length == 227 && conf[4] == 4, "GET_DESCRIPTOR configuration (227 bytes, 4 interfaces)");
            var (s3, str) = client.ControlIn(0x80, 6, 0x0302, 0x0409, 255);
            Check(s3 == 0 && System.Text.Encoding.Unicode.GetString(str, 2, str.Length - 2) == "DualSense Wireless Controller", "GET_DESCRIPTOR product string");
            var (s4, _) = client.ControlIn(0x80, 6, 0x0600, 0, 10);
            Check(s4 == -32, "Device qualifier stalls (full-speed device)");
            var (s5, rd) = client.ControlIn(0x81, 6, 0x2200, 3, 337);
            Check(s5 == 0 && rd.Length == 337, "HID report descriptor (337 bytes)");
            var (s6, f05) = client.ControlIn(0xA1, 1, 0x0305, 3, 41);
            Check(s6 == 0 && f05.Length == 41 && f05[0] == 0x05, "GET_REPORT feature 0x05 via Bluetooth");
            var (s7, vol) = client.ControlIn(0xA1, 0x82, 0x0200, 0x0200, 2);
            Check(s7 == 0 && vol.Length == 2 && vol[1] == 0x9C, "Audio GET_MIN speaker volume (-100 dB)");

            var setVol = client.ControlOut(0x21, 0x01, 0x0200, 0x0200, [0x00, 0xEC]);
            Thread.Sleep(50);
            Check(setVol == 0 && mock.States.Any(r => r[0] == 0x32 && r[4 + 5] == 80), "SET_CUR volume -> controller speaker volume 80");

            var outRep = new byte[48];
            outRep[0] = 0x02;
            outRep[2] = 0x04;
            outRep[45] = 0x11;
            outRep[46] = 0x22;
            outRep[47] = 0x33;
            var o = client.Wait(client.Submit(3, 0, new byte[8], outRep, (uint)outRep.Length));
            Thread.Sleep(50);
            Check(o.status == 0 && mock.States.Any(r => r[0] == 0x31 && r.Length == 78 && r[3 + 44] == 0x11 && r[3 + 46] == 0x33),
                "Output report 0x02 -> BT 0x31 with lightbar colour");

            var inR = client.Wait(client.Submit(4, 1, new byte[8], [], 64));
            Check(inR.status == 0 && inR.data.Length == 64 && inR.data[0] == 0x01 && inR.data[1] == 0x80, "Interrupt IN input report 0x01");

            mock.Paused = true;
            Thread.Sleep(20);
            client.Submit(4, 1, new byte[8], [], 64);
            uint victim = client.Submit(4, 1, new byte[8], [], 64);
            Thread.Sleep(20);
            Check(client.Unlink(victim) == -104, "CMD_UNLINK of a pending URB returns -ECONNRESET");
            mock.Paused = false;

            client.ControlOut(0x01, 0x0B, 1, 1, []);
            client.ControlOut(0x01, 0x0B, 1, 2, []);
            Thread.Sleep(50);
            Check(mock.States.Any(r => r[0] == 0x32 && r[3] == 1 && (r[4] & 1) == 1), "Opening mic stream enables controller mic");

            var seqs = new List<uint>();
            long start = Stopwatch.GetTimestamp();
            double ph = 0;
            for (int u = 0; u < 25; u++)
            {
                var pcm = new byte[8 * 384];
                for (int f = 0; f < 8 * 48; f++)
                {
                    short sp = (short)(Math.Sin(ph * 4) * 8000), hp = (short)(Math.Sin(ph) * 16000);
                    ph += 2 * Math.PI * 100 / 48000;
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 8), sp);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 8 + 2), sp);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 8 + 4), hp);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 8 + 6), hp);
                }
                var iso = Enumerable.Range(0, 8).Select(i => ((uint)(i * 384), 384u)).ToArray();
                seqs.Add(client.Submit(1, 0, new byte[8], pcm, (uint)pcm.Length, iso));
            }
            long lastTicks = 0;
            bool allOk = true;
            foreach (var sq in seqs)
            {
                var r = client.Wait(sq);
                allOk &= r.status == 0;
                lastTicks = r.ticks;
            }
            double elapsedMs = (lastTicks - start) * 1000.0 / Stopwatch.Frequency;
            Check(allOk && elapsedMs is > 185 and < 260, $"Iso OUT paced in real time: 200 ms of audio completed in {elapsedMs:F0} ms");
            Thread.Sleep(50);
            int audioReports = mock.Audio.Count;
            Check(audioReports is >= 8 and <= 10 && mock.Audio.All(r => r[0] == 0x39 && r.Length == 547),
                $"Haptics + speaker -> {audioReports} BT 0x39 reports");
            var last = mock.Audio.LastOrDefault();
            if (last != null)
            {
                int peak = 0;
                for (int i = 12; i < 12 + 128; i++) peak = Math.Max(peak, Math.Abs((sbyte)last[i]));
                Check(peak is > 40 and < 70, $"Haptics samples carry the 100 Hz tone (peak {peak}, expected ~62)");
                Check(last[141] == 200 && last[142] != 0, "Speaker Opus frames present (200 bytes CBR)");
            }

            mock.EmitMic();
            Thread.Sleep(20);
            var micIso = Enumerable.Range(0, 10).Select(i => ((uint)(i * 196), 196u)).ToArray();
            var mic = client.Wait(client.Submit(2, 1, new byte[8], [], 1960, micIso));
            Check(mic.status == 0 && mic.data.Length == 1920, $"Iso IN mic URB returns 10 x 192 bytes ({mic.data.Length})");
            Check(mic.data.Any(b => b != 0), "Mic PCM decoded from controller Opus");
        }

        server.Device = null;
        var xbox = new VirtualXbox360(mock);
        (byte heavy, byte light)? rumble = null;
        int player = -1;
        xbox.Rumble += (h, l) => rumble = (h, l);
        xbox.PlayerChanged += p => player = p;
        server.Device = xbox;
        using (var x = new TestClient(port))
        {
            Check(x.Vid == 0x045E && x.Pid == 0x028E, "Xbox 360: OP_REQ_IMPORT 045E:028E");
            var (xs1, xdev) = x.ControlIn(0x80, 6, 0x0100, 0, 18);
            Check(xs1 == 0 && xdev.Length == 18 && xdev[4] == 0xFF, "Xbox 360: device descriptor (vendor class)");
            var (xs2, xconf) = x.ControlIn(0x80, 6, 0x0200, 0, 255);
            Check(xs2 == 0 && xconf.Length == 153 && xconf[4] == 4, "Xbox 360: configuration descriptor (153 bytes, 4 interfaces)");
            var (xs3, xstr) = x.ControlIn(0x80, 6, 0x0302, 0x0409, 255);
            Check(xs3 == 0 && System.Text.Encoding.Unicode.GetString(xstr, 2, xstr.Length - 2) == "Controller", "Xbox 360: product string");

            var xrep = x.Wait(x.Submit(1, 1, new byte[8], [], 32));
            short rx = BinaryPrimitives.ReadInt16LittleEndian(xrep.data.AsSpan(10));
            short ly = BinaryPrimitives.ReadInt16LittleEndian(xrep.data.AsSpan(8));
            Check(xrep.status == 0 && xrep.data.Length == 20 && xrep.data[1] == 0x14 && (xrep.data[3] & 0x10) != 0 && rx == 32767 && ly == -129,
                $"Xbox 360: report maps Cross->A, right stick, inverted Y (rx={rx}, ly={ly})");

            uint pending = x.Submit(3, 1, new byte[8], [], 32);
            Thread.Sleep(30);
            Check(x.Unlink(pending) == -104, "Xbox 360: unused IN endpoint stays idle");

            x.Wait(x.Submit(2, 0, new byte[8], [0x00, 0x08, 0x00, 200, 100, 0, 0, 0], 8));
            x.Wait(x.Submit(2, 0, new byte[8], [0x01, 0x03, 0x07], 3));
            Thread.Sleep(20);
            Check(rumble == (200, 100), "Xbox 360: rumble command reaches the bridge");
            Check(player == 2, "Xbox 360: LED command -> player 2");
        }
        server.Device = null;
        xbox.Release();

        lock (mock.Audio) mock.Audio.Clear();
        using (var mixer = new HapticsMixer(cfg, () => mock, audio))
        using (var engine = new RumbleEngine(cfg, () => mock, mixer))
        {
            cfg.RumbleStyle = 0;
            engine.Set(255, 0);
            Thread.Sleep(150);
            engine.Set(0, 0);
            List<byte[]> packets;
            lock (mock.Audio) packets = mock.Audio.Where(r => r[0] == 0x32).ToList();
            int peakL = 0, peakR = 0;
            foreach (var pk in packets)
                for (int i = 10; i < 10 + 128; i += 2)
                {
                    peakL = Math.Max(peakL, Math.Abs((sbyte)pk[i]));
                    peakR = Math.Max(peakR, Math.Abs((sbyte)pk[i + 1]));
                }
            Check(packets.Count is >= 5 and <= 10 && peakL > 100 && peakR is > 20 and < 50,
                $"Rumble -> HD haptics: {packets.Count} packets, left peak {peakL}, right peak {peakR}");

            cfg.RumbleStyle = 1;
            while (mock.States.TryDequeue(out _)) { }
            engine.Set(200, 100);
            Thread.Sleep(30);
            var st31 = mock.States.LastOrDefault(r => r[0] == 0x31);
            Check(st31 != null && (st31[3] & 0x03) == 0x03 && st31[3 + 3] == 200 && st31[3 + 2] == 100,
                "Rumble -> classic emulation report (flags + motor values)");
            cfg.RumbleStyle = 0;
        }

        static string Hex(byte[] b) => Convert.ToHexString(b);
        Check(Hex(new TriggerEffect { Type = TriggerEffectType.Resistance, Start = 2, Strength = 5 }.Encode()) == "21FC030049922400000000",
            "Trigger encoder: resistance from zone 2, strength 5");
        Check(Hex(new TriggerEffect { Type = TriggerEffectType.Weapon, Start = 2, End = 5, Strength = 8 }.Encode()) == "25240007000000000000" + "00",
            "Trigger encoder: weapon 2-5, strength 8");
        Check(Hex(new TriggerEffect { Type = TriggerEffectType.Vibration, Start = 0, Strength = 8, Frequency = 30 }.Encode()) == "26FF03FFFFFF3F00001E00",
            "Trigger encoder: vibration all zones, 30 Hz");
        Check(new TriggerEffect { Type = TriggerEffectType.Off }.Encode()[0] == 0x05, "Trigger encoder: off");
        var slope = new TriggerEffect { Type = TriggerEffectType.Slope, Start = 1, End = 5, Strength = 1, EndStrength = 8 };
        var roundTrip = new TriggerEffect { Type = TriggerEffectType.Captured, Raw = slope.Encode() };
        Check(slope.Curve().SequenceEqual(roundTrip.Curve()) && slope.Curve()[0] == 0 && slope.Curve()[9] == 8,
            $"Trigger slope curve survives capture ({string.Join(",", slope.Curve())})");

        var triggerCfg = new Config();
        var triggerMock = new MockController();
        var triggers = new TriggerManager(triggerCfg, () => triggerMock);
        byte[]? capturedR = null;
        triggers.Captured += (side, b) => { if (side == 1) capturedR = b; };
        var gameState = new StateData();
        gameState.SetTrigger(1, new TriggerEffect { Type = TriggerEffectType.Weapon, Start = 3, End = 6, Strength = 6 }.Encode());
        triggers.FilterGameReport(gameState);
        Check(capturedR is { } cr && cr[0] == 0x25 && gameState.AllowRightTriggerFFB, "Game trigger effect is captured and passed through");
        triggerCfg.TriggerR = new TriggerEffect { Type = TriggerEffectType.Resistance, Start = 0, Strength = 8 };
        triggers.FilterGameReport(gameState);
        Check(!gameState.AllowRightTriggerFFB, "Custom R2 effect blocks the game's R2 effect");
        while (triggerMock.States.TryDequeue(out _)) { }
        triggers.Apply();
        Check(triggerMock.States.TryDequeue(out var applied) && applied[0] == 0x31 && (applied[3] & 0x04) != 0 && applied[3 + 10] == 0x21,
            "Custom effect is sent in a 0x31 report");

        var ahCfg = new Config();
        var ahAudio = new AudioBridge(ahCfg, () => null);
        using (var ahMixer = new HapticsMixer(ahCfg, () => null, ahAudio))
        {
            var ah = new AudioHaptics(ahCfg, ahMixer) { Enabled = true };
            float LevelFor(double hz, double amp)
            {
                ah.Reset();
                var buf = new float[480 * 2];
                double ph = 0;
                float peak = 0;
                for (int block = 0; block < 50; block++)
                {
                    for (int i = 0; i < 480; i++)
                    {
                        buf[i * 2] = buf[i * 2 + 1] = (float)(Math.Sin(ph) * amp);
                        ph += 2 * Math.PI * hz / 48000;
                    }
                    ah.Feed(buf);
                    if (block > 20) peak = Math.Max(peak, ah.Level);
                }
                return peak;
            }
            float bass = LevelFor(60, 0.3), treble = LevelFor(2000, 0.5), quiet = LevelFor(60, 0.003);
            Check(bass > 0.8f && treble < 0.05f && quiet < 0.05f,
                $"Audio haptics follows bass only (60 Hz: {bass:F2}, 2 kHz: {treble:F2}, quiet: {quiet:F2})");
            LevelFor(60, 0.3);
            var outL = new float[32];
            var outR = new float[32];
            ah.Render(outL, outR);
            Check(outL.Any(v => Math.Abs(v) > 0.3f), "Audio haptics produces actuator samples");

            var rum = new RumbleEngine(ahCfg, () => null, ahMixer);
            ah.Enabled = false;
            rum.Set(255, 0);
            var frame = new float[64];
            for (int k = 0; k < 4; k++) ahMixer.MixHostFrame(frame);
            Check(frame.Any(v => Math.Abs(v) > 0.2f), "Rumble mixes into the game's haptics stream");
            rum.Set(0, 0);
            rum.Dispose();
        }

        const string page = """
            {{Input
            |playstation prompts       = hackable
            |playstation prompts notes = Uses generic prompts. PlayStation prompts can be used with [[#GInput|GInput]].
            |light bar support         = false
            }}

            ===GInput===
            {{Fixbox|description=Installing GInput|ref={{cn|date=2020-01-01}}|fix=
            # Download GInputSA [https://example.com/ginput here].
            # Copy {{file|GInputSA.asi}} into {{folder|{{p|game}}\scripts\}}.
            '''Notes'''
            {{ii}} Works with [[DsHidMini]].
            }}

            ===Something else===
            Unrelated text.
            """;
        var fields = PcgwInfo.ParseInputTemplate(page);
        var modWiki = PcgwInfo.ExtractPromptMod(page, fields);
        var (modText, modLinks) = WikiText.Render(modWiki);
        Check(modText.Contains("1. Download GInputSA here.") && modText.Contains("2. Copy GInputSA.asi into [game folder]\\scripts\\."),
            "Prompt mod: section followed from the notes, steps numbered, paths readable");
        Check(!modText.Contains("Unrelated") && !modText.Contains("{{") && !modText.Contains("[["), "Prompt mod: only the linked section, no wiki markup left");
        Check(modLinks.Count == 1 && modLinks[0].Url == "https://example.com/ginput" && modLinks[0].Label == "here",
            "Prompt mod: external link extracted");
        Check(new PcgwInfo { Fields = fields, PromptModWiki = modWiki }.PromptMod != null, "Prompt mod: offered for a hackable game");
        Check(GameLibrary.IsHelper(@"C:\Games\X\UnityCrashHandler64.exe") && !GameLibrary.IsHelper(@"C:\Games\X\Game.exe"),
            "Game detection skips helper executables");

        (string raw, string clean)[] names =
        [
            ("Call.of.Duty.MW4.SP", "Call of Duty MW4"),
            ("1666Amsterdam", "1666 Amsterdam"),
            ("DantesInferno", "Dantes Inferno"),
            ("CONTROLResonant", "CONTROL Resonant"),
            ("X-Men Destiny version 0.1.0-alpha.2", "X-Men Destiny"),
            ("Some.Game.v1.2.3-CODEX", "Some Game"),
            ("Some Game [FitGirl Repack]", "Some Game"),
            ("Grand_Theft_Auto_V", "Grand Theft Auto V"),
            ("uncharted-windows", "uncharted"),
            ("Gears of War E Day", "Gears of War E Day"),
            ("Half-Life 2", "Half-Life 2"),
            ("METRO 2033 Redux Next Gen", "METRO 2033 Redux Next Gen"),
        ];
        var badNames = names.Where(n => GameNames.Clean(n.raw) != n.clean).Select(n => $"{n.raw} -> {GameNames.Clean(n.raw)}").ToList();
        Check(badNames.Count == 0, "Game names cleaned from folders and release names" + (badNames.Count > 0 ? ": " + string.Join("; ", badNames) : ""));

        Log.Info(failures == 0 ? "Self-test passed" : $"Self-test: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    sealed class TestClient : IDisposable
    {
        readonly TcpClient _c;
        readonly NetworkStream _s;
        readonly BlockingCollection<(uint seq, int status, byte[] data, long ticks)> _replies = new();
        readonly ConcurrentDictionary<uint, uint> _dir = new();
        uint _next = 1;
        public ushort Vid, Pid;
        public uint ImportStatus;

        public TestClient(int port)
        {
            _c = new TcpClient("127.0.0.1", port) { NoDelay = true };
            _s = _c.GetStream();
            var import = new byte[40];
            new byte[] { 0x01, 0x11, 0x80, 0x03 }.CopyTo(import, 0);
            "1-1"u8.ToArray().CopyTo(import, 8);
            _s.Write(import);
            var rep = new byte[8 + 312];
            _s.ReadExactly(rep);
            ImportStatus = BinaryPrimitives.ReadUInt32BigEndian(rep.AsSpan(4));
            Vid = BinaryPrimitives.ReadUInt16BigEndian(rep.AsSpan(8 + 300));
            Pid = BinaryPrimitives.ReadUInt16BigEndian(rep.AsSpan(8 + 302));
            new Thread(Read) { IsBackground = true }.Start();
        }

        public static uint DeviceCount(int port)
        {
            using var c = new TcpClient("127.0.0.1", port);
            var s = c.GetStream();
            s.Write(new byte[] { 0x01, 0x11, 0x80, 0x05, 0, 0, 0, 0 });
            var hdr = new byte[12];
            s.ReadExactly(hdr);
            return BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(8));
        }

        void Read()
        {
            try
            {
                var h = new byte[48];
                while (true)
                {
                    _s.ReadExactly(h);
                    uint cmd = BinaryPrimitives.ReadUInt32BigEndian(h), seq = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
                    int status = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(20));
                    byte[] data = [];
                    if (cmd == 3)
                    {
                        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(24));
                        uint packets = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(32));
                        data = new byte[_dir.GetValueOrDefault(seq) == 1 ? len : 0];
                        _s.ReadExactly(data);
                        if (packets is > 0 and not 0xFFFFFFFF) _s.ReadExactly(new byte[16 * packets]);
                    }
                    _replies.Add((seq, status, data, Stopwatch.GetTimestamp()));
                }
            }
            catch (Exception)
            {
                _replies.CompleteAdding();
            }
        }

        public uint Submit(uint ep, uint dir, byte[] setup, byte[] outData, uint length, (uint off, uint len)[]? iso = null)
        {
            uint seq = _next++;
            _dir[seq] = dir;
            var b = new byte[48 + (dir == 0 ? outData.Length : 0) + (iso?.Length ?? 0) * 16];
            BinaryPrimitives.WriteUInt32BigEndian(b, 1);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), dir);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), ep);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), length);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(32), iso == null ? 0xFFFFFFFF : (uint)iso.Length);
            setup.CopyTo(b, 40);
            if (dir == 0) outData.CopyTo(b, 48);
            if (iso != null)
            {
                int p = 48 + (dir == 0 ? outData.Length : 0);
                foreach (var (off, len) in iso)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), off);
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p + 4), len);
                    p += 16;
                }
            }
            lock (_s) _s.Write(b);
            return seq;
        }

        public (uint seq, int status, byte[] data, long ticks) Wait(uint seq)
        {
            var other = new List<(uint, int, byte[], long)>();
            try
            {
                while (_replies.TryTake(out var r, 2000))
                {
                    if (r.seq == seq) return r;
                    other.Add(r);
                }
                return (seq, int.MinValue, [], 0);
            }
            finally
            {
                foreach (var o in other) _replies.Add(o);
            }
        }

        static byte[] Setup(byte type, byte req, ushort value, ushort index, ushort length) =>
            [type, req, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), (byte)length, (byte)(length >> 8)];

        public (int, byte[]) ControlIn(byte type, byte req, ushort value, ushort index, ushort length)
        {
            var r = Wait(Submit(0, 1, Setup(type, req, value, index, length), [], length));
            return (r.status, r.data);
        }

        public int ControlOut(byte type, byte req, ushort value, ushort index, byte[] data) =>
            Wait(Submit(0, 0, Setup(type, req, value, index, (ushort)data.Length), data, (uint)data.Length)).status;

        public int Unlink(uint victim)
        {
            uint seq = _next++;
            var b = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(b, 2);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), victim);
            lock (_s) _s.Write(b);
            return Wait(seq).status;
        }

        public void Dispose() => _c.Close();
    }

    sealed class MockController : IController
    {
        public event Action<byte[], int>? InputReport;
        public readonly ConcurrentQueue<byte[]> States = new();
        public readonly List<byte[]> Audio = new();
        public volatile bool Paused;
        readonly byte[] _micOpus;

        public MockController()
        {
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(4);
                    if (Paused) continue;
                    var r = new byte[78];
                    r[0] = 0x31;
                    r[2] = 0x80; r[3] = 0x80; r[4] = 0xFF; r[5] = 0x80; r[9] = 0x28;
                    InputReport?.Invoke(r, r.Length);
                }
            }) { IsBackground = true }.Start();

            var enc = Concentus.OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
            enc.Bitrate = 71 * 8 * 100;
            enc.UseVBR = false;
            var pcm = new short[480];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin(i * 0.05) * 10000);
            _micOpus = new byte[71];
            enc.Encode(pcm, 480, _micOpus, 71);
        }

        public void EmitMic()
        {
            var r = new byte[78];
            r[0] = 0x31;
            r[1] = 0x02;
            _micOpus.CopyTo(r, 3);
            InputReport?.Invoke(r, r.Length);
        }

        public byte[]? GetFeatureLive(byte id) => Enumerable.Repeat((byte)0xAB, 44).ToArray();
        public byte[]? GetFeatureCached(byte id) => GetFeatureLive(id);
        public bool SetFeature(byte id, ReadOnlySpan<byte> body) => true;
        public void SendState(byte[] report) => States.Enqueue(report);
        public void SendAudio(byte[] report)
        {
            lock (Audio) Audio.Add(report);
        }
    }
}
