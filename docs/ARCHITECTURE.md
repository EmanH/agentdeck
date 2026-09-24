# AgentDeck architecture

AgentDeck is a single .NET 10 WPF process. The window hosts a WebView2 page (the UI); C# owns everything that
touches the OS: pseudo consoles, the Stream Deck, the microphone, the global hotkey and the tray.

```
┌──────────────────────────── AgentDeck.exe ─────────────────────────────┐
│  MainWindow (WPF)  ── JSON messages ──  wwwroot/app.js (WebView2)      │
│     │                                   sidebar · tabs · split panes    │
│     ├─ SessionManager ── PseudoConsole (ConPTY) ×N ── claude/codex/...  │
│     ├─ DeckController ── StreamDeckSharp (USB HID) + KeyArt (Skia)      │
│     ├─ DictationService ── NAudio ─▶ Soniox WS ─▶ gpt-6-luna ─▶ deliver │
│     ├─ AgentCatalog ── probes the agent CLIs for models/thinking levels │
│     ├─ ModifierHotkey (WH_KEYBOARD_LL) · TrayIcon · Chime               │
│     └─ Stores: projects.json · workflows.json · transcripts.json        │
└────────────────────────────────────────────────────────────────────────┘
```

## Terminals

- `Native/PseudoConsole.cs` creates a ConPTY (`CreatePseudoConsole`) and launches the child with
  `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`, exactly like Windows Terminal. Output is read on a background thread,
  UTF-8 decoded (split multi-byte sequences are handled by a stateful `Decoder`) and batched to the UI every
  8 ms, one message per session, which is far faster than posting every chunk.
- Agent sessions run as `powershell -NoExit -Command <agent>`, so quitting the agent leaves you in a shell in
  the project folder.
- **Environment:** at startup the process environment is replaced with a clean login environment
  (`CreateEnvironmentBlock`), the same thing Explorer and Windows Terminal give a new process. Whatever launched
  AgentDeck can't leak variables into agents, and newly added API keys or PATH entries are picked up.

## UI ↔ host protocol

`app.js` and `MainWindow.cs` exchange small JSON messages over `chrome.webview`.

| UI → host | Host → UI |
|---|---|
| `ready`, `input`, `resize`, `title`, `focus`, `close` | `state` (projects, sessions, labels, stars) |
| `newSession`, `selectProject`, `saveProject`, `removeProject` | `created`, `output`, `exited`, `activate` |
| `saveWorkflow`, `runWorkflow`, `deleteWorkflow`, `refreshAgentOptions` | `workflows`, `agentOptions`, `transcripts` |
| `openUrl`, `openFolder`, `addProject` | `pasteInto`, `projectDialog`, `workflowEditor` |

C# owns processes and persistent data; the page owns layout (per-project tabs, each a binary tree of split
panes). `docs/tools/mock-bridge.js` implements the host side in ~150 lines of JS, which is handy for UI work
without the app, and is how the README screenshots are made.

## Stream Deck

- `Deck/DeckController.cs` runs a 30 fps loop. Each frame it builds a `KeyView` per key (a signature string, a
  draw function, and press/hold actions) and only re-renders keys whose signature changed, so an idle deck
  costs nothing and animations (mic, busy dots, countdown rings) cost only their own keys.
- Presses fire on key-down (taps are never lost); a key can also have a **hold** action that fires after 800 ms,
  with a progress ring drawn from the hold state.
- Modes: normal (projects / sessions / launchers), project picker (more than three projects) and the workflow
  list. Both overlays close after 10 idle seconds.
- `Deck/KeyArt.cs` draws every key with SkiaSharp at 72×72: fitted, wrapped bold labels, SVG agent logos, and
  Fluent Emoji icons rendered via Svg.Skia.

## Dictation

1. `MicCapture` records 16 kHz mono PCM (NAudio) and tracks a smoothed level for the mic animation.
2. `SonioxSession` streams audio to Soniox's real-time API (`stt-rt-v5`) *while you speak*; audio captured before
   the socket opens is queued, so the first words are never lost. Stopping sends end-of-audio and waits for
   the final tokens (typically ~0.3 s).
3. `gpt-6-luna` (Responses API, reasoning off, priority tier, connection pre-warmed at mic start) cleans the
   text. On any failure or timeout the raw transcript is used.
4. Delivery: if dictation started in an AgentDeck terminal, the text goes straight into that terminal through
   xterm's `paste()` (bracketed paste when the app wants it) and is submitted if you've moved away. Otherwise
   it's pasted into the focused app with Ctrl+V, after waiting for the shortcut's modifiers to be released.
5. Synthetic keystrokes are tagged (`dwExtraInfo`) so the low-level hotkey hook ignores our own input but still
   sees injected input from tools like Logitech Options.

## Finished detection

A session enters "awaiting work" when you submit (Enter, or a bare digit for agent menus). If it then produces
output for at least 4 s followed by 4 s of silence, it's finished or waiting for you: agents animate a
spinner/timer the whole time they (or their sub-agents) work, so silence is a reliable signal.

## Agent discovery

`Core/AgentCatalog.cs` asks each CLI what it supports, locally and without model calls:

| Agent | Models | Thinking levels |
|---|---|---|
| Claude Code | aliases named in `claude --help` | the `--effort` list in `claude --help` |
| Codex | `~/.codex/models_cache.json` (visible models) | per model, from the same cache |
| Grok | `grok models` | the error for an invalid `--reasoning-effort` in `-p` mode |

Workflows pass the choice as each CLI's own flags (`--model/--effort`, `-m/-c model_reasoning_effort=`,
`-m/--reasoning-effort`). Prompts travel via a temp file and are escaped for Windows argv parsing, because
Windows PowerShell 5.1 doesn't escape embedded quotes when calling native programs.

## Icons

`AgentDeck/tools/build-icons.mjs` builds `wwwroot/icons/fluent.json` (1,591 Fluent Emoji, flat style) with
categories, a hand-picked *Featured* set and emojilib search keywords. The same file feeds the UI picker and the
Stream Deck renderer.

## Data

| What | Where |
|---|---|
| Projects, workflows, last 10 transcripts | `%APPDATA%\AgentDeck\*.json` |
| Log (timings only, no transcript text) | `%LOCALAPPDATA%\AgentDeck\agentdeck.log` |
| Installed app | `%LOCALAPPDATA%\Programs\AgentDeck` (self-contained, so .NET updates can't break it) |
