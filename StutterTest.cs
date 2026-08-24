// StutterTest.cs -- single-window app. Detect game, record, analyse, report.
// Built against .NET Framework 4 (csc.exe ships with Windows, no SDK needed).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace StutterTest
{
    // ---------- analysis ---------------------------------------------------

    public class Frame { public double Ft, Cpu, Gpu; }

    public class Hitch { public int Index; public double Lost; public string Cause; }

    public class Result
    {
        public string Game = "";
        public int Frames;
        public double Seconds, Median, P99, TotalLost, LostPct;
        public List<Hitch> Hitches = new List<Hitch>();
        public Dictionary<string, double> ByCause = new Dictionary<string, double>();
        public bool Warmup, Throttle, Pacing;
        public double Drift, R1;
        // test validity
        public double Headroom, Spread;
        public bool Capped, TooShort;
        public string Caveat = "";
        public List<double> Trace = new List<double>();
        public HashSet<int> HitchAt = new HashSet<int>();
        public string Headline = "", Verdict = "", VClass = "good";
    }

    public static class Analyzer
    {
        const int Window = 120;
        const double Ratio = 2.0, AbsMs = 8.0, Share = 0.55, DecayRatio = 2.5;
        const double ThrottleMin = 8.0, ThrottleMax = 40.0, PacingR1 = -0.35;
        const double SeverityFloor = 1.0;
        const int MinHitchPattern = 20;
        const double WarmupMinRate = 3.0;

        static double Median(List<double> v)
        {
            if (v.Count == 0) return 0;
            var s = new List<double>(v); s.Sort();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
        }
        static double Pct(List<double> v, double p)
        {
            if (v.Count == 0) return 0;
            var s = new List<double>(v); s.Sort();
            int i = (int)(s.Count * p); if (i >= s.Count) i = s.Count - 1;
            return s[i];
        }
        static double D(string s)
        {
            double d; return double.TryParse(s, NumberStyles.Any,
                CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        public static Result Run(string csvPath, string game)
        {
            var r = new Result(); r.Game = game;
            var frames = new List<Frame>();

            using (var sr = new StreamReader(csvPath))
            {
                string header = sr.ReadLine();
                if (header == null) return r;
                var cols = header.TrimStart('\uFEFF').Split(',');
                int iFt = Array.IndexOf(cols, "FrameTime");
                int iCpu = Array.IndexOf(cols, "CPUBusy");
                int iGpu = Array.IndexOf(cols, "GPUBusy");
                if (iFt < 0) iFt = Array.IndexOf(cols, "msBetweenPresents");
                if (iFt < 0) return r;

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    var p = line.Split(',');
                    if (p.Length <= iFt) continue;
                    double ft = D(p[iFt]);
                    if (ft <= 0 || ft > 5000) continue;
                    frames.Add(new Frame {
                        Ft = ft,
                        Cpu = (iCpu >= 0 && p.Length > iCpu) ? D(p[iCpu]) : 0,
                        Gpu = (iGpu >= 0 && p.Length > iGpu) ? D(p[iGpu]) : 0
                    });
                }
            }

            int n = frames.Count;
            r.Frames = n;
            if (n < 600) return r;

            var all = frames.Select(f => f.Ft).ToList();
            r.Seconds = all.Sum() / 1000.0;
            r.Median = Median(all);
            r.P99 = Pct(all, 0.99);

            // rolling-baseline hitch detection
            var w = new List<double>(); var wc = new List<double>(); var wg = new List<double>();
            for (int i = 0; i < n; i++)
            {
                if (w.Count >= 30)
                {
                    double b = Median(w), bc = Median(wc), bg = Median(wg);
                    double thresh = Math.Max(Ratio * b, b + AbsMs);
                    if (frames[i].Ft > thresh)
                    {
                        double lost = frames[i].Ft - b;
                        string cause = "unattributed";
                        if (bc > 0 && bg > 0)
                        {
                            double ce = Math.Max(0, frames[i].Cpu - bc);
                            double ge = Math.Max(0, frames[i].Gpu - bg);
                            double tot = ce + ge;
                            if (tot < 0.35 * lost) cause = "present_path";
                            else if (ce >= Share * tot) cause = "cpu_stall";
                            else if (ge >= Share * tot) cause = "gpu_spike";
                            else cause = "mixed";
                        }
                        r.Hitches.Add(new Hitch { Index = i, Lost = lost, Cause = cause });
                        r.HitchAt.Add(i);
                    }
                }
                w.Add(frames[i].Ft); wc.Add(frames[i].Cpu); wg.Add(frames[i].Gpu);
                if (w.Count > Window) { w.RemoveAt(0); wc.RemoveAt(0); wg.RemoveAt(0); }
            }

            int fifth = n / 5;

            // warm-up decay, gated on having enough evidence to claim a trend
            var q = new double[5];
            foreach (var h in r.Hitches)
                if (h.Cause == "cpu_stall")
                {
                    int b = Math.Min(4, h.Index * 5 / n);
                    q[b] += 1;
                }
            for (int i = 0; i < 5; i++) q[i] = q[i] / Math.Max(1, fifth) * 1000.0;
            int cpuStalls = r.Hitches.Count(h => h.Cause == "cpu_stall");
            double backHalf = (q[3] + q[4]) / 2.0;
            if (cpuStalls >= MinHitchPattern && q[0] >= WarmupMinRate)
                r.Warmup = backHalf <= 0.001 ? q[0] >= WarmupMinRate * 2
                                             : (q[0] / backHalf) >= DecayRatio;

            // throttle: compare the LIGHTEST frames early vs late
            if (fifth > 50)
            {
                var early = frames.Take(fifth).Where(f => f.Gpu > 0).Select(f => f.Gpu).ToList();
                var late = frames.Skip(n - fifth).Where(f => f.Gpu > 0).Select(f => f.Gpu).ToList();
                if (early.Count > 10 && late.Count > 10)
                {
                    double e = Pct(early, 0.10), l = Pct(late, 0.10);
                    if (e > 0)
                    {
                        r.Drift = (l - e) / e * 100.0;
                        r.Throttle = r.Drift >= ThrottleMin && r.Drift <= ThrottleMax;
                    }
                }
            }

            // pacing: alternating long/short among clean frames
            var clean = new List<double>();
            for (int i = 0; i < n; i++) if (!r.HitchAt.Contains(i)) clean.Add(frames[i].Ft);
            if (clean.Count > 600)
            {
                double mean = clean.Average(), den = 0, num = 0;
                for (int i = 0; i < clean.Count; i++)
                {
                    double d = clean[i] - mean; den += d * d;
                    if (i < clean.Count - 1) num += d * (clean[i + 1] - mean);
                }
                if (den > 0) { r.R1 = num / den; r.Pacing = r.R1 <= PacingR1; }
            }

            foreach (var h in r.Hitches)
            {
                if (h.Cause == "cpu_stall" && r.Warmup && h.Index < n * 0.4) h.Cause = "shader_warmup";
                else if (h.Cause == "gpu_spike" && r.Throttle && h.Index > n * 0.6) h.Cause = "thermal_throttle";
                else if (h.Cause == "present_path" && r.Pacing) h.Cause = "pacing";
            }

            foreach (var h in r.Hitches)
            {
                if (!r.ByCause.ContainsKey(h.Cause)) r.ByCause[h.Cause] = 0;
                r.ByCause[h.Cause] += h.Lost;
            }
            if (r.Pacing)
            {
                double extra = clean.Where(v => v > r.Median).Sum(v => v - r.Median);
                if (!r.ByCause.ContainsKey("pacing")) r.ByCause["pacing"] = 0;
                r.ByCause["pacing"] += extra;
            }

            r.TotalLost = r.ByCause.Values.Sum();
            r.LostPct = r.Seconds > 0 ? r.TotalLost / (r.Seconds * 1000.0) * 100.0 : 0;

            // downsample for the trace graph
            int step = Math.Max(1, n / 1000);
            for (int i = 0; i < n; i += step) r.Trace.Add(frames[i].Ft);

            // ---- is this capture even capable of showing stutter? ----------
            // A hard frame cap with idle GPU time means the machine has slack
            // to absorb hiccups before they ever reach the screen. A clean
            // result under those conditions proves nothing.
            var gpuAll = frames.Where(f => f.Gpu > 0).Select(f => f.Gpu).ToList();
            if (gpuAll.Count > 100 && r.Median > 0)
            {
                double gMed = Median(gpuAll);
                r.Headroom = (1.0 - gMed / r.Median) * 100.0;
            }
            if (r.Median > 0)
            {
                double lo10 = Pct(all, 0.10), hi90 = Pct(all, 0.90);
                r.Spread = (hi90 - lo10) / r.Median * 100.0;
            }
            r.Capped = (r.Spread < 6.0 && r.Headroom > 25.0);
            r.TooShort = r.Seconds < 45;

            // Detected patterns only mean something if there's stutter to explain.
            // Reporting "your GPU is throttling" to someone with a flawless
            // capture is exactly the crying-wolf behaviour that kills trust.
            if (r.Hitches.Count == 0 && !r.Pacing)
            {
                r.Throttle = false;
                r.Warmup = false;
            }

            BuildVerdict(r);
            return r;
        }

        public static readonly string[] Fixable =
            { "pacing", "thermal_throttle", "present_path", "shader_warmup" };

        public static string Title(string cause)
        {
            switch (cause)
            {
                case "shader_warmup": return "Temporary — this will go away";
                case "pacing": return "Fixable — frames arriving unevenly";
                case "thermal_throttle": return "Fixable — your GPU slows as it heats up";
                case "present_path": return "Fixable — display or sync problem";
                case "gpu_spike": return "Partly fixable — graphics settings too high";
                case "cpu_stall": return "Not fixable — the game's engine";
                case "mixed": return "Unclear — no single cause";
                default: return "Unknown";
            }
        }
        public static string Body(string cause)
        {
            switch (cause)
            {
                case "shader_warmup": return "Your graphics card is compiling shaders the first time it meets new effects, then caching them. Replaying the same area should be noticeably smoother, and this usually settles within about 30 minutes of play.";
                case "pacing": return "Frames are being delivered irregularly rather than taking longer to produce. Cap your frame rate about 3 below your monitor's refresh rate, and turn on G-Sync or FreeSync if your monitor supports it.";
                case "thermal_throttle": return "The same GPU work took measurably longer late in the session than early on. Check temperatures, clear dust from the fans, or raise the fan curve.";
                case "present_path": return "Time was lost outside actual rendering — usually vsync or the Windows compositor. Try exclusive fullscreen and cap your frame rate just below your refresh rate.";
                case "gpu_spike": return "Your graphics card spiked doing render work. Lower ray tracing, shadow quality, or volumetric effects.";
                case "cpu_stall": return "Your processor stalled while the graphics card sat idle — typically the game loading assets as you move through the world. No setting on your end changes this. It's how the game was built.";
                case "mixed": return "The delays didn't come from one consistent place.";
                default: return "This capture lacked the detail needed to attribute causes.";
            }
        }

        static void BuildVerdict(Result r)
        {
            if (r.TooShort)
                r.Caveat = "This capture was only " + r.Seconds.ToString("0") +
                    " seconds. Stutter often clusters around entering new areas, " +
                    "so a short recording can easily miss it entirely.";

            if (r.Hitches.Count == 0 && !r.Pacing)
            {
                // The critical case: a clean result from a test that could not
                // have produced a dirty one. Say so instead of certifying it.
                if (r.Capped)
                {
                    r.Headline = "Can't tell — your frame rate is capped.";
                    r.VClass = "warn";
                    r.Verdict = string.Format(CultureInfo.InvariantCulture,
                        "No stutter showed up, but this test couldn't have found any. " +
                        "Your frame rate is locked at about {0} FPS and your graphics card " +
                        "is finishing each frame with {1:0}% of the time to spare — enough " +
                        "slack to swallow hiccups before they reach your screen. " +
                        "Turn off VSync or raise your frame cap, then record again.",
                        Math.Round(1000 / r.Median), r.Headroom);
                    return;
                }
                r.Headline = "No stuttering found.";
                r.VClass = "good";
                r.Verdict = "This session ran smoothly — no hitches worth reporting. " +
                    "If it still felt bad, the problem may be low frame rate rather than " +
                    "stutter. Those are different things and need different fixes.";
                return;
            }
            if (r.LostPct < SeverityFloor)
            {
                r.Headline = "Barely any stuttering.";
                r.VClass = "good";
                r.Verdict = string.Format(CultureInfo.InvariantCulture,
                    "{0} small hitches, costing {1:0.00}% of your playtime. " +
                    "That's low enough that most people wouldn't notice it.",
                    r.Hitches.Count, r.LostPct);
                if (r.Capped)
                    r.Caveat = "Note that your frame rate is capped with about " +
                        r.Headroom.ToString("0") + "% GPU headroom, which hides some " +
                        "stutter. Uncap it for a stricter test.";
                return;
            }
            double fix = r.ByCause.Where(k => Fixable.Contains(k.Key)).Sum(k => k.Value);
            double frac = r.TotalLost > 0 ? fix / r.TotalLost : 0;
            string top = r.ByCause.Count > 0
                ? r.ByCause.OrderByDescending(k => k.Value).First().Key : "unattributed";

            if (frac >= 0.4)
            {
                r.Headline = "Stuttering found — and you can do something about it.";
                r.VClass = "warn";
                r.Verdict = string.Format(CultureInfo.InvariantCulture,
                    "You lost {0:0.0}% of your playtime to {1} stutters. Most of it comes " +
                    "from {2}",
                    r.LostPct, r.Hitches.Count, Cause(top));
            }
            else
            {
                r.Headline = "Stuttering found — but it's the game, not your PC.";
                r.VClass = "bad";
                r.Verdict = string.Format(CultureInfo.InvariantCulture,
                    "You lost {0:0.0}% of your playtime to {1} stutters. Most of it comes " +
                    "from {2} Changing your settings won't help much.",
                    r.LostPct, r.Hitches.Count, Cause(top));
            }
        }

        // Reads as a noun phrase inside a sentence, unlike the headline form.
        static string Cause(string c)
        {
            switch (c)
            {
                case "shader_warmup": return "your graphics card compiling shaders for the first time, which will stop once they're cached.";
                case "pacing": return "frames being delivered unevenly rather than taking longer to produce.";
                case "thermal_throttle": return "your graphics card slowing down as it heats up.";
                case "present_path": return "the display and sync path rather than rendering itself.";
                case "gpu_spike": return "your graphics card spiking on render work.";
                case "cpu_stall": return "the game's engine stalling while it loads assets.";
                default: return "no single identifiable source.";
            }
        }
    }

    // ---------- HTML report ------------------------------------------------

    public static class Report
    {
        public static string Write(Result r, string dir, string specs)
        {
            var sb = new StringBuilder();
            foreach (var kv in r.ByCause.OrderByDescending(k => k.Value))
            {
                double share = r.TotalLost > 0 ? kv.Value / r.TotalLost * 100 : 0;
                if (share < 5) continue;
                string col = Analyzer.Fixable.Contains(kv.Key) ? "#b3781f" : "#5a6b74";
                if (kv.Key == "shader_warmup") col = "#3d7a52";
                sb.AppendFormat(CultureInfo.InvariantCulture,
                  "<div class='cause'><div class='top'><span class='pct'>{0:0}%</span>" +
                  "<span class='name'>{1}</span></div><div class='bar'><i style='width:{0:0}%;" +
                  "background:{2}'></i></div><p>{3}</p></div>",
                  share, Esc(Analyzer.Title(kv.Key)), col, Esc(Analyzer.Body(kv.Key)));
            }

            string caveat = string.IsNullOrEmpty(r.Caveat) ? ""
                : "<div class='caveat'><b>Worth knowing</b><p>" + Esc(r.Caveat) + "</p></div>";

            string html = Template
                .Replace("{{CAVEAT}}", caveat)
                .Replace("{{HEADROOM}}", r.Headroom > 0 ? r.Headroom.ToString("0") + "%" : "none")
                .Replace("{{CAPPED}}", r.Capped ? "yes - may hide stutter" : "no")
                .Replace("{{GAME}}", Esc(r.Game))
                .Replace("{{WHEN}}", DateTime.Now.ToString("d MMM yyyy  HH:mm"))
                .Replace("{{HEADLINE}}", Esc(r.Headline))
                .Replace("{{FRAMES}}", r.Frames.ToString("N0"))
                .Replace("{{SECONDS}}", r.Seconds.ToString("N0"))
                .Replace("{{FPS}}", r.Median > 0 ? Math.Round(1000 / r.Median).ToString() : "?")
                .Replace("{{SVG}}", Svg(r))
                .Replace("{{MEDIAN}}", r.Median.ToString("0.0"))
                .Replace("{{WORST}}", r.Trace.Count > 0 ? r.Trace.Max().ToString("0.0") : "0")
                .Replace("{{P99}}", r.P99.ToString("0.0"))
                .Replace("{{VCLASS}}", r.VClass)
                .Replace("{{VERDICT}}", Esc(r.Verdict))
                .Replace("{{CAUSES}}", sb.ToString())
                .Replace("{{HITCHES}}", r.Hitches.Count.ToString())
                .Replace("{{LOSTPCT}}", r.LostPct.ToString("0.00"))
                .Replace("{{WARMUP}}", r.Warmup ? "detected" : "not detected")
                .Replace("{{DRIFT}}", r.Throttle
                    ? r.Drift.ToString("+0.0;-0.0") + "%" : "none")
                .Replace("{{PACING}}", r.Pacing ? "yes" : "no")
                .Replace("{{SPECS}}", specs);

            string path = Path.Combine(dir, "report.html");
            File.WriteAllText(path, html, Encoding.UTF8);
            return path;
        }

        static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        static string Svg(Result r)
        {
            int W = 1000, H = 190, pad = 8;
            if (r.Trace.Count < 2) return "<svg viewBox='0 0 1000 190'></svg>";
            double hi = r.Trace.Max(), lo = r.Trace.Min();
            double rng = Math.Max(hi - lo, 1.0);
            var pts = new StringBuilder(); var dots = new StringBuilder();
            int step = Math.Max(1, r.Frames / r.Trace.Count);
            for (int k = 0; k < r.Trace.Count; k++)
            {
                double x = (double)k / (r.Trace.Count - 1) * W;
                double y = H - pad - ((r.Trace[k] - lo) / rng) * (H - 2 * pad);
                pts.AppendFormat(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0} ", x, y);
                if (r.HitchAt.Contains(k * step))
                    dots.AppendFormat(CultureInfo.InvariantCulture,
                        "<circle cx='{0:0.0}' cy='{1:0.0}' r='2.6' fill='#c2452c'/>", x, y);
            }
            var grid = new StringBuilder();
            foreach (double f in new[] { .25, .5, .75 })
                grid.AppendFormat(CultureInfo.InvariantCulture,
                    "<line x1='0' y1='{0:0}' x2='1000' y2='{0:0}' stroke='#e3e9e7' stroke-width='1'/>", H * f);
            return "<svg viewBox='0 0 1000 190' preserveAspectRatio='none' " +
                   "xmlns='http://www.w3.org/2000/svg'>" + grid +
                   "<polyline points='" + pts + "' fill='none' stroke='#1f6f8b' " +
                   "stroke-width='1.2' vector-effect='non-scaling-stroke'/>" + dots + "</svg>";
        }

        public static string Template = "";
    }

    // ---------- the window -------------------------------------------------

    public class MainForm : Form
    {
        ComboBox gameBox; Button findBtn, recBtn, openBtn;
        Label status, headline, detail; ProgressBar bar;
        string lastReport = null, exeDir;

        public MainForm()
        {
            exeDir = Path.GetDirectoryName(Application.ExecutablePath);

            Text = "Stutter Test";
            ClientSize = new Size(560, 470);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(244, 246, 244);
            Font = new Font("Segoe UI", 9.5f);

            var band = new Panel {
                Dock = DockStyle.Top, Height = 54,
                BackColor = Color.FromArgb(22, 35, 44) };
            var title = new Label {
                Text = "STUTTER TEST", ForeColor = Color.FromArgb(244, 246, 244),
                Font = new Font("Bahnschrift", 12f, FontStyle.Bold),
                AutoSize = true, Location = new Point(20, 16) };
            band.Controls.Add(title);
            Controls.Add(band);

            var step1 = MakeLabel("1.  Start your game and get into gameplay", 20, 74, true);
            Controls.Add(step1);

            findBtn = new Button {
                Text = "Find my game", Location = new Point(20, 100),
                Size = new Size(130, 30), FlatStyle = FlatStyle.System };
            findBtn.Click += FindGames;
            Controls.Add(findBtn);

            gameBox = new ComboBox {
                Location = new Point(160, 103), Size = new Size(378, 24),
                DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(gameBox);

            var step2 = MakeLabel("2.  Record 60 seconds while you play", 20, 148, true);
            Controls.Add(step2);

            recBtn = new Button {
                Text = "Record", Location = new Point(20, 174),
                Size = new Size(130, 34), FlatStyle = FlatStyle.System,
                Enabled = false, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
            recBtn.Click += StartRecording;
            Controls.Add(recBtn);

            status = new Label {
                Location = new Point(160, 182), Size = new Size(378, 22),
                ForeColor = Color.FromArgb(90, 107, 116),
                Text = "Find your game first." };
            Controls.Add(status);

            bar = new ProgressBar {
                Location = new Point(20, 216), Size = new Size(518, 6),
                Style = ProgressBarStyle.Continuous, Visible = false };
            Controls.Add(bar);

            var rule = new Panel {
                Location = new Point(20, 240), Size = new Size(518, 1),
                BackColor = Color.FromArgb(195, 205, 203) };
            Controls.Add(rule);

            headline = new Label {
                Location = new Point(20, 258), Size = new Size(518, 52),
                Font = new Font("Bahnschrift", 15f, FontStyle.Bold),
                ForeColor = Color.FromArgb(22, 35, 44), Text = "" };
            Controls.Add(headline);

            detail = new Label {
                Location = new Point(20, 312), Size = new Size(518, 96),
                ForeColor = Color.FromArgb(90, 107, 116), Text = "" };
            Controls.Add(detail);

            openBtn = new Button {
                Text = "Open full report", Location = new Point(20, 418),
                Size = new Size(150, 30), FlatStyle = FlatStyle.System, Visible = false };
            openBtn.Click += (s, e) => {
                if (lastReport != null) Process.Start(lastReport); };
            Controls.Add(openBtn);

            var folderBtn = new Button {
                Text = "Open results folder", Location = new Point(180, 418),
                Size = new Size(150, 30), FlatStyle = FlatStyle.System };
            folderBtn.Click += (s, e) => {
                string d = Path.Combine(exeDir, "results");
                Directory.CreateDirectory(d); Process.Start(d); };
            Controls.Add(folderBtn);
        }

        Label MakeLabel(string t, int x, int y, bool bold)
        {
            return new Label {
                Text = t, Location = new Point(x, y), AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = Color.FromArgb(22, 35, 44) };
        }

        string PresentMon
        {
            get
            {
                foreach (var f in Directory.GetFiles(exeDir, "PresentMon*.exe"))
                    return f;
                return null;
            }
        }

        static readonly HashSet<string> Ignore = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase) {
            "dwm.exe","WindowsTerminal.exe","explorer.exe","ApplicationFrameHost.exe",
            "SearchHost.exe","StartMenuExperienceHost.exe","TextInputHost.exe",
            "EpicGamesLauncher.exe","steamwebhelper.exe","Discord.exe","chrome.exe",
            "msedge.exe","firefox.exe","EACefSubProcess.exe","ShellExperienceHost.exe",
            "StutterTest.exe","powershell.exe","conhost.exe","Widgets.exe"
        };

        void FindGames(object sender, EventArgs e)
        {
            if (PresentMon == null) {
                MessageBox.Show("PresentMon.exe is missing from this folder.\n\n" +
                    "Make sure you unzipped the whole folder.", "Missing file");
                return;
            }
            findBtn.Enabled = false; status.Text = "Looking for games...";
            bar.Visible = true; bar.Style = ProgressBarStyle.Marquee;
            Application.DoEvents();

            string probe = Path.Combine(Path.GetTempPath(), "stutterprobe.csv");
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }

            RunPM("--output_file \"" + probe + "\" --v2_metrics --timed 8 " +
                  "--terminate_after_timed --stop_existing_session --no_console_stats");

            var found = new List<string>();
            if (File.Exists(probe))
            {
                try
                {
                    var lines = File.ReadAllLines(probe);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var p = lines[i].Split(',');
                        if (p.Length == 0) continue;
                        string app = p[0].Trim();
                        if (app.Length > 4 && !Ignore.Contains(app) && !found.Contains(app))
                            found.Add(app);
                    }
                }
                catch { }
            }

            bar.Visible = false; bar.Style = ProgressBarStyle.Continuous;
            findBtn.Enabled = true;
            gameBox.Items.Clear();
            foreach (var g in found) gameBox.Items.Add(g);

            if (found.Count == 0) {
                status.Text = "No game found. Is it running and in gameplay?";
                recBtn.Enabled = false;
            } else {
                gameBox.SelectedIndex = 0;
                status.Text = found.Count == 1
                    ? "Found it. Click Record, then switch to the game."
                    : "Pick your game, then click Record.";
                recBtn.Enabled = true;
            }
        }

        void StartRecording(object sender, EventArgs e)
        {
            if (gameBox.SelectedItem == null) return;
            string game = gameBox.SelectedItem.ToString();
            string dir = Path.Combine(exeDir, "results");
            Directory.CreateDirectory(dir);
            string safe = System.Text.RegularExpressions.Regex.Replace(
                game.Replace(".exe", ""), "[^A-Za-z0-9]", "");
            string csv = Path.Combine(dir,
                safe + "-" + DateTime.Now.ToString("MMdd-HHmm") + ".csv");

            recBtn.Enabled = false; findBtn.Enabled = false;
            headline.Text = ""; detail.Text = ""; openBtn.Visible = false;
            bar.Visible = true; bar.Value = 0; bar.Maximum = 75;
            status.Text = "Switch to your game now — recording starts in 15s";
            Application.DoEvents();

            var proc = StartPM("--process_name \"" + game + "\" --output_file \"" + csv +
                "\" --v2_metrics --delay 15 --timed 60 --terminate_after_timed " +
                "--stop_existing_session --set_circular_buffer_size 16384");

            var t = new Timer { Interval = 1000 }; int elapsed = 0;
            t.Tick += (s2, e2) =>
            {
                elapsed++;
                if (bar.Value < bar.Maximum) bar.Value = elapsed;
                status.Text = elapsed <= 15
                    ? "Starting in " + (15 - elapsed) + "s — play normally, keep moving"
                    : "Recording... " + Math.Max(0, 75 - elapsed) + "s left";
                if (proc == null || proc.HasExited || elapsed > 90)
                {
                    t.Stop();
                    bar.Visible = false;
                    recBtn.Enabled = true; findBtn.Enabled = true;
                    Finish(csv, game, dir);
                }
            };
            t.Start();
        }

        void Finish(string csv, string game, string dir)
        {
            if (!File.Exists(csv)) {
                status.Text = "Nothing was recorded.";
                headline.Text = "Recording failed";
                detail.Text = "PresentMon didn't capture anything for " + game +
                    ". Try running this as administrator, or set the game to " +
                    "Borderless Windowed and try again.";
                return;
            }
            status.Text = "Analysing...";
            Application.DoEvents();

            try
            {
                var r = Analyzer.Run(csv, game);
                if (r.Frames < 600) {
                    headline.Text = "Capture too short";
                    detail.Text = "Only " + r.Frames + " frames were recorded. " +
                        "Make sure you're actually in gameplay when recording starts.";
                    status.Text = "";
                    return;
                }
                headline.Text = r.Headline;
                detail.Text = r.Verdict +
                    (string.IsNullOrEmpty(r.Caveat) ? "" : "\r\n\r\n" + r.Caveat);
                string specs = Specs();
                File.WriteAllText(Path.ChangeExtension(csv, null) + "-system.txt",
                    "Game: " + game + "\r\n" + SpecsPlain());
                lastReport = Report.Write(r, dir, specs);
                openBtn.Visible = true;
                status.Text = "Done — saved to the results folder";
            }
            catch (Exception ex)
            {
                headline.Text = "Analysis failed";
                detail.Text = ex.Message;
                status.Text = "";
            }
        }

        string SpecsPlain()
        {
            var sb = new StringBuilder();
            try {
                sb.AppendLine("CPU: " + Wmi("Win32_Processor", "Name"));
                sb.AppendLine("GPU: " + Wmi("Win32_VideoController", "Name"));
                sb.AppendLine("OS: " + Environment.OSVersion.VersionString);
            } catch { }
            sb.AppendLine("Recorded: " + DateTime.Now);
            return sb.ToString();
        }
        string Specs()
        {
            return "<tr><td>Processor</td><td>" + Wmi("Win32_Processor", "Name") +
                   "</td></tr><tr><td>Graphics</td><td>" +
                   Wmi("Win32_VideoController", "Name") + "</td></tr>";
        }
        string Wmi(string cls, string prop)
        {
            try {
                var s = new System.Management.ManagementObjectSearcher(
                    "SELECT " + prop + " FROM " + cls);
                foreach (System.Management.ManagementObject o in s.Get())
                    return (o[prop] ?? "").ToString().Trim();
            } catch { }
            return "unknown";
        }

        Process StartPM(string args)
        {
            try {
                var psi = new ProcessStartInfo(PresentMon, args) {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                return Process.Start(psi);
            } catch { return null; }
        }
        void RunPM(string args)
        {
            var p = StartPM(args);
            if (p != null) { p.WaitForExit(30000); }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string tpl = Path.Combine(dir, "report_template.html");
            Report.Template = File.Exists(tpl) ? File.ReadAllText(tpl) : "<html><body>{{VERDICT}}</body></html>";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
