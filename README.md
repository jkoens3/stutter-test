# Stutter Test

Tells you **why** your PC game stutters — and whether it's going to stop.

### [→ Download it here](https://github.com/jkoens3/stutter-test/releases/latest)

Grab all three files into one folder, then run `StutterTest.exe`.

---

Frametime graphs show you *that* you stuttered. They don't tell you what to do
about it. This classifies the cause and gives you a straight answer.

![report](screenshot.png)

## What it tells you

- **Temporary** — your GPU is compiling shaders the first time it meets new
  effects, then caching them. This will stop, usually within about 30 minutes
  of play. The game isn't broken and neither is your PC.
- **Fixable** — frame pacing, thermal throttling, or display sync problems,
  with the specific thing to change.
- **Not fixable** — the game's engine stalling while it streams assets. No
  setting on your end changes this. It's how the game was built.
- **Can't tell** — the test couldn't work. If your frame rate is capped and
  your GPU is idle part of every frame, stutter physically can't show up, so a
  clean result would be meaningless. It says so instead of pretending you're fine.

## How to use it

1. Download all three files from the [latest release](https://github.com/jkoens3/stutter-test/releases/latest)
   into one folder
2. Start your game and get into actual gameplay
3. Run `StutterTest.exe` — Windows will ask for administrator rights, say yes
4. Click **Find my game**, then **Record**
5. Alt-Tab back into the game and play for a minute — keep moving, go into new
   areas, that's where stutter lives
6. Read the verdict, or click **Open full report** for the detail

## Is this safe?

Reasonable question for any executable that asks for administrator rights.

- **It's code signed.** Signed through Microsoft Artifact Signing, so Windows
  shows a verified publisher rather than an unknown one.
- **VirusTotal: [1 of 71](https://www.virustotal.com/gui/file/8084509ee7dd33506cfa4d57b0cd278b9c8c0ef4869eea0b2fd7a39c03c0ac56)** —
  a single heuristic flag from one scanner. Every major engine is clean:
  Microsoft Defender, Kaspersky, BitDefender, ESET, Sophos, Symantec,
  Malwarebytes, CrowdStrike, Avast, TrendMicro.
- **Why admin:** reading Windows performance traces requires elevation. That's
  the only reason. Same requirement as PresentMon itself, or FPS Monitor.
- **No overlay, no injection.** Nothing is loaded into the game process. It
  reads trace events the OS already produces. That's also why anti-cheat
  doesn't object — tested against Easy Anti-Cheat.
- **Nothing leaves your PC.** There is no network code in this application at
  all. It writes a CSV and an HTML file next to the exe.
- **Nothing is changed.** It doesn't touch your settings, drivers, registry, or
  game files. It only reads.

Full source is in this repo — a single C# file. If you'd rather not run my
binary, `BUILD.bat` compiles it with the C# compiler already included in
Windows. Takes about ten seconds and you never have to trust me.

## Built with AI assistance

I'm not a C# developer. The code was written with AI, and I'd rather say that
up front than have it come up later.

What I did do was test it against real captures until it stopped being wrong.
Several things in here exist because the data contradicted the first version:

- It was reporting **+182% GPU thermal throttling** when a scene simply got
  heavier. Now it compares only the *lightest* frames in each window, and
  treats anything above 40% as a workload change, because silicon doesn't
  throttle by multiples.
- It gave a **clean bill of health to a frame-capped test** where stutter
  physically couldn't appear. Now it measures GPU headroom and refuses to
  give a verdict when the test can't work.
- It **claimed patterns from a handful of hitches**. Now it won't call a trend
  with fewer than 20 relevant data points.
- It **missed frame pacing entirely**, because uneven frame delivery never
  crosses a hitch threshold. That's now its own detection.

The methodology and the validation below are mine. Judge it on whether the
numbers hold up.

## How it works

Built on [Intel PresentMon](https://github.com/GameTechDev/PresentMon) (MIT
licensed), which reads frame timing from Windows' built-in ETW tracing.

It finds frames that took much longer than the surrounding ones, then splits
the lost time by where it went — CPU render work, GPU render work, or neither
(the present/display path). Session-level patterns separate causes that look
identical frame-by-frame: shader compilation decays as you play, thermal
throttling shows up as the lightest frames getting slower over time, and frame
pacing appears as alternating long/short frames that never trip a hitch
threshold at all.

### Does it actually work?

Same game, same route, twice — once with a cleared shader cache, once after:

| | Cold cache | Warm cache, identical route |
|---|---|---|
| Stutters | 34 | 5 |
| Playtime lost | 1.57% | 0.15% |

The tool predicted 90% of that stutter was shader compilation and would
disappear. It did. The portion it flagged as **permanent** engine stutter
stayed within 5% across both runs (94ms vs 90ms).

That's a falsifiable prediction, and it held. It's also the thing the tool is
actually for: telling those two apart while you're playing, when they feel
identical.

## Building it yourself

Put `StutterTest.cs`, `app.manifest`, `report_template.html`, and `BUILD.bat`
in a folder and double-click `BUILD.bat`. It uses the C# compiler already
included in Windows — no SDK, no Visual Studio.

You'll also need `PresentMon.exe` (from the
[PresentMon releases](https://github.com/GameTechDev/PresentMon/releases))
in the same folder to run it.

## Known limits

- Frame generation (DLSS FG / FSR FG) inserts synthetic frames, which makes
  frame timings mean something different. Results will be unreliable with it on.
- A 60-second capture can miss stutter that only happens when entering new
  areas. Record where the problem actually occurs.
- When the answer is "the engine," it currently stops there. Making that more
  specific is the next thing worth building.
- Tested on DX11, DX12 and Vulkan titles. Very old or unusual renderers may
  report less detail.

## Found a wrong answer?

That's the most useful thing you can send me. Open an issue with the CSV from
your `results` folder and what you were doing at the time. Every fix listed
above came from exactly that.

## License

MIT. PresentMon is separately MIT licensed by Intel.
