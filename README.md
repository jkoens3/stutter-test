# Stutter Test

Tells you **why** your PC game stutters — and whether it's going to stop.

Frametime graphs show you *that* you stuttered. They don't tell you what to do
about it. This classifies the cause and gives you a straight answer.

![report](screenshot.png)

## What it tells you

- **Temporary** — your GPU is compiling shaders for the first time. It caches
  them. This will stop, usually within about 30 minutes of play.
- **Fixable** — frame pacing, thermal throttling, or display sync problems,
  with the specific thing to change.
- **Not fixable** — the game's engine stalling while it loads assets. No
  setting on your end changes this.
- **Can't tell** — the test couldn't work. If your frame rate is capped and
  your GPU is idle part of every frame, stutter physically can't show up, so a
  clean result would be meaningless. It says so instead of pretending you're fine.

## How to use it

1. Download the latest release and unzip it
2. Start your game and get into actual gameplay
3. Run `StutterTest.exe` as administrator
4. Click **Find my game**, then **Record**
5. Alt-Tab back into the game and play for a minute — keep moving, go into new
   areas, that's where stutter lives
6. Read the verdict, or click **Open full report** for the detail

## How it works

Built on [Intel PresentMon](https://github.com/GameTechDev/PresentMon) (MIT
licensed), which reads frame timing from Windows' built-in ETW tracing.

It finds frames that took much longer than the surrounding ones, then splits
the lost time by where it went — CPU render work, GPU render work, or neither
(the present/display path). Session-level patterns separate causes that look
identical frame-by-frame: shader compilation decays as you play, thermal
throttling shows up as the *lightest* frames getting slower over time, and
frame pacing appears as alternating long/short frames that never trip a hitch
threshold at all.

It refuses to claim a pattern it can't support. Fewer than 20 relevant hitches
and it won't call a trend. GPU slowdown above 40% is treated as a workload
change rather than thermals, because silicon doesn't throttle by multiples.

## Is this safe?

Reasonable question — it's an unsigned executable that asks for administrator
rights. So:

- **Why admin:** reading Windows performance traces requires it. That's the
  only reason. Same requirement as PresentMon itself, or FPS Monitor.
- **No overlay, no injection.** Nothing is loaded into the game process. It
  reads trace events the OS already produces. This is also why it doesn't
  upset anti-cheat.
- **Nothing leaves your PC.** No network code at all. It writes a CSV and an
  HTML file next to the exe.
- **Nothing is changed.** It doesn't touch your settings, drivers, registry,
  or game files. It only reads.

The full source is in this repo — it's a single C# file. Read it, or build it
yourself with `BUILD.bat`, which uses the C# compiler already included in
Windows. No SDK needed.

## Building it yourself

Put `StutterTest.cs`, `report_template.html`, and `BUILD.bat` in a folder and
double-click `BUILD.bat`. You'll also need `PresentMon.exe` (from the
[PresentMon releases](https://github.com/GameTechDev/PresentMon/releases))
in the same folder to run it.

## Known limits

- Frame generation (DLSS FG / FSR FG) inserts synthetic frames, which makes
  frame timings mean something different. Results will be unreliable with it on.
- A 60-second capture can miss stutter that only happens when entering new
  areas. Record where the problem actually occurs.
- Tested on DX11, DX12 and Vulkan titles. Very old or unusual renderers may
  report less detail.

## License

MIT. PresentMon is separately MIT licensed by Intel.
