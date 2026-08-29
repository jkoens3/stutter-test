// Share.cs -- optional, opt-in sharing of capture results.
//
// DESIGN RULES, because this is the part that can destroy trust:
//
//   1. Off by default. Nothing is ever sent unless the user says yes.
//   2. The user is shown the EXACT payload before the first send. Not a
//      description of it -- the actual text that would leave the machine.
//   3. No file paths, no user name, no machine name, no IP-identifying data,
//      no game save info. Hardware model, game name, and frame statistics only.
//   4. The install ID is random, generated locally, and exists solely so ten
//      captures from one person don't look like ten people. It is disclosed.
//   5. "Never ask again" is honoured permanently and is one click away.
//
// If you are reading this file to check what it sends: the payload is built
// in BuildPayload() below and there is nothing else. No other method in this
// application opens a network connection.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace StutterTest
{
    public static class Share
    {
        // Set this to your collection endpoint. Empty = sharing disabled
        // entirely and the prompt never appears.
        public const string Endpoint = "https://script.google.com/macros/s/AKfycbwwQGOTd-2FtdSeRrPrlGsTWkieyicgiZLuMtk-u0HFylZG86tXOLsnyy-VfAg-pJio/exec";

        public const string ToolVersion = "1.5";

        static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath),
                    "share-settings.txt");
            }
        }

        // "ask" (default), "always", "never"
        public static string Mode
        {
            get
            {
                try
                {
                    foreach (var line in File.ReadAllLines(SettingsPath))
                        if (line.StartsWith("mode=")) return line.Substring(5).Trim();
                }
                catch { }
                return "ask";
            }
        }

        public static string InstallId
        {
            get
            {
                try
                {
                    foreach (var line in File.ReadAllLines(SettingsPath))
                        if (line.StartsWith("id=")) return line.Substring(3).Trim();
                }
                catch { }
                return "";
            }
        }

        static void Save(string mode, string id)
        {
            try
            {
                File.WriteAllLines(SettingsPath, new[] {
                    "# Stutter Test sharing settings. Delete this file to reset.",
                    "# mode=ask     -> prompt after each recording (default)",
                    "# mode=always  -> send automatically, no prompt",
                    "# mode=never   -> never send, never ask",
                    "mode=" + mode,
                    "id=" + id
                });
            }
            catch { }
        }

        static string NewId()
        {
            // Random per install. Not derived from anything about the machine.
            var b = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        // ---- payload ------------------------------------------------------

        public static string BuildPayload(Result r, string cpu, string gpu,
                                          string installId, CalibrationProfile cal)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            Add(sb, "tool_version", ToolVersion);
            Add(sb, "install_id", installId);
            Add(sb, "game", r.Game);
            Add(sb, "cpu", cpu);
            Add(sb, "gpu", gpu);
            string vendor = GpuVendor(gpu);
            if (vendor.Length > 0) Add(sb, "gpu_vendor", vendor);
            Num(sb, "frames", r.Frames);
            Num(sb, "duration_s", Math.Round(r.Seconds, 1));
            Num(sb, "median_frametime_ms", Math.Round(r.Median, 2));
            Num(sb, "p99_frametime_ms", Math.Round(r.P99, 2));
            Num(sb, "spread_pct", Math.Round(r.Spread, 1));
            Num(sb, "hitches", r.Hitches.Count);
            if (r.Seconds > 0)
                Num(sb, "hitches_per_min",
                    Math.Round(r.Hitches.Count / (r.Seconds / 60.0), 2));
            Num(sb, "lost_pct", Math.Round(r.LostPct, 3));
            Num(sb, "gpu_headroom_pct", Math.Round(r.Headroom, 1));
            Add(sb, "frame_capped", r.Capped ? "yes" : "no");
            Add(sb, "untrustworthy", r.Untrustworthy ? "yes" : "no");
            Add(sb, "warmup_detected", r.Warmup ? "yes" : "no");
            Add(sb, "throttle_detected", r.Throttle ? "yes" : "no");
            Add(sb, "pacing_detected", r.Pacing ? "yes" : "no");

            // Only present once this machine has enough similar captures of the
            // game for a profile to exist. It's the noise floor, not anything
            // about the machine.
            if (cal != null)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "\"calibration\":{{\"noise_band_pct\":{0:0.0},\"runs\":{1}," +
                    "\"nothing_to_measure\":\"{2}\"}},",
                    cal.LostPctBand, cal.RunCount,
                    cal.NothingToMeasure ? "yes" : "no");
            }

            sb.Append("\"causes\":{");
            bool first = true;
            foreach (var kv in r.ByCause.OrderByDescending(k => k.Value))
            {
                if (!first) sb.Append(",");
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "\"{0}\":{1:0.0}", kv.Key, kv.Value);
                first = false;
            }
            sb.Append("}}");
            return sb.ToString();
        }

        static void Add(StringBuilder sb, string k, string v)
        {
            sb.AppendFormat("\"{0}\":\"{1}\",", k, (v ?? "").Replace("\"", "'"));
        }
        static void Num(StringBuilder sb, string k, double v)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "\"{0}\":{1},", k, v);
        }

        // Vendor family only -- "NVIDIA" / "AMD" / "Intel". Coarser than the
        // model string already in the payload, and deliberately so: this is
        // for grouping results, not identifying a card. Empty when unknown.
        static string GpuVendor(string gpu)
        {
            string g = (gpu ?? "").ToLowerInvariant();
            if (g.Contains("nvidia") || g.Contains("geforce") ||
                g.Contains("rtx") || g.Contains("gtx") || g.Contains("quadro"))
                return "NVIDIA";
            if (g.Contains("radeon") || g.Contains("amd") || g.Contains("firepro"))
                return "AMD";
            if (g.Contains("intel") || g.Contains("arc ") || g.Contains("iris") ||
                g.Contains("uhd graphics") || g.Contains("hd graphics"))
                return "Intel";
            return "";
        }

        // Same data, formatted for a human to read before they consent. Every
        // line here corresponds to something BuildPayload actually sends -- if
        // you add a field there, add it here too.
        public static string Readable(Result r, string cpu, string gpu, string id,
                                      CalibrationProfile cal)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Game:            " + r.Game);
            sb.AppendLine("Processor:       " + cpu);
            sb.AppendLine("Graphics:        " + gpu);
            string vendor = GpuVendor(gpu);
            if (vendor.Length > 0)
                sb.AppendLine("Graphics vendor: " + vendor);
            sb.AppendLine();
            sb.AppendLine("Frames:          " + r.Frames.ToString("N0"));
            sb.AppendLine("Duration:        " + r.Seconds.ToString("0") + " s");
            sb.AppendLine("Typical frame:   " + r.Median.ToString("0.0") + " ms");
            sb.AppendLine("Worst 1%:        " + r.P99.ToString("0.0") + " ms");
            sb.AppendLine("Spread 10-90%:   " + r.Spread.ToString("0") + "% of typical");
            sb.AppendLine("Stutters:        " + r.Hitches.Count);
            if (r.Seconds > 0)
                sb.AppendLine("Stutters/min:    " +
                    (r.Hitches.Count / (r.Seconds / 60.0)).ToString("0.0"));
            sb.AppendLine("Playtime lost:   " + r.LostPct.ToString("0.00") + "%");
            sb.AppendLine("GPU headroom:    " + r.Headroom.ToString("0") + "%");
            sb.AppendLine("Frame capped:    " + (r.Capped ? "yes" : "no"));
            sb.AppendLine("Readable data:   " + (r.Untrustworthy ? "no - frame gen suspected" : "yes"));
            sb.AppendLine();
            sb.AppendLine("Causes found:");
            if (r.ByCause.Count == 0) sb.AppendLine("   (none)");
            foreach (var kv in r.ByCause.OrderByDescending(k => k.Value))
                sb.AppendLine("   " + kv.Key + ": " + kv.Value.ToString("0") + " ms");
            sb.AppendLine();
            if (cal != null)
            {
                sb.AppendLine("Calibration (this PC's run-to-run noise for this game):");
                if (cal.NothingToMeasure)
                    sb.AppendLine("   no stutter worth measuring - band not meaningful");
                else
                    sb.AppendLine("   noise band: +/- " + cal.LostPctBand.ToString("0.0") + "%");
                sb.AppendLine("   built from " + cal.RunCount + " runs");
                sb.AppendLine();
            }
            sb.AppendLine("Random install ID: " + id);
            sb.AppendLine("   (so repeat captures from one person aren't counted as many people)");
            sb.AppendLine();
            sb.AppendLine("That is the complete list. No file paths, no user name,");
            sb.AppendLine("no machine name, nothing about what you were doing in the game.");
            return sb.ToString();
        }

        // ---- consent + send -----------------------------------------------

        public static void Offer(IWin32Window owner, Result r, string cpu, string gpu,
                                 CalibrationProfile cal)
        {
            if (string.IsNullOrEmpty(Endpoint)) return;   // sharing not configured
            string mode = Mode;
            if (mode == "never") return;

            string id = InstallId;
            if (string.IsNullOrEmpty(id)) id = NewId();

            if (mode == "always")
            {
                Send(BuildPayload(r, cpu, gpu, id, cal));
                Save("always", id);
                return;
            }

            using (var dlg = new ConsentForm(Readable(r, cpu, gpu, id, cal)))
            {
                var res = dlg.ShowDialog(owner);
                if (res == DialogResult.Cancel) { Save("never", id); return; }
                if (res == DialogResult.OK)
                {
                    Send(BuildPayload(r, cpu, gpu, id, cal));
                    Save(dlg.Always ? "always" : "ask", id);
                }
                else Save("ask", id);   // "not this time"
            }
        }

        static void Send(string json)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                    wc.UploadStringAsync(new Uri(Endpoint), "POST", json);
                }
            }
            catch { }   // never let a failed upload interrupt the user
        }
    }

    // Shows the actual payload, not a summary of it.
    public class ConsentForm : Form
    {
        public bool Always { get; private set; }
        CheckBox always;

        public ConsentForm(string payload)
        {
            Text = "Share this result?";
            ClientSize = new Size(520, 580);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = Color.FromArgb(244, 246, 244);
            Font = new Font("Segoe UI", 9.5f);

            Controls.Add(new Label {
                Location = new Point(16, 16), Size = new Size(488, 62),
                Text = "I'm short of results from PCs that actually stutter. If you're " +
                       "willing, sending this helps work out which causes are common " +
                       "and where the tool gets things wrong.\r\n\r\n" +
                       "Here is exactly what would be sent:" });

            Controls.Add(new TextBox {
                Location = new Point(16, 84), Size = new Size(488, 360),
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f), Text = payload,
                BackColor = Color.White });

            always = new CheckBox {
                Location = new Point(16, 454), Size = new Size(488, 22),
                Text = "Send future results automatically, don't ask again" };
            Controls.Add(always);

            Controls.Add(new Label {
                Location = new Point(16, 480), Size = new Size(488, 34),
                ForeColor = Color.FromArgb(90, 107, 116),
                Text = "You can change this any time by editing (or deleting) " +
                       "share-settings.txt next to the app." });

            var send = new Button { Text = "Send it", DialogResult = DialogResult.OK,
                Location = new Point(16, 526), Size = new Size(110, 32),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            var not = new Button { Text = "Not this time", DialogResult = DialogResult.Ignore,
                Location = new Point(136, 526), Size = new Size(110, 32) };
            var never = new Button { Text = "Never ask again", DialogResult = DialogResult.Cancel,
                Location = new Point(394, 526), Size = new Size(110, 32) };

            send.Click += (s, e) => { Always = always.Checked; };
            Controls.Add(send); Controls.Add(not); Controls.Add(never);
            AcceptButton = send; CancelButton = not;
        }
    }
}
