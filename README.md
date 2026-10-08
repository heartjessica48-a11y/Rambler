# Rambler for Windows

A small Windows tray utility for AI dictation. Speak naturally (think aloud, change your mind mid-sentence, ramble) and Rambler types clear text into whatever app you were using.

- **Prompt Cleanup** (default): microphone → Gemini SMART transcription → Gemini Flash cleanup → text inserted.
- **Smart Only**: microphone → Gemini SMART transcription → text inserted. No second model.

Start dictation with the popup's microphone button, or with two optional global shortcuts:

| Shortcut | Action |
|---|---|
| Main shortcut | Start/stop dictation in the selected mode |
| Bypass shortcut | Start/stop dictation as **Smart Only**, without changing your selected mode |

No shortcuts are set out of the box, because the obvious combinations (such as `Ctrl+Win+Space`) are already used by Windows on many PCs. Record your own in Settings › General (for example `Ctrl+Alt+Space` and `Ctrl+Alt+Shift+Space`).

Native .NET 10 + WPF. No Electron, no Python, no browser engine, no background services. Dependencies: NAudio (WASAPI capture) and the .NET runtime.

---

## Install

1. Download `Rambler-<version>-win-x64.exe` from the [Releases](https://github.com/heartjessica48-a11y/Rambler/releases) page (self-contained, x64, no .NET install needed), or [build it yourself](#build-from-source).
2. Run it. It's unsigned, so Windows SmartScreen may ask you to confirm (**More info → Run anyway**).
3. Rambler appears in the system tray (you may need to drag it out of the `^` overflow area).
4. On first run, Settings opens on the **Gemini** tab. Paste your API key from [Google AI Studio](https://aistudio.google.com/apikey), click **Test connection**, then **Save**.

Requirements: Windows 10 (1809+) or Windows 11, x64, a microphone, and a Gemini API key with access to `gemini-3.5-transcribe-live` (public preview).

## Use

- **Dictate:** put the cursor where you want text, press your shortcut (or click the microphone in the popup) and talk. Text appears in that app **while you speak**: each finished phrase (Smart Only) or each finished thought (Prompt Cleanup) is inserted as soon as it's stable. Press the shortcut (or the button) again to stop; the last words are flushed then. Prefer one insert at the end? Settings › General › **When I finish**.
- **Tray icon:** blue = ready, **red = microphone on**, amber = working, grey `!` = needs attention.
- **Left-click the tray icon** for the popup, which opens right at the icon: mode switch, start/stop button, live preview, input level, and hotkey reminders. Click anywhere else to dismiss it. Dictation keeps running.
- **Right-click the tray icon** for Settings, Restart and Exit.
- **Cancel:** the ✕ button in the popup discards the current dictation. Nothing is inserted.
- **Pauses are fine.** A thinking pause never ends dictation, and a pause alone never ends a thought that trails off ("…and", "so,"). Recording stops only when you stop it, or optionally after a long silence (Settings › Audio).
- **Switching apps is safe.** If you click into another app while dictating, Rambler keeps listening but **pauses insertion** (status: *Waiting for target*). Click back into the same text field and it continues. Nothing is ever typed into a different window or field.
- **Say "new line" or "new paragraph"** to break lines. In Prompt Cleanup, "next point" and "make that a list" also work, and the model formats lists and paragraphs on its own when the content clearly calls for it.
- **If something fails, your words aren't lost:**
  - If cleanup fails, insertion stops and the popup offers **Insert** (the SMART transcript from that point) or **Copy**.
  - If text couldn't be inserted (you were in another app when you stopped, the window closed, or the app runs as administrator), the popup offers **Insert** (into the field you were last typing in) or **Copy**.
  - **Copy last** re-copies the most recent result. It's held in memory only.

## Settings

| Tab | What you can change |
|---|---|
| General | Start with Windows, default mode, both shortcuts (**Record** button, optional: clear one to turn it off, with per-shortcut conflict status), **when to insert** (as I speak / when I finish), insertion method (auto / type / paste), clipboard restore, theme (system / light / dark), start/stop sounds |
| Audio | Microphone, live input level, 4-second record-and-playback test, auto-stop after silence (off by default), maximum dictation length |
| Gemini | API key (Credential Manager), connection test, live and recorded-audio models, recorded-audio fallback, language (empty = auto-detect), custom vocabulary, last error |
| Cleanup | Cleanup model, thinking level, **technical refinement** (default 35%), editable system prompt with **Restore default**, and a read-only view of the rules Rambler always appends |
| Privacy | What is sent to Google and what stays local |

Settings live in `%APPDATA%\Rambler\settings.json`. They contain no secrets.

**Technical refinement** adds one short paragraph to the cleanup model's system instruction:

- **Low:** keep your own wording; fix only clearly garbled terms.
- **Medium (default 35%):** use the established term only when your meaning is clear.
- **High:** prefer precise terminology whenever the transcript justifies it.

Every level says never invent technical details. This is a prompt adjustment, not an API parameter.

## How it works

```
Microphone ─► LiveTranscriptionSession ──WSS──► gemini-3.5-transcribe-live (SMART)
                 │  utterance ends at a natural pause → committed segment (in order, exactly once)
                 ▼
            ProgressiveOutput (one ordered worker per dictation)
                 ├─ Smart Only:      SpokenFormatting ("new line"/"new paragraph") ─┐
                 └─ Prompt Cleanup:  ThoughtBuffer → GeminiCleanupService (chunk +  │
                                     read-only committed context) ──────────────────┤
                                                                                     ▼
            TextInsertionService ◄── TargetWindowService (window + focused control + UI Automation)
```

### Progressive dictation

**Finalized text.** The Live API documents interim results (`interimInputTranscription`) and finals (`inputTranscription`), but not when finals arrive mid-turn. Rambler doesn't rely on that. With manual endpointing it decides itself when an utterance is over: a pause of about 1.2 s after at least 2.5 s of speech ends the utterance. Its audio gets `activityEnd` on its own connection, the server finalizes it, and listening continues seamlessly on a fresh connection (audio during the handover is buffered, not lost).

- Short thinking pauses don't split.
- After 25 s a 0.45 s pause is enough, and 60 s is a hard cap.
- Each finalized utterance becomes a *committed segment* with a stable, increasing index. Segments are emitted strictly in order and exactly once; a slow one, or one repaired by the recorded-audio fallback, holds later ones back.
- Interim text is shown in the popup only and never inserted.

**Chunk boundaries for Prompt Cleanup.** `ThoughtBuffer` groups committed segments using deterministic rules, with no extra model:

- Release at ≥ 5 s of speech when the text ends a sentence and doesn't trail off (a trailing comma, dash, ellipsis or a connective such as "and", "but", "so", "because").
- Release at ≥ 15 s unless it trails off; always release at 30 s or 900 characters.
- If the speaker has been quiet for 2.5 s after a complete sentence, release it even if short; after 8 s quiet, release anything.
- At stop, flush the rest as the final chunk.

**Cleanup context.** Each chunk is sent as `<transcript final="…">` together with up to about 1,200 characters of already-produced text as `<committed_context>`. The model edits only the chunk; the committed text is read-only. Chunks are processed one at a time, in order, so a slow response can never be inserted after a later chunk.

**Focus safety.** At start Rambler captures:

- the foreground window (or, if the popup is in front, the window you were in before);
- the focused control's handle (`GetGUIThreadInfo`);
- its UI Automation identity (runtime id) and editability (control type / `ValuePattern`).

Before every insert (and between typing batches) it checks that the same window is in front and the same control is focused. Browsers and Electron apps use one window for every field, which is why the UI Automation identity matters. If anything differs, insertion **waits**; it never forces another window to the front while you're dictating. The only exception is one return of focus when you stop from Rambler's own popup, done while the popup still owns the foreground, which fixes the old "couldn't switch back, copied instead" problem. Non-editable targets (the desktop, File Explorer's file list, buttons) receive nothing; the text is kept for you.

**Line breaks and Enter.** Rambler never presses a bare Enter for a line break, except in classic editors such as Notepad and WordPad where Enter is a line break. Elsewhere (browsers, Discord, Slack, Teams and other Electron or web apps), text containing line breaks is pasted. Pasting inserts the breaks without submitting the message. The clipboard is restored afterwards and the temporary text is kept out of clipboard history. Single-line text is typed directly with Unicode input and doesn't touch the clipboard. **Always type** in Settings uses Shift+Enter for line breaks instead.

**Formatting.**

- **Smart Only:** a tiny deterministic layer converts standalone "new line" / "new paragraph" into line breaks. A phrase counts only after punctuation or a line break and followed by the end or a new sentence, so "add a new line of code" stays literal, and a break SMART already produced isn't doubled.
- **Prompt Cleanup:** formatting is left to the model through an app-managed *Formatting Rules* section. That section, plus *Progressive Dictation Rules* in progressive mode, is appended after your system prompt on every request; your editable prompt is never modified.

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

### Releases

`.github/workflows/release.yml` builds on a Windows runner, runs the unit tests, and publishes a GitHub Release with both executables and `SHA256SUMS.txt`. To cut one:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Or run the **Release** workflow manually from the Actions tab and enter a tag. Tag pushes are marked as pre-releases; promote one to a full release on GitHub when you're happy with it.

### Tests

- `tests/Rambler.Core.Tests`: 207 deterministic unit tests. They need no API key or Windows. Coverage:
  - the state machine and hotkey mode selection, including the Smart Only bypass
  - transcript accumulation and interim/final de-duplication
  - Live protocol messages
  - a full session against a scripted fake Live server: buffering, batching, reconnects, rollover, `goAway`, fallback, timeouts and cancellation
  - cleanup prompt construction and transcript preservation
  - REST error mapping and retries
  - settings persistence and secret handling
  - audio conversion
  - coordinator flows (cleanup failure offers, insertion fallback, microphone release, rapid toggling)
  - progressive dictation:
    - pause endpointing, in-order exactly-once commits, and the fallback-repaired utterance
    - thought chunking and ordered chunk cleanup with read-only context (including slow responses)
    - Smart Only bypass and spoken "new line" / "new paragraph"
    - focus loss (pause and resume), destroyed targets, partial typing without duplicates, and cleanup failure recovery
    - empty chunks, stale events from a cancelled session, stop/restart races
    - preservation of a customized system prompt
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
  Dictation/                 DictationStateMachine, DictationCoordinator, ProgressiveOutput,
                             ThoughtBuffer, SpokenFormatting, ITextInserter
  Gemini/                    Endpoints, HTTP + error mapping, connection tester, Redactor
  Settings/                  AppSettings, SettingsService, HotkeyGesture, ICredentialStore
src/Rambler/                 Windows app (net10.0-windows, WPF)
  App.xaml.cs                AppHost: composition, tray lifecycle, single instance
  Services/                  TrayService, HotkeyService (RegisterHotKey), WasapiAudioSource,
                             TargetWindowService, TextInsertionService, ClipboardHelper, WindowsCredentialStore,
                             StartupRegistration, ThemeService, FeedbackSounds
  Views/, ViewModels/        Popup and Settings (light MVVM)
  Themes/                    Light/Dark palettes and the popup's control styles
tests/                       Unit tests and optional live integration tests
```


## Known limitations

- **Utterance splitting uses audio level.** Very noisy rooms may never look silent; text then appears at the 60-second cap or when you stop. Each utterance is SMART-cleaned on its own, so a self-correction spoken across a long pause isn't merged in Smart Only. Prompt Cleanup's context handles it.
- **Text is added at the caret.** Rambler can't see the caret position, so if you move the cursor or type yourself while dictating, new text goes wherever the caret is. Rambler never deletes or rewrites what's already there.
- **Field identity uses UI Automation where the app supports it.** Apps without accessibility support fall back to window/control-handle checks, which can't tell two fields in the same browser window apart.
- **Line breaks outside classic editors use the clipboard.** It's restored afterwards when that's safe; complex formats such as metafiles can't be restored.
- **Elevated (administrator) apps can't receive typed text.** Windows blocks it from a normal app; use **Copy**.
- **Chunk timing has no settings.** The defaults above are deliberately conservative.

## Troubleshooting

- **"Shortcut conflict":** Windows or another app already owns the shortcut. For example, Windows uses `Ctrl+Win+Space` to switch back to the previous keyboard input method. Go to Settings › General, click **Record** and press another combination (e.g. `Ctrl+Alt+Space`), or **Clear** it and use the popup's microphone button. Each shortcut shows whether it's active. Note that shortcuts Windows grabs first, such as `Win+Space`, can't be recorded at all.
- **Status says "Waiting for target":** you're in a different app or a different field than when you started. Click back into the original text field and insertion continues. Or stop, then use **Insert** (it goes where you were last typing) or **Copy**.
- **"No editable target":** dictation started while the desktop, a file list or a button had focus. The text is kept; stop, click into a text field, open the popup and press **Insert**.
- **Text went to the clipboard instead of being typed:** the target app runs as administrator (Windows blocks typing into elevated apps from normal apps). Press `Ctrl+V`.
- **Microphone access is blocked:** Windows Settings › Privacy & security › Microphone › enable *Let desktop apps access your microphone*.
- **Live preview unavailable:** the live model couldn't be reached. With fallback on, Rambler keeps recording and transcribes the audio when you stop. Settings › Gemini › **Test connection** shows which step fails.
- **Language:** leave it empty unless detection gets it wrong. A third-party report says setting language codes on the live model may disable SMART formatting. That's unconfirmed, but auto-detect avoids the question.

## Verification status

What has been verified so far:

- The solution builds in Release with zero warnings, and the self-contained `win-x64` single-file publish works. Both were run on Linux with the .NET 10.0.112 SDK, which bundles runtime 10.0.12.
- All 207 unit tests pass, on Linux and on the Windows release runner. They ran repeatedly with no flakiness, and a deliberate code mutation was caught.
- The tray, popup and Settings windows of 0.1.x were confirmed running on Windows by the user.
- The real Gemini endpoints were probed with a deliberately invalid key. Both `generateContent` and the Live `BidiGenerateContent` WebSocket accepted the request shape and header authentication, and returned `API key not valid`, which Rambler maps to its invalid-key error.

**Not yet verified:**

- No API key was available, so live transcription and cleanup have not run against real audio or text.
- Progressive insertion (UI Automation focus tracking, paste-based line breaks) was compiled but has not been exercised against real apps. Behavior in Notepad, browser textareas, Discord and rich-text editors still needs hands-on testing.

To finish verification on a Windows PC:

1. `./build.ps1`, then run `artifacts\publish\win-x64\Rambler.exe`. Check the tray icon appears, left-click opens the popup, and right-click shows Settings / Restart / Exit.
2. In Settings › Gemini, enter your key and click **Test connection**. All four checks should pass.
3. In Settings › Audio, click **Test microphone**. The level meter should move and you should hear the playback.
4. Record a shortcut in Settings › General. Open Notepad, press it, speak a few sentences with pauses and self-corrections. Cleaned text should appear while you're still talking. Say "new paragraph" and check for a blank line. Press the shortcut again to stop.
5. Do the same in a browser textarea and in Discord. Line breaks must appear without the message being sent.
6. While dictating, click into another app. The status should show *Waiting for target* and nothing should be typed there. Click back and insertion should resume.
7. Repeat with the bypass shortcut. SMART text should appear phrase by phrase with no cleanup, and the popup should still show **Prompt Cleanup** selected.
8. Start dictation from the popup's button, then stop it from the popup. Text should go to the previous app, not the popup.
9. Unplug the microphone while dictating. The recorded part should be finished and inserted, and the tray icon should no longer be red.
10. Optionally run `./build.ps1 -LiveTests` with `GEMINI_API_KEY` and `RAMBLER_TEST_WAV` set.
