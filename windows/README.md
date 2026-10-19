# Notely for Windows (alpha)

Independent C#/.NET 10 WPF client. macOS sources and installed app are unchanged.
Initial supported target: Windows 11 x64. The portable package includes .NET.

See [QUICKSTART-RU.txt](QUICKSTART-RU.txt) for first run, hotkeys and the physical-PC test checklist.

## Build and tests

On a Windows machine with .NET 10 SDK:

```powershell
dotnet test windows/Notely.Tests/Notely.Tests.csproj -c Release
dotnet publish windows/Notely.Windows/Notely.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o windows/artifacts/Notely-Windows-x64
windows/artifacts/Notely-Windows-x64/Notely.exe --smoke-test windows/artifacts/smoke-test.json
```

GitHub Actions runs the same steps on `windows-2025`, then uploads `Notely-Windows-x64`.
No Windows VM or SDK is installed on the development Mac. No API keys are needed by CI;
the network tests use scripted providers and fake HTTP responses.

The packaged-executable smoke test exercises DPAPI, settings persistence, WPF window
creation, markdown rendering, PCM conversion, and the capture-exclusion Win32 contract.
It also tests individual key edits, blank fields, immediate close, autosave and a fresh
process reading both keys; corrupt storage/write failures; window bounds/non-activating
restore; hotkey registration/collision and background message routing; screenshot capture
and automatic submission to a fake AI. It does not simulate physical mouse/key gestures.
It writes dummy credentials into its own fresh temporary directory, never the user's data.
It does not capture audio, contact cloud APIs, or demonstrate invisibility in a video call.

## Features and platform boundaries

- Separate WASAPI loopback and microphone streams, native float/PCM conversion to 16k mono PCM16.
- Explicit device choice or automatic Windows default. Reopen after default-device changes,
  device removal, or capture failure. An explicitly selected absent device is retried, not
  silently replaced with another microphone. Select the same output device as the call app.
- Silence frames during idle loopback allow Deepgram to finish a question.
- Deepgram nova-3 Russian streaming, keyterms, bounded audio queue, reconnect/backoff/keepalive.
- OpenRouter streaming with timeouts, HTTP and midstream error handling; no automatic repeat
  of a paid generation after a partial answer.
- Interrupted answers stay visible and are recorded in conversation context. Late callbacks
  cannot write into a newer request or session. Auto turns coalesce for 150 ms; resumed
  speech (VAD/interim signals) cancels pending auto work without storing unstable words.
  Deepgram endpointing stays at 400 ms, with a 1000 ms UtteranceEnd fallback. A new auto
  candidate leaves the current stream running until it actually produces an answer. Exact
  SKIP sentinels (including Markdown wrappers) and candidate errors preserve the old stream.
  Only one visible answer and one unconfirmed candidate can run concurrently. This may briefly
  overlap two paid generations, avoiding a separate classification API call.
- Microphone PCM is never muted because system audio is active. Short replies/rewordings are
  kept; only near-verbatim long replay of one interviewer utterance within 3 seconds is dropped.
  Headphones remain preferable when speaker echo is strong; real echo handling needs PC testing.
- Older conversation is summarized after 12 minutes. Failed/empty summaries preserve verbatim
  entries and retry on subsequent activity after 30, 60, 120, 240, then up to 300 seconds.
- Observer callbacks are serialized outside the state lock; profile/model delegates also run
  outside it. Pause cancels unconfirmed auto work, while visible/manual answers can finish.
- Images are resized to a maximum of 2400 pixels on the long edge, JPEG quality 75, and sent
  once. Subsequent requests use the screen description in the model's textual answer.
- Cursor-monitor screenshot, clipboard image, text input, profile and keyterms, model selection,
  font/width controls, scrolling history, pause, new conversation, tray and global hotkeys.
- The Screenshot button, menu and global Ctrl+Alt+S all capture and immediately submit via
  the existing image/text send path. Registration conflicts remain visible in the overlay.
- A larger dedicated drag bar leaves edge/corner resizing intact. Movement, resize, restore,
  and display changes constrain the whole overlay to the current monitor's working area,
  using physical-pixel bounds (including negative-coordinate monitors). Maximizing this
  non-activating overlay is disabled; hidden/maximized state is normalized before showing.
- WDA_EXCLUDEFROMCAPTURE on the overlay, settings in the same protected window. Notely's tray
  icon/process name remain normal. Capture exclusion is best effort and requires a second-device
  test in the exact conferencing app/version.
- Per-user DPAPI credentials; settings/profile under `%LOCALAPPDATA%\Notely`. Logs omit transcript,
  responses, image data, profile and credentials. Diagnostic export includes device names.
- Key edits autosave after 600 ms and flush when settings/the application close. Both keys
  are saved independently; blank fields preserve existing values. The existing encrypted
  `keys.dat` format is reused. No cloud service is contacted to save keys.

## Evidence still required from physical Windows

WASAPI with actual endpoints/driver formats; Bluetooth/USB switches and wake; microphone permission;
real Deepgram streaming in a call; visual rendering at different DPIs; fullscreen/multi-monitor
behaviour; real Zoom/Meet/Telemost screen sharing; SmartScreen/Defender and hotkey collisions.

The first package is unsigned. No claim of production readiness or guaranteed invisibility is made.
Code in this port is independent of Natively; no Natively sources or prompts were copied.

## Rollback

All Windows changes are in branch `windows-port` and paths `windows/`, `.github/workflows/windows.yml`,
plus Windows build ignores. Use the original `main` checkout for macOS. Downloading or developing
this branch does not replace `~/Applications/Notely.app` or modify the Mac's saved data/keys.
