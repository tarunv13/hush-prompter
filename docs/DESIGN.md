# How Hush works

## Shape of the code

```
src/Hush
  Core/      pure logic, no UI: cue parsing, scroll engine, voice detector, tempo, word aligner,
             model router, importers, note store, settings
  Audio/     microphone capture (winmm), WAV playback for tests, voice pipeline, offline recogniser
  Ai/        provider chain: Claude Code CLI, Claude API, Ollama; prompts
  Native/    capture exclusion, window styles, global hotkeys
  UI/        main window, prompter window, settings, calibration, AI preview
tests/
  Hush.Tests  dependency-free runner over Core, Audio and Ai (77 offline + 5 live tests)
  e2e         drives the built app with synthesised speech and reads its status file
```

The app targets .NET Framework 4.8, which ships with Windows 10 (1903+) and Windows 11, so the executable is about 250 KB and needs no runtime. All UI is WPF, built in code; shared styles live in `App.xaml`.

## The prompter window

A borderless, transparent, top-most window. In notch mode it is centred at the top of the screen the main window is on; the visible panel is a path with square shoulders, small curved "ears" where it meets the top edge, and rounded lower corners. Transparent areas let clicks through. The window has `WS_EX_NOACTIVATE`, so clicking it never steals focus from your call app, and `WS_EX_TOOLWINDOW`, so it has no taskbar button.

`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` removes it from every capture path that respects the flag: screenshots, screen recording and screen sharing. The end-to-end test proves this by grabbing the screen region and checking that the black panel is absent, and present again when hiding is turned off.

Text is a read-only `TextBox`, chosen because it can report the exact position of any character. That lets cue jumps and word follow scroll to an exact line. Every frame (`CompositionTarget.Rendering`) advances the scroll engine by the measured frame time and moves the text with a transform. The fade at the top and bottom is an opacity mask in absolute pixels, so it stays fixed while the text moves.

## Voice

Capture is 16 kHz mono PCM through `waveIn` with an event handle and a dedicated thread, so no native callback can outlive its delegate. Each 10 ms frame is filtered (two 150 Hz high-pass sections and a 4 kHz low-pass, removing mains hum by about 38 dB) and measured in dBFS.

- **Detection.** The threshold sits a sensitivity-dependent margin above a slowly rising noise floor, or between your calibrated room and voice levels. Speech starts after 60 ms above the threshold and ends after a configurable pause (450 ms by default) below it, with 3 dB of hysteresis.
- **Tempo.** The frame levels are smoothed and peak-picked with 3 dB hysteresis. Each peak is a syllable nucleus. Syllables per voiced second over a four-second window, divided by your calibrated pace, gives a factor between 0.6 and 1.6 that scales the scroll speed.
- **Motion.** Velocity eases toward its target (speed times tempo while speaking, zero otherwise) with a time constant of about 170 ms, so starts and stops are smooth but prompt. A stalled frame is capped at 250 ms, so the text never jumps.
- **Word follow.** The audio is also fed to the Windows `System.Speech` recogniser through an in-memory stream, with a grammar of one to six words from the script. A recognised phrase is matched against the next 40 words; it needs two in-order hits (or one long word) and never moves backwards, so stray recognitions cannot throw the text off.

## AI

`ModelRouter` turns a task and the script length into a tier (fast, balanced, deep) and an ordered list of available providers. `AiService` tries them in order and moves to the next on failure, but never on cancellation. Results go to a preview window; nothing changes until you accept, and Replace keeps Ctrl+Z undo.

The Claude Code call is: `claude -p --model <haiku|sonnet|opus> --output-format json --tools "" --no-session-persistence --strict-mcp-config --setting-sources "" --system-prompt-file <temp file>`, run in an empty sandbox folder with the prompt on stdin. The CLI aliases always resolve to the newest model in each family. The Claude API path uses `claude-haiku-5-5`, `claude-sonnet-5-5` and `claude-opus-5-5`, with effort `low`, `medium` and `high`, and server-side fallback on the two larger tiers.

## Test hooks

The app accepts `--data-dir` (an isolated profile), `--audio-file` (a WAV file in place of the microphone), `--status-file` (a JSON snapshot every 200 ms), `--start notch|float`, `--open-note <id>` and `--skip-calibration`. Paths must be absolute. These hooks exist for the end-to-end tests and change nothing unless passed.
