# Rambler for Windows

A small Windows tray utility for AI dictation. Speak naturally (think aloud, change your mind mid-sentence, ramble) and Rambler types clear text into whatever app you were using.

- **Prompt Cleanup** (default): microphone → Gemini SMART transcription → Gemini Flash cleanup → text inserted.
- **Smart Only**: microphone → Gemini SMART transcription → text inserted. No second model.

| Shortcut | Action |
|---|---|
| `Ctrl + Win + Space` | Start/stop dictation in the selected mode |
| `Ctrl + Win + Shift + Space` | Start/stop dictation as **Smart Only**, without changing your selected mode |

Native .NET 10 + WPF. No Electron, no Python, no browser engine, no background services. Dependencies: NAudio (WASAPI capture) and the .NET runtime.

---

## Install

1. Download or build `Rambler.exe` (self-contained, x64, no .NET install needed). See [Build](#build-from-source).
2. Run it. It's unsigned, so Windows SmartScreen may ask you to confirm (**More info → Run anyway**).
3. Rambler appears in the system tray (you may need to drag it out of the `^` overflow area).
4. On first run, Settings opens on the **Gemini** tab. Paste your API key from [Google AI Studio](https://aistudio.google.com/apikey), click **Test connection**, then **Save**.

Requirements: Windows 10 (1809+) or Windows 11, x64, a microphone, and a Gemini API key with access to `gemini-3.5-transcribe-live` (public preview).

## Use

- **Dictate:** put the cursor where you want text, press `Ctrl+Win+Space`, talk, press it again. Rambler transcribes, cleans up (in Prompt Cleanup mode), and inserts the text into the window you started in.
- **Tray icon:** blue = ready, **red = microphone on**, amber = working, grey `!` = needs attention.
- **Left-click the tray icon** for the popup: mode switch, start/stop button, live preview, input level, and hotkey reminders. Click anywhere else to dismiss it. Dictation keeps running.
- **Right-click the tray icon** for Settings, Restart and Exit.
- **Cancel:** the ✕ button in the popup discards the current dictation. Nothing is inserted.
- **Pauses are fine.** Rambler never finalizes a thought because you paused. The utterance ends only when you stop, or optionally after a long silence (Settings › Audio).
- **If something fails, your words aren't lost:**
  - If cleanup fails, the popup offers **Insert** (the SMART transcript) or **Copy**.
  - If insertion isn't safe (the window closed, focus moved, or the app runs as administrator), the text goes to the clipboard and you're told to press `Ctrl+V`.
  - **Copy last** re-copies the most recent result. It's held in memory only.

## Settings

| Tab | What you can change |
|---|---|
| General | Start with Windows, default mode, both shortcuts (with conflict warnings), insertion method (auto / type / paste), clipboard restore, theme (system / light / dark), start/stop sounds |
| Audio | Microphone, live input level, 4-second record-and-playback test, auto-stop after silence (off by default), maximum dictation length |
| Gemini | API key (Credential Manager), connection test, live and recorded-audio models, recorded-audio fallback, language (empty = auto-detect), custom vocabulary, last error |
| Cleanup | Cleanup model, thinking level, **technical refinement** (default 35%), editable system prompt with **Restore default** |
| Privacy | What is sent to Google and what stays local |

Settings live in `%APPDATA%\Rambler\settings.json`. They contain no secrets.

**Technical refinement** adds one short paragraph to the cleanup model's system instruction:

- **Low:** keep your own wording; fix only clearly garbled terms.
- **Medium (default 35%):** use the established term only when your meaning is clear.
- **High:** prefer precise terminology whenever the transcript justifies it.

Every level says never invent technical details. This is a prompt adjustment, not an API parameter.

## How it works

```
Hotkey ─► DictationCoordinator ─► WasapiAudioSource (any mic format → 16 kHz mono PCM16)
              │                        │
              │                        ▼
              │               LiveTranscriptionSession ──WSS──► gemini-3.5-transcribe-live (SMART)
              │                        │  (fallback) ──HTTPS──► Files API + Interactions API, gemini-3.5-transcribe
              ▼                        ▼
   Idle → Listening → Finalizing → Cleaning (Prompt Cleanup only) → Inserting → Idle
                                       │
                         GeminiCleanupService ──HTTPS──► gemini-3.6-flash (generateContent)
                                       ▼
                         TextInsertionService (SendInput Unicode / clipboard paste)
```

### Gemini APIs used (verified against Google's documentation and SDK, October 2026)

I verified these against Google's official `gemini-live-api-dev` skill, the official *Get started with Gemini Transcribe* cookbook, and the `google-genai` 2.29.0 SDK source (the wire field names come from its converters). `ai.google.dev` itself was blocked in the build environment.

**Live transcription:** `wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent`

- First message:
  ```json
  {"setup":{"model":"models/gemini-3.5-transcribe-live",
            "generationConfig":{"responseModalities":["TEXT"]},
            "realtimeInputConfig":{"automaticActivityDetection":{"disabled":true}},
            "inputAudioTranscription":{"mode":"SMART","customVocabulary":[...]}}}
  ```
- **Manual endpointing** (automatic activity detection disabled, explicit `activityStart` / `activityEnd`) is Google's recommendation for SMART mode on live streams: automatic VAD can split a thought at natural pauses. It also suits a speaker who thinks aloud.
- Audio is sent as `realtimeInput.audio` (`audio/pcm;rate=16000`, base64) in 100 ms chunks. Audio captured while the socket is still connecting is buffered, so the first words aren't lost.
- `serverContent.interimInputTranscription` is shown as a provisional preview and replaced by each newer interim. `serverContent.inputTranscription` is committed text. Cumulative and delta final updates are both de-duplicated, and committed text is never rewritten.
- Connections last about 10 minutes. Rambler rolls over to a fresh connection at 9 minutes, or on `goAway`, without losing audio. Dropped connections reconnect with backoff.

**Recorded-audio fallback** (same path as the working proof of concept):

- Files API resumable upload, then `POST /v1beta/interactions` with `generation_config.transcription_config.mode = "smart"` and `"store": false`, then the upload is deleted.
- It's used only for the parts of a dictation where live transcription failed, and only when the fallback setting is on.

**Cleanup:** `POST /v1beta/models/gemini-3.6-flash:generateContent`

- The editable system prompt goes in `systemInstruction`. The transcript is a separate `user` message, wrapped in `<transcript>` tags so instructions inside the speech are edited, not obeyed.
- `thinkingConfig.thinkingLevel = MINIMAL` keeps latency low. Models that reject that level are retried automatically with the model default.
- Safety filters are set to `OFF` so profanity and explicit wording survive cleanup as the prompt requires.
- Why `gemini-3.6-flash`: it's the newest Flash model whose documented thinking levels include `MINIMAL` (the 3.7 and 3.8 Flash models start at `LOW`). I couldn't check pricing on Google's pricing page from the build environment. Switch to `gemini-3.5-flash-lite` (or any model) in Settings › Cleanup if cost matters more.

All requests authenticate with the `x-goog-api-key` header, never a URL parameter.

## Security and privacy

- **API key:** stored in Windows Credential Manager (generic credential `Rambler/GeminiApiKey`, this user on this machine only). Never written to JSON, never logged, and scrubbed from any error text. The `GEMINI_API_KEY` environment variable is honoured read-only if no key is saved.
- **Microphone:** opened only while listening (red tray icon), and released the moment you stop, cancel, exit, or the device disappears. Rambler never records at startup and has no background recording.
- **Audio and transcripts:** never written to disk. With fallback on, the current dictation's audio stays in memory until it finishes, then the buffer is zeroed. The last result stays in memory for **Copy last** until exit.
- **Network:** HTTPS/WSS to `generativelanguage.googleapis.com` only.
- **Text insertion:** documented, unprivileged APIs only (`SendInput` with Unicode characters, and the clipboard). No keyboard hooks, no DLL injection, no elevation. Rambler only types after confirming the original window is back in the foreground. Otherwise it copies to the clipboard and tells you.
- **Clipboard:** text placed temporarily for pasting is marked so Windows clipboard history skips it. Your previous clipboard content is restored when it can be copied faithfully: text, HTML, RTF, files and bitmaps. If it can't (for example a metafile), it's left as is and you're told.
- **Log:** `%LOCALAPPDATA%\Rambler\logs\rambler.log` holds warnings and errors only. It never contains transcripts or keys.

## Build from source

Requires the .NET 10 SDK. Building works on Windows, and cross-builds from Linux or macOS.

```powershell
./build.ps1              # build, unit tests, publish
./build.ps1 -SkipTests
./build.ps1 -LiveTests   # also run live Gemini tests (see below)
```

```bash
./build.sh               # same, from Linux/macOS
```

Or by hand:

```powershell
dotnet build Rambler.sln -c Release
dotnet test tests/Rambler.Core.Tests -c Release
dotnet publish src/Rambler/Rambler.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
```

The output is a single self-contained `artifacts/publish/win-x64/Rambler.exe` (about 66 MB, including the .NET and WPF runtimes).

For a small (~1 MB) build that relies on the installed [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) instead:

```powershell
dotnet publish src/Rambler/Rambler.csproj -c Release -r win-x64 -p:SelfContained=false -p:EnableCompressionInSingleFile=false -o artifacts/publish/win-x64-framework-dependent
``` Open `Rambler.sln` in Visual Studio 2026 (or any IDE with .NET 10 support, such as Rider) to work on it.

### Tests

- `tests/Rambler.Core.Tests`: 142 deterministic unit tests. They need no API key or Windows. Coverage:
  - the state machine and hotkey mode selection, including the Smart Only bypass
  - transcript accumulation and interim/final de-duplication
  - Live protocol messages
  - a full session against a scripted fake Live server: buffering, batching, reconnects, rollover, `goAway`, fallback, timeouts and cancellation
  - cleanup prompt construction and transcript preservation
  - REST error mapping and retries
  - settings persistence and secret handling
  - audio conversion
  - coordinator flows (cleanup failure offers, insertion fallback, microphone release, rapid toggling)
- `tests/Rambler.IntegrationTests`: optional live tests, skipped unless configured:
  ```powershell
  $env:GEMINI_API_KEY = "..."
  $env:RAMBLER_TEST_WAV = "C:\path\speech-16k-mono.wav"   # optional; enables the audio tests
  dotnet test tests/Rambler.IntegrationTests -c Release --logger "console;verbosity=detailed"
  ```

## Project layout

```
src/Rambler.Core/            Platform-neutral logic (net10.0, unit-tested)
  Audio/                     PcmConverter (any format → 16 kHz mono PCM16), WavWriter, IAudioSource
  Transcription/             LiveProtocol, GeminiLiveConnection, LiveTranscriptionSession,
                             TranscriptAccumulator, GeminiRecordedTranscriber
  Cleanup/                   DefaultPrompts, CleanupPromptBuilder, GeminiCleanupService
  Dictation/                 DictationStateMachine, DictationCoordinator, ITextInserter
  Gemini/                    Endpoints, HTTP + error mapping, connection tester, Redactor
  Settings/                  AppSettings, SettingsService, HotkeyGesture, ICredentialStore
src/Rambler/                 Windows app (net10.0-windows, WPF)
  App.xaml.cs                AppHost: composition, tray lifecycle, single instance
  Services/                  TrayService, HotkeyService (RegisterHotKey), WasapiAudioSource,
                             TextInsertionService, ClipboardHelper, WindowsCredentialStore,
                             StartupRegistration, ThemeService, FeedbackSounds
  Views/, ViewModels/        Popup and Settings (light MVVM)
  Themes/                    Light/Dark palettes and the popup's control styles
tests/                       Unit tests and optional live integration tests
```

**Future incremental insertion.** Each live segment already commits final text independently (`TranscriptAccumulator.FinalText`), and insertion sits behind `ITextInserter`. Inserting committed segments as they arrive would mean adding a segment-committed event to `ITranscriptionSession` and calling the inserter from the coordinator. Nothing else needs rewriting.

## Troubleshooting

- **"Shortcut conflict" at startup:** Windows or another app already owns the shortcut. Windows uses `Ctrl+Win+Space` to switch back to the previous keyboard input method, so if you use several input languages, pick another shortcut in Settings › General (e.g. `Ctrl+Alt+Space`).
- **Text went to the clipboard instead of being typed:** the target app runs as administrator (Windows blocks typing into elevated apps from normal apps), or focus moved. Press `Ctrl+V`.
- **Microphone access is blocked:** Windows Settings › Privacy & security › Microphone › enable *Let desktop apps access your microphone*.
- **Live preview unavailable:** the live model couldn't be reached. With fallback on, Rambler keeps recording and transcribes the audio when you stop. Settings › Gemini › **Test connection** shows which step fails.
- **Language:** leave it empty unless detection gets it wrong. A third-party report says setting language codes on the live model may disable SMART formatting. That's unconfirmed, but auto-detect avoids the question.

## Verification status

What has been verified so far:

- The solution builds in Release with zero warnings, and the self-contained `win-x64` single-file publish works. Both were run on Linux with the .NET 10.0.112 SDK, which bundles runtime 10.0.12.
- All 142 unit tests pass. They ran repeatedly with no flakiness, and a deliberate code mutation was caught.
- The real Gemini endpoints were probed with a deliberately invalid key. Both `generateContent` and the Live `BidiGenerateContent` WebSocket accepted the request shape and header authentication, and returned `API key not valid`, which Rambler maps to its invalid-key error.

**Not yet verified:** no API key was available, so live transcription and cleanup have not run against real audio or text. The WPF app was compiled but not launched, because the build environment is Linux. To finish verification on a Windows PC:

1. `./build.ps1`, then run `artifacts\publish\win-x64\Rambler.exe`. Check the tray icon appears, left-click opens the popup, and right-click shows Settings / Restart / Exit.
2. In Settings › Gemini, enter your key and click **Test connection**. All four checks should pass.
3. In Settings › Audio, click **Test microphone**. The level meter should move and you should hear the playback.
4. Open Notepad, press `Ctrl+Win+Space`, speak with pauses and self-corrections, then press it again. You should see the live preview, then cleaned text inserted into Notepad.
5. Repeat with `Ctrl+Win+Shift+Space`. The SMART transcript should be inserted with no cleanup call, and the popup should still show **Prompt Cleanup** selected.
6. Start dictation from the popup's button, then stop it from the popup. Text should go to the previous app, not the popup.
7. Unplug the microphone while dictating. The recorded part should be finished and inserted, and the tray icon should no longer be red.
8. Optionally run `./build.ps1 -LiveTests` with `GEMINI_API_KEY` and `RAMBLER_TEST_WAV` set.
