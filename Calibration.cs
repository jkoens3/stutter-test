// Calibration.cs -- establish a machine's run-to-run noise floor.
//
// Comparison.Run can tell you run B lost less time than run A. What it can't
// tell you, on its own, is whether that difference is real or just this PC
// being noisy. Two captures of the same route with NOTHING changed between
// them measure that noise directly. Store it per game, and a later A/B can
// be called "inconclusive" when the delta sits inside the floor.
//
// Ported from the Pro version, with one behavioural change for this free
// build: there is no calibrate button. AutoUpdate() runs after every
// recording and silently (re)builds the profile whenever the results folder
// already holds enough similar captures of the same game. The user does
// nothing and sees nothing -- the profile is just there when a later A/B
// needs it.
//
// Storage: %LOCALAPPDATA%\StutterTest\calibration\<safe-game>.json
// The noise floor is a property of THIS machine and must never sync to
// another one, so LocalApplicationData (non-roaming) is the right home.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace StutterTest
{
    [DataContract]
    public class CalibrationProfile
    {
        // Bump when the shape below changes in a way an old reader can't cope
        // with. Load() tolerates a missing/other value by returning null.
        public const int CurrentSchema = 1;

        [DataMember(Order = 0)] public int SchemaVersion { get; set; }
        [DataMember(Order = 1)] public string Game { get; set; }
        [DataMember(Order = 2)] public string CreatedUtc { get; set; }   // ISO 8601, UTC
        [DataMember(Order = 3)] public int RunCount { get; set; }
        [DataMember(Order = 4)] public List<string> SourceFiles { get; set; }

        // Mean, across the calibration runs, of each run's LostPct with the
        // shader_warmup bucket removed. Percentage points of playtime, same
        // units as Analyzer.LostPct. This is the absolute floor: a relative
        // band means nothing when this is near zero.
        [DataMember(Order = 5)] public double LostPctMean { get; set; }

        // Run-to-run spread of that same figure, as a RELATIVE percent of the
        // mean (sample stddev / mean * 100, the coefficient of variation),
        // THEN multiplied by SmallSampleFactor to pad for sample size. This is
        // the working band -- it lines up directly with Comparison's
        // LostDeltaPct. Zero (and not meaningful) when NothingToMeasure is set.
        [DataMember(Order = 6)] public double LostPctBand { get; set; }

        // The small-sample multiplier that was applied to the raw coefficient
        // of variation to get LostPctBand. Divide LostPctBand by this to
        // recover the raw spread. 1.0 once RunCount is comfortably large, and
        // 1.0 when NothingToMeasure is set.
        [DataMember(Order = 12)] public double SmallSampleFactor { get; set; }

        // Set when LostPctMean < Calibration.NegligibleLossPct: this game on
        // this machine has no stutter worth measuring, so there is no relative
        // band to report and nothing for a later comparison to improve on.
        [DataMember(Order = 7)] public bool NothingToMeasure { get; set; }

        // Guard values -- plain averages. If a later A/B pair doesn't sit near
        // these, it wasn't recorded under the same conditions this calibration
        // was, and the comparison should be treated with suspicion.
        [DataMember(Order = 8)] public double MedianMean { get; set; }
        [DataMember(Order = 9)] public double SpreadMean { get; set; }
        [DataMember(Order = 10)] public double HeadroomMean { get; set; }

        // Populated only once RunCount >= Calibration.MinRunsForPerCause. Null
        // below that: two runs can't support a per-cause noise estimate. Keys
        // are Analyzer cause names (never shader_warmup); values are a relative
        // percent band like LostPctBand.
        [DataMember(Order = 11)] public Dictionary<string, double> PerCauseBands { get; set; }
    }

    public static class Calibration
    {
        // Fewest captures we'll build any profile from. Two runs give a band
        // that swings wildly from pair to pair (seen: +-12% one pair, +-27% the
        // next, same game, same warm cache), so a standard deviation needs at
        // least three points to mean anything.
        public const int MinRuns = 3;

        // Below this many runs we publish only the global LostPct band. A
        // per-cause breakdown needs more points than a handful of runs can give.
        public const int MinRunsForPerCause = 5;

        // Multiplier applied to a calibrated band before Comparison is allowed
        // to call a change real (delta must exceed SafetyMultiplier * band).
        //
        // PROVISIONAL. 2.0 is a guess. It needs validating against a corpus of
        // real back-to-back captures before it should be trusted: raise it if
        // noise leaks through as "real change", lower it if genuine changes
        // get buried as "inconclusive". Not used yet -- Comparison is untouched.
        public const double SafetyMultiplier = 2.0;

        // Largest fractional gap allowed between the calibration runs' median
        // frame times. Beyond this, Build() rejects the pair: two runs of the
        // same route at the same settings shouldn't have medians this far apart.
        // AutoUpdate() applies the same limit up front when deciding which of
        // the folder's captures count as "the same test".
        //
        // PROVISIONAL. 0.10 is a guess. It needs validating against a corpus of
        // real back-to-back captures before it should be trusted: raise it if
        // legitimate same-route runs get rejected, lower it if mismatched
        // captures slip through and pollute the noise floor.
        public const double MedianDivergenceLimit = 0.10;

        // Mean LostPct (in percentage points) below which a relative noise band
        // is meaningless -- a ±47% band off a 0.03% mean tells you nothing. Below
        // this the profile just records that there's no stutter here to measure.
        //
        // PROVISIONAL. 0.25 is a guess. It needs validating against a corpus of
        // real back-to-back captures before it should be trusted: raise it if
        // near-clean games still produce junk bands, lower it if games with real
        // (if small) stutter get dismissed as nothing.
        public const double NegligibleLossPct = 0.25;

        // Factor the raw noise band is multiplied by when RunCount sits at
        // MinRuns, easing toward 1.0 as runs are added -- see WidenFactor().
        // Three runs barely pin down a standard deviation, so the raw spread
        // understates the real one; this pads it.
        //
        // PROVISIONAL. 1.5 is a guess. It needs validating against a corpus of
        // real back-to-back captures before it should be trusted: raise it if
        // minimum-run bands still let noise through as real change, lower it if
        // they swallow genuine changes.
        public const double SmallSampleWiden = 1.5;

        const string AppFolder = "StutterTest";
        const string SubFolder = "calibration";

        public static string Dir()
        {
            string root = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(root, AppFolder), SubFolder);
        }

        // Same mapping StutterTest uses for CSV names, kept in lockstep so a
        // game's captures and its calibration file share a stem.
        public static string SafeName(string game)
        {
            string bare = (game ?? "").Replace(".exe", "");
            return System.Text.RegularExpressions.Regex.Replace(bare, "[^A-Za-z0-9]", "");
        }

        public static string PathFor(string game)
        {
            return Path.Combine(Dir(), SafeName(game) + ".json");
        }

        // ---- automatic, no-button calibration --------------------------
        //
        // Called from MainForm.Finish after a capture has been analysed and
        // saved. Scans the results folder for earlier captures of the same
        // game at a similar median frame time; if enough exist, (re)builds
        // the profile and saves it. Returns the profile it wrote, or null if
        // there wasn't enough to build one (or anything at all went wrong --
        // calibration must never disrupt the recording flow, and must never
        // surface an error to the user).
        public static CalibrationProfile AutoUpdate(Result latest, string resultsDir)
        {
            try
            {
                if (latest == null || latest.Median <= 0) return null;
                if (latest.Frames < 600 || latest.TooShort || latest.Untrustworthy)
                    return null;
                if (string.IsNullOrEmpty(resultsDir) || !Directory.Exists(resultsDir))
                    return null;

                string stem = SafeName(latest.Game);
                if (stem.Length == 0) return null;

                var runs = new List<Result>();
                var files = new List<string>();

                foreach (var csv in Directory.GetFiles(resultsDir, "*.csv"))
                {
                    // Cheap stem match first; Analyzer.Run confirms the game
                    // from the CSV's own application column below.
                    if (!Path.GetFileName(csv).StartsWith(
                            stem + "-", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Result r;
                    try { r = Analyzer.Run(csv, latest.Game); }
                    catch { continue; }

                    if (r.Frames < 600 || r.TooShort || r.Untrustworthy) continue;
                    if (r.Median <= 0) continue;
                    if (!string.Equals(r.Game, latest.Game, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // "Similar median frame time" -- the same gate Build()
                    // enforces between runs, applied here per-file against the
                    // capture we just took so a different route or settings
                    // change never enters the noise floor.
                    if (Math.Abs(r.Median - latest.Median) / latest.Median
                        > MedianDivergenceLimit)
                        continue;

                    runs.Add(r);
                    files.Add(Path.GetFileName(csv));
                }

                // MinRuns counts the capture we just took (it's in the folder
                // now), so this is "two or more earlier captures at a similar
                // median" before a profile appears.
                if (runs.Count < MinRuns) return null;

                var profile = Build(runs, files);
                Save(profile);
                return profile;
            }
            catch { return null; }
        }

        // ---- build -------------------------------------------------------

        public static CalibrationProfile Build(IList<Result> runs, IList<string> sourceFiles)
        {
            if (runs == null || runs.Count < MinRuns)
                throw new ArgumentException(
                    "Calibration needs at least " + MinRuns + " captures of the same " +
                    "game with nothing changed between them.");

            string game = runs[0].Game;
            foreach (var r in runs)
            {
                if (r.Frames < 600)
                    throw new ArgumentException(
                        "One capture has only " + r.Frames + " frames. Every run needs " +
                        "at least 600.");
                if (r.TooShort)
                    throw new ArgumentException(
                        "One capture is only " + r.Seconds.ToString("0") + " seconds -- " +
                        "too short to calibrate from.");
                if (r.Untrustworthy)
                    throw new ArgumentException(
                        "One capture is untrustworthy (frame generation or contradictory " +
                        "timings). Calibrate from clean runs only.");
                if (!string.Equals(r.Game, game, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        "These captures are from different games (" + game + " vs " +
                        r.Game + "). Calibrate one game at a time.");
            }

            var lostPct = new List<double>();
            var median = new List<double>();
            var spread = new List<double>();
            var headroom = new List<double>();
            foreach (var r in runs)
            {
                lostPct.Add(NonWarmupLostPct(r));
                median.Add(r.Median);
                spread.Add(r.Spread);
                headroom.Add(r.Headroom);
            }

            // Guard: the runs have to be OF THE SAME THING. Medians this far
            // apart mean a different route, or settings that changed.
            double medLo = double.MaxValue, medHi = 0;
            foreach (double m in median)
            {
                if (m < medLo) medLo = m;
                if (m > medHi) medHi = m;
            }
            if (medLo > 0 && (medHi - medLo) / medLo > MedianDivergenceLimit)
                throw new ArgumentException(
                    "The captures' median frame times differ by " +
                    ((medHi - medLo) / medLo * 100.0).ToString("0") + "% (" +
                    medLo.ToString("0.0") + " ms vs " + medHi.ToString("0.0") +
                    " ms). That doesn't look like the same route at the same " +
                    "settings. Calibration needs runs of the same thing with " +
                    "nothing changed between them.");

            double lpMean = Mean(lostPct);

            // Below the negligible floor a relative band is noise off noise
            // (a ±47% band from a 0.03% mean). Record "nothing to measure"
            // instead and leave the band at zero.
            bool nothing = lpMean < NegligibleLossPct;

            double widen = 1.0;
            double band = 0;
            if (!nothing)
            {
                double rawCv = lpMean > 0 ? SampleStdDev(lostPct) / lpMean * 100.0 : 0;
                widen = WidenFactor(runs.Count);
                band = rawCv * widen;
            }

            return new CalibrationProfile
            {
                SchemaVersion = CalibrationProfile.CurrentSchema,
                Game = game,
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                RunCount = runs.Count,
                SourceFiles = sourceFiles != null
                    ? new List<string>(sourceFiles) : new List<string>(),
                LostPctMean = lpMean,
                LostPctBand = band,
                SmallSampleFactor = widen,
                NothingToMeasure = nothing,
                MedianMean = Mean(median),
                SpreadMean = Mean(spread),
                HeadroomMean = Mean(headroom),
                PerCauseBands = (!nothing && runs.Count >= MinRunsForPerCause)
                    ? PerCause(runs) : null
            };
        }

        // Non-warmup loss as a percentage of playtime, mirroring the LostPct
        // formula in Analyzer.Run. Public so callers can show the exact per-run
        // figures the band is derived from.
        public static double NonWarmupLostPct(Result r)
        {
            double lost = r.TotalLost - Bucket(r, "shader_warmup");
            if (lost < 0) lost = 0;
            return r.Seconds > 0 ? lost / (r.Seconds * 1000.0) * 100.0 : 0;
        }

        static double Bucket(Result r, string cause)
        {
            double v;
            return r.ByCause != null && r.ByCause.TryGetValue(cause, out v) ? v : 0;
        }

        // Per-cause relative bands. Only meaningful with several runs. Each
        // cause is a per-second rate; shader_warmup is excluded entirely.
        static Dictionary<string, double> PerCause(IList<Result> runs)
        {
            var names = new HashSet<string>();
            foreach (var r in runs)
                if (r.ByCause != null)
                    foreach (var k in r.ByCause.Keys)
                        if (k != "shader_warmup") names.Add(k);

            var outp = new Dictionary<string, double>();
            foreach (var name in names)
            {
                var rates = new List<double>();
                foreach (var r in runs)
                    rates.Add(r.Seconds > 0 ? Bucket(r, name) / r.Seconds : 0);
                double m = Mean(rates);
                outp[name] = m > 0 ? SampleStdDev(rates) / m * 100.0 : 0;
            }
            return outp;
        }

        static double Mean(IList<double> v)
        {
            if (v.Count == 0) return 0;
            double s = 0;
            for (int i = 0; i < v.Count; i++) s += v[i];
            return s / v.Count;
        }

        static double SampleStdDev(IList<double> v)
        {
            if (v.Count < 2) return 0;
            double m = Mean(v), ss = 0;
            for (int i = 0; i < v.Count; i++) { double d = v[i] - m; ss += d * d; }
            return Math.Sqrt(ss / (v.Count - 1));
        }

        // SmallSampleWiden at RunCount == MinRuns, tapering toward 1.0 as runs
        // are added: 1 + (SmallSampleWiden - 1) * MinRuns / RunCount.
        //   3 runs -> 1.50   5 runs -> 1.30   8 runs -> 1.19   -> 1.0
        static double WidenFactor(int runCount)
        {
            int n = runCount < MinRuns ? MinRuns : runCount;
            return 1.0 + (SmallSampleWiden - 1.0) * MinRuns / (double)n;
        }

        // ---- persistence -----------------------------------------------

        public static string Save(CalibrationProfile p)
        {
            string dir = Dir();
            Directory.CreateDirectory(dir);
            string path = PathFor(p.Game);
            var ser = new DataContractJsonSerializer(typeof(CalibrationProfile));
            using (var fs = File.Create(path))
                ser.WriteObject(fs, p);
            return path;
        }

        public static CalibrationProfile Load(string game)
        {
            string path = PathFor(game);
            if (!File.Exists(path)) return null;
            try
            {
                var ser = new DataContractJsonSerializer(typeof(CalibrationProfile));
                using (var fs = File.OpenRead(path))
                    return (CalibrationProfile)ser.ReadObject(fs);
            }
            catch { return null; }
        }
    }
}
