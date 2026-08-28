// Compare.cs -- the A/B proof loop.
//
// A single capture can only *infer* shader compilation from a decay pattern.
// That's a heuristic and it can be wrong. Two captures of the same route can
// prove it, because the two causes behave differently over repeat runs:
//
//   shader compilation -> happens once, then cached. Gone on run 2.
//   asset streaming    -> happens every time you enter the area. Still there.
//
// So: whatever disappears was compilation. Whatever comes back is the engine.
// This also works for settings changes: record, change one thing, record
// again, and see whether it actually helped instead of guessing.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StutterTest
{
    public class Comparison
    {
        public Result A, B;
        public string Headline = "", Verdict = "", VClass = "good";
        public double LostDeltaPct;          // % change in playtime lost
        public double VanishedMs, PersistedMs;
        public List<string> Vanished = new List<string>();
        public List<string> Persisted = new List<string>();
        public List<string> Appeared = new List<string>();
        public string Caveat = "";

        // A cause counts as "gone" if it lost this much of its magnitude.
        const double VanishThreshold = 0.60;
        // ...and as "unchanged" if it stayed within this band either way.
        const double PersistBand = 0.25;

        public static Comparison Run(Result before, Result after)
        {
            var c = new Comparison { A = before, B = after };

            // Comparing captures of different lengths or different games is
            // meaningless, so normalise to loss-per-second and warn loudly.
            double aRate = before.Seconds > 0 ? before.TotalLost / before.Seconds : 0;
            double bRate = after.Seconds > 0 ? after.TotalLost / after.Seconds : 0;
            c.LostDeltaPct = aRate > 0 ? (bRate - aRate) / aRate * 100.0 : 0;

            if (!string.Equals(before.Game, after.Game, StringComparison.OrdinalIgnoreCase))
                c.Caveat = "These are two different games, so the comparison " +
                    "doesn't mean much. Record the same game twice.";
            else if (Math.Abs(before.Seconds - after.Seconds) > before.Seconds * 0.5)
                c.Caveat = "The two captures are very different lengths. " +
                    "Numbers are normalised per second, but a like-for-like " +
                    "run gives a much clearer answer.";

            var causes = before.ByCause.Keys.Union(after.ByCause.Keys).Distinct();
            foreach (var cause in causes)
            {
                double a = before.ByCause.ContainsKey(cause) ? before.ByCause[cause] : 0;
                double b = after.ByCause.ContainsKey(cause) ? after.ByCause[cause] : 0;
                // normalise per second so uneven capture lengths don't lie
                double an = before.Seconds > 0 ? a / before.Seconds : 0;
                double bn = after.Seconds > 0 ? b / after.Seconds : 0;

                if (an < 0.5 && bn < 0.5) continue;   // too small to talk about

                if (an > 0 && bn <= an * (1 - VanishThreshold))
                {
                    c.Vanished.Add(cause);
                    c.VanishedMs += a - (bn * before.Seconds);
                }
                else if (an > 0 && Math.Abs(bn - an) <= an * PersistBand)
                {
                    c.Persisted.Add(cause);
                    c.PersistedMs += a;
                }
                else if (an <= 0.5 && bn > 0.5)
                {
                    c.Appeared.Add(cause);
                }
            }

            BuildVerdict(c);
            return c;
        }

        static string Name(string cause)
        {
            switch (cause)
            {
                case "shader_warmup": return "shader compilation";
                case "cpu_stall": return "engine asset streaming";
                case "gpu_spike": return "GPU render spikes";
                case "pacing": return "uneven frame delivery";
                case "thermal_throttle": return "thermal throttling";
                case "present_path": return "display/sync stalls";
                case "mixed": return "mixed-cause hitches";
                default: return cause;
            }
        }

        static void BuildVerdict(Comparison c)
        {
            bool improved = c.LostDeltaPct <= -25;
            bool worse = c.LostDeltaPct >= 25;
            bool same = !improved && !worse;

            // The headline case: something disappeared and something didn't.
            // That's the proof the single-capture version can only guess at.
            if (c.Vanished.Count > 0 && c.Persisted.Count > 0)
            {
                c.Headline = "Part of it was temporary. Part of it wasn't.";
                c.VClass = "warn";
                c.Verdict = string.Format(CultureInfo.InvariantCulture,
                    "Stutter dropped {0:0}% between the two runs. " +
                    "{1} disappeared, which means it was a one-time cost — most " +
                    "likely your GPU compiling effects it hadn't seen before. " +
                    "But {2} came back at almost the same level, so that part " +
                    "isn't going anywhere. That's the game, not your PC.",
                    Math.Abs(c.LostDeltaPct),
                    Cap(string.Join(" and ", c.Vanished.Select(Name))),
                    string.Join(" and ", c.Persisted.Select(Name)));
                return;
            }

            if (c.Vanished.Count > 0 && improved)
            {
                c.Headline = "It was temporary. It's gone now.";
                c.VClass = "good";
                c.Verdict = string.Format(CultureInfo.InvariantCulture,
                    "Stutter dropped {0:0}% on the second run and {1} disappeared " +
                    "entirely. That's a one-time cost you've now paid. Keep playing.",
                    Math.Abs(c.LostDeltaPct),
                    Cap(string.Join(" and ", c.Vanished.Select(Name))));
                return;
            }

            if (same && c.Persisted.Count > 0)
            {
                c.Headline = "Nothing changed. This one's permanent.";
                c.VClass = "bad";
                c.Verdict = string.Format(CultureInfo.InvariantCulture,
                    "Both runs came out within {0:0}% of each other, and {1} " +
                    "showed up identically in both. Whatever's causing this " +
                    "happens every single time — it isn't warming up and it " +
                    "isn't going to settle down. If the second run was after a " +
                    "settings change, that change did nothing.",
                    Math.Abs(c.LostDeltaPct),
                    string.Join(" and ", c.Persisted.Select(Name)));
                return;
            }

            if (worse)
            {
                c.Headline = "It got worse.";
                c.VClass = "bad";
                c.Verdict = string.Format(CultureInfo.InvariantCulture,
                    "The second run lost {0:0}% more time to stutter than the " +
                    "first. If you changed a setting between runs, change it " +
                    "back. If you didn't, something else on your machine did — " +
                    "heat, a background process, or a different route through " +
                    "the game.", c.LostDeltaPct);
                return;
            }

            if (improved)
            {
                c.Headline = "Better on the second run.";
                c.VClass = "good";
                c.Verdict = string.Format(CultureInfo.InvariantCulture,
                    "Stutter dropped {0:0}%. Not enough detail to say exactly " +
                    "which cause went away, but whatever you changed — or the " +
                    "cache warming up — helped.", Math.Abs(c.LostDeltaPct));
                return;
            }

            c.Headline = "Both runs look the same.";
            c.VClass = "good";
            c.Verdict = "Neither capture found enough stutter to compare " +
                "meaningfully. That's usually good news.";
        }

        static string Cap(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
        }

        // ---- report -------------------------------------------------------

        public string WriteReport(string dir, string template)
        {
            string rows = "";
            foreach (var cause in A.ByCause.Keys.Union(B.ByCause.Keys).Distinct())
            {
                double a = A.ByCause.ContainsKey(cause) ? A.ByCause[cause] : 0;
                double b = B.ByCause.ContainsKey(cause) ? B.ByCause[cause] : 0;
                if (a < 5 && b < 5) continue;
                string tag, col;
                if (Vanished.Contains(cause)) { tag = "gone"; col = "#3d7a52"; }
                else if (Persisted.Contains(cause)) { tag = "unchanged"; col = "#c2452c"; }
                else if (Appeared.Contains(cause)) { tag = "new"; col = "#b3781f"; }
                else { tag = "changed"; col = "#5a6b74"; }
                rows += string.Format(CultureInfo.InvariantCulture,
                    "<tr><td>{0}</td><td class='n'>{1:0} ms</td><td class='n'>{2:0} ms</td>" +
                    "<td class='t' style='color:{3}'>{4}</td></tr>",
                    Esc(Name(cause)), a, b, col, tag);
            }

            string caveat = string.IsNullOrEmpty(Caveat) ? ""
                : "<div class='caveat'><b>Worth knowing</b><p>" + Esc(Caveat) + "</p></div>";

            string html = template
                .Replace("{{GAME}}", Esc(A.Game))
                .Replace("{{WHEN}}", DateTime.Now.ToString("d MMM yyyy  HH:mm"))
                .Replace("{{HEADLINE}}", Esc(Headline))
                .Replace("{{VERDICT}}", Esc(Verdict))
                .Replace("{{VCLASS}}", VClass)
                .Replace("{{CAVEAT}}", caveat)
                .Replace("{{SVG_A}}", Report.Svg(A, 110))
                .Replace("{{SVG_B}}", Report.Svg(B, 110))
                .Replace("{{HITCH_A}}", A.Hitches.Count.ToString())
                .Replace("{{HITCH_B}}", B.Hitches.Count.ToString())
                .Replace("{{LOST_A}}", A.TotalLost.ToString("0"))
                .Replace("{{LOST_B}}", B.TotalLost.ToString("0"))
                .Replace("{{PCT_A}}", A.LostPct.ToString("0.00"))
                .Replace("{{PCT_B}}", B.LostPct.ToString("0.00"))
                .Replace("{{WORST_A}}", (A.Trace.Count > 0 ? A.Trace.Max() : 0).ToString("0"))
                .Replace("{{WORST_B}}", (B.Trace.Count > 0 ? B.Trace.Max() : 0).ToString("0"))
                .Replace("{{DELTA}}", (LostDeltaPct <= 0 ? "" : "+") + LostDeltaPct.ToString("0"))
                .Replace("{{ROWS}}", rows);

            string path = Path.Combine(dir, "comparison.html");
            File.WriteAllText(path, html, Encoding.UTF8);
            return path;
        }

        static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
