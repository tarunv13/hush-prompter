<p align="center"><img src="src/Hush/Assets/hush-256.png" width="96" alt="Hush icon"></p>

<h1 align="center">Hush Prompter</h1>

<p align="center">A private teleprompter for Windows that sits right under your webcam, scrolls when you speak, and is invisible to screen sharing.</p>

<p align="center"><b>One 250 KB file. No installer. No runtime to download. No account. No telemetry.</b></p>

---

Hush puts your script in a black "notch" panel hanging from the top edge of the screen, directly under the camera, so you can read and still look your audience in the eye. It is a clean-room Windows take on the notch-teleprompter idea popularised on the Mac, written from public descriptions of that kind of app. It shares no code or assets with any of them and is not affiliated with any of them.

## Features

| | |
| :--- | :--- |
| **Notch mode** | The script appears in a panel at the top centre of the screen, under the webcam. Drag the edges to resize it. |
| **Floating window** | Move and resize the prompter anywhere, for classic teleprompter rigs or a second screen. |
| **Hidden from screen sharing** | Uses the Windows capture-exclusion flag, so Teams, Zoom, Meet, OBS, screenshots and recordings never see the prompter. The editor can be hidden too. |
| **Voice-activated scrolling** | Scrolls while you talk and stops when you pause. No transcription: it measures speech energy, with a filter that removes mains hum and hiss. |
| **Adaptive tempo** | Counts syllables to estimate your pace, and speeds up or slows down with you. |
| **Word follow** (optional) | Tracks the actual words you say with the offline speech recogniser built into Windows, restricted to the words of your script. |
| **Cues** | Split a script with `## Heading` or a `---` line. Jump between cues with the pager under the prompter or with hotkeys, then carry on. One-cue-at-a-time mode stops at the end of each cue. |
| **Voice beam** | A soft glow at the bottom of the panel follows your loudness, so you can see the microphone is hearing you. |
| **Countdown, hover to pause, end marker** | A countdown before you start; hovering over the prompter pauses it; a marker shows the end of the text. |
| **Stepped speed** | 4 to 120 px/s in 15 steps, changeable while you talk. |
| **Editor with search** | Notes as plain Markdown files, a serif title, a monospace body, search across every note (Ctrl+F). |
| **Import and export** | Drop `.txt`, `.md`, `.rtf`, `.docx` or `.html` files onto the window; export any note to `.md` or `.txt`. |
| **Microphone handling** | Pick the microphone, test it with a live meter, calibrate once, adjust sensitivity and pause length. If the device disappears, Hush falls back to the default input; if it hears nothing for eight seconds, it tells you which input it is using. |
| **AI helpers** | Split into cues, make it speakable, bullet points, shorten by a third, a custom instruction, or a coach that reads your last rehearsal. Every change is shown side by side before it is applied. |

## AI: your Claude Code login first, the right model for each task

Hush can use three providers and tries them in this order (you can force one in Settings):

1. **Claude Code** on this computer, in headless mode, so requests use your existing Claude Code login and plan.
2. **The Claude API**, with a key you paste in Settings. The key is encrypted with Windows DPAPI for your account.
3. **Ollama**, a local model on `127.0.0.1`, which works fully offline.

The model is chosen from the task you ask for:

| Task | Model tier | Why |
| :--- | :--- | :--- |
| Split into cues, bullet points | fast (Haiku) | structural edits |
| Make it speakable, shorten, custom edit | balanced (Sonnet) | rewriting in your voice |
| Rehearsal coach | deepest (Opus) | reads the whole script and your run |

Scripts over 2,500 words move a fast task up to the balanced tier, and scripts over 9,000 words move a balanced task up to the deepest tier. You can pin one tier in Settings.

## Install

1. Download `Hush.exe` from the [latest release](../../releases/latest). Check it against the published SHA-256 if you like.
2. Run it. It needs Windows 10 (version 2004 or later) or Windows 11; both include the .NET Framework 4.8 it runs on.

Windows SmartScreen may warn about an unsigned app the first time. Choose "More info", then "Run anyway", or build it yourself (below).

Your notes and settings live in `%APPDATA%\Hush`. Delete that folder to remove all data.

## Shortcuts

| While the prompter runs (works in any app) | |
| :--- | :--- |
| Ctrl+Alt+P | pause or resume (Ctrl+Alt+Space too, if no other app has taken it) |
| Ctrl+Alt+Left / Right | slower / faster |
| Ctrl+Alt+Up / Down | back / forward one line |
| Ctrl+Alt+Page Up / Page Down | previous / next cue |
| Ctrl+Alt+Q | stop |

| In the editor | |
| :--- | :--- |
| Ctrl+Enter | start in the notch |
| Ctrl+Shift+Enter | start floating |
| Ctrl+F | search notes |
| Ctrl+N | new note |
| Ctrl+, | settings |

## Security and privacy

Hush is built to have almost nothing to attack.

- **No network listener and no remote control.** Nothing on your network can connect to it.
- **No third-party code.** It uses only the .NET Framework that ships with Windows, so there is no package supply chain.
- **Your data stays local.** Notes are Markdown files in your profile. Nothing leaves the machine unless you run an AI action, and then only that note goes to the provider you chose.
- **Locked-down Claude Code calls.** All tools are disabled, MCP servers and settings files (with their hooks) are not loaded, nothing is saved as a session, and each call runs in an empty sandbox folder. The script is sent over stdin and never placed on a command line. The executable is found only on absolute `PATH` entries.
- **The script is treated as data.** It is wrapped in delimiters the script cannot close, and the instructions tell the model never to obey text inside it. A live test checks that an injected "ignore previous instructions" is edited, not followed.
- **Hostile files are handled safely.** Size caps on every import, XML with DTDs and external entities disabled (XXE), a byte budget against zip bombs, HTML read as text only, note file names that are random ids and never derived from titles.
- **Secrets are encrypted at rest.** The API key is encrypted with DPAPI, is sent only to `api.anthropic.com`, and is never followed through redirects. Settings are clamped on load, so a tampered file cannot push the app into a bad state.
- **Least privilege.** It runs as the normal user, never asks for administrator rights, and logs errors without note text.

See [SECURITY.md](SECURITY.md) to report a vulnerability, and [docs/DESIGN.md](docs/DESIGN.md) for how it works.

## Build and test

Needs the .NET SDK (any version from 6) on Windows.

```powershell
dotnet build src/Hush -c Release            # -> src/Hush/bin/Release/Hush.exe
dotnet build tests/Hush.Tests -c Release
tests/Hush.Tests/bin/Release/Hush.Tests.exe            # 77 offline tests
tests/Hush.Tests/bin/Release/Hush.Tests.exe --live     # also calls Claude Code and Ollama
powershell -ExecutionPolicy Bypass -File tests/e2e/run-e2e.ps1   # drives the real app
```

The end-to-end script synthesises speech with the built-in Windows voice and plays it into the app as its microphone. It checks that the text moves with speech and stops in silence, that the countdown holds, that the window is excluded from capture (by grabbing the screen), and that the global hotkeys work. It briefly grabs a small region at the top of your screen to check exclusion. Pass `-Shots <folder>` only if you want those images saved.

## Not included

- **Screen recording with a camera bubble.** Doing it well needs a video encoder, which would add size and dependencies. Use the Windows Snipping Tool, Clipchamp or OBS; the prompter stays invisible in all of them.
- **Phone remote.** It would need a network listener, which Hush deliberately does not have. The global hotkeys and any Bluetooth presentation clicker that sends Page Up and Page Down cover the same job.

## Licence

[MIT](LICENSE).
