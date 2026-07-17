# Z Transcribe Desktop

A Windows desktop media queue for Zoom AI Services Scribe and Translator. Add video or audio files, process them in order, and review the resulting WebVTT captions over the original media in an embedded VLC player.

This is an open-source early working cut intended for Windows testing. The processing core is UI-neutral so a native-feeling macOS shell can follow without rewriting the queue, VTT, Zoom, or FFmpeg orchestration.

## What is included

- Persistent drag-and-drop media queue with retry, cancel, removal, progress, and event status.
- Transcription choices limited to English (`en-US`), Simplified Chinese (`zh-CN`), Japanese (`ja-JP`), Spanish (`es-ES`), and Italian (`it-IT`).
- Optional cue-preserving translation. Non-English pairs such as Japanese → Chinese are routed through English because Zoom Translator requires English on one side.
- FFmpeg/FFprobe integration that selects the first audio stream with `-vn -c:a copy`. It does **not** alter speed, resample, remix, or recompress audio.
- Long-media segmentation into 15-minute audio-only parts, two concurrent Scribe calls by default, and restored original-timeline timestamps.
- Original and translated `.vtt` files plus transcript JSON.
- Embedded LibVLCSharp player with WebVTT overlay and a seekable caption timeline.
- Zoom API key and API secret stored in Windows Credential Manager, never in `settings.json` or the queue.
- Windows GitHub Actions build and downloadable `win-x64` workflow artifact.

## Windows prerequisites

1. Windows 10/11 x64.
2. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for development, or the .NET 10 Desktop Runtime for a framework-dependent published build.
3. FFmpeg and FFprobe. Install a reputable Windows distribution, then either add its `bin` directory to `PATH` or enter the full executable paths in Settings.
4. Zoom Build API key and API secret with AI Services access.

Compilation is validated locally and by the repository's Windows GitHub Actions workflow.

## Run from Visual Studio

1. Clone the repository.
2. Open `ZTranscribe.sln` in Visual Studio 2026 (or a current Visual Studio with .NET 10 support).
3. Set `ZTranscribe.App` as the startup project.
4. Run the app, open Settings, and enter the Zoom Build API key and secret.
5. Set FFmpeg and FFprobe paths if they are not on `PATH`.
6. Add media, select its spoken language and optional translation language, then start the queue.
7. Double-click a Ready job to review it with captions.

## Audio integrity

Supported stream-copy mappings are:

| Input audio codec | Upload container | Result |
|---|---|---|
| AAC / ALAC | M4A | copied unchanged |
| MP3 | MP3 | copied unchanged |
| PCM | WAV | copied unchanged |

If the source codec cannot be placed in Zoom's WAV/M4A/MP3 inputs without transcoding, the job fails with an explicit message. This is deliberate: the app never silently degrades or changes customer audio.

Temporary audio lives under `%LOCALAPPDATA%\Z Transcribe\work` and is removed in a `finally` block after success, failure, or cancellation. Final outputs default to a `Z Transcribe Outputs` folder beside the source file.

## Repository map

- `src/ZTranscribe.Core` — models, service contracts, language routing, WebVTT.
- `src/ZTranscribe.Infrastructure` — FFmpeg/ffprobe, Zoom HTTP/JWT, persistence, queue pipeline.
- `src/ZTranscribe.App` — WPF UI, Windows Credential Manager, LibVLCSharp playback.
- `tests/ZTranscribe.Core.Tests` — dependency-free executable checks run in CI.
- `docs` — UX rationale, architecture, security, Windows test plan, and macOS path.

## Open source and license

Z Transcribe's project-owned source code is released under the [MIT License](LICENSE).
See [OPEN_SOURCE.md](OPEN_SOURCE.md) for contribution and security guidance, and
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency licensing and
redistribution considerations.

## Current first-cut limitations

- Jobs run sequentially; only segment uploads inside one job are parallel.
- “Cancel current” cancels the active job and stops that queue run. Start again to continue queued items.
- Credential validation avoids making a paid API request; the first job is the authoritative server-side credential check.
- The app uses the first audio stream. Multi-track selection is a planned enhancement.
- The FFmpeg binaries are not redistributed in this repository.

