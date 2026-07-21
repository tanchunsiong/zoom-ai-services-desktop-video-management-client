# Z Scribe Desktop

A Windows desktop media queue for Zoom AI Services Scribe, Translator, and Summarizer. Add video or audio files, process them in order, and review the resulting captions and summaries alongside the original media in an embedded VLC player.

This is an open-source early working cut intended for Windows testing. The processing core is UI-neutral so a native-feeling macOS shell can follow without rewriting the queue, VTT, Zoom, or FFmpeg orchestration.

## What is included

- Persistent drag-and-drop media queue with retry, cancel, removal, progress, and event status.
- Transcription choices limited to English (`en-US`), Simplified Chinese (`zh-CN`), Japanese (`ja-JP`), Spanish (`es-ES`), and Italian (`it-IT`).
- Optional cue-preserving translation. Non-English pairs such as Japanese → Chinese are routed through English because Zoom Translator requires English on one side.
- FFmpeg/FFprobe integration that selects the first audio stream. Zoom-compatible codecs use `-vn -c:a copy`; every other FFmpeg-decodable codec, including AC3 and WMA, is decoded to PCM WAV without resampling, channel remixing, or additional lossy compression.
- Long-media segmentation into 15-minute audio-only parts, two concurrent Scribe calls by default, and restored original-timeline timestamps.
- Source-named `.vtt`, translated `.vtt`, transcript JSON, and summary sidecars written directly beside each source file without creating an output folder.
- Row-level Zoom Summarizer output is enabled by default, can be disabled per job, is saved as a source-named `.summary.md` sidecar, and is shown in the Review panel. Before transcription, its estimate is derived from media duration and language-aware character density, then includes a 10% contingency; actual cost continues to use measured API usage.
- Embedded LibVLCSharp player with software-decoded video, captions below the player, and a seekable caption timeline.
- Separate per-job and all-jobs `Estimate` and `Actual` cost columns, itemized for Scribe, Translator, and Summarizer. Pre-transcript character estimates are language-aware; completed jobs use measured Zoom API usage for Translator and Summarizer.
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
| Every other FFmpeg-decodable codec, including AC3 and WMA | WAV | decoded to 16-bit PCM without resampling or remixing |

Audio segments use a conservative 40 MB target to leave headroom below Zoom's documented 100 MB request limit and observed multipart gateway limits, including high-channel-count PCM audio. This changes the storage encoding for fallback codecs but does not apply another lossy codec, resample the waveform, or alter the channel layout. A source that FFmpeg cannot decode, is encrypted, is corrupt, or has no audio stream still cannot be processed.

Temporary audio lives under `%LOCALAPPDATA%\Z Transcribe\work`. Each segment is deleted immediately after its Scribe call succeeds or fails, and the job work directory is removed with retries after success, failure, or cancellation. Final outputs are source-named sidecars in the source file's directory, for example `meeting.vtt`, `meeting.translated-zh-CN.vtt`, `meeting.transcript.json`, and `meeting.summary.md`.

## Repository map

- `src/ZTranscribe.Core` — models, service contracts, language routing, WebVTT.
- `src/ZTranscribe.Infrastructure` — FFmpeg/ffprobe, Zoom HTTP/JWT, persistence, queue pipeline.
- `src/ZTranscribe.App` — WPF UI, Windows Credential Manager, LibVLCSharp playback.
- `tests/ZTranscribe.Core.Tests` — dependency-free executable checks run in CI.
- `docs` — UX rationale, architecture, security, Windows test plan, and macOS path.

## Open source and license

Z Scribe's project-owned source code is released under the [MIT License](LICENSE).
See [OPEN_SOURCE.md](OPEN_SOURCE.md) for contribution and security guidance, and
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency licensing and
redistribution considerations.

## Current first-cut limitations

- Jobs run sequentially; only segment uploads inside one job are parallel.
- “Cancel current” cancels the active job and stops that queue run. Start again to continue queued items.
- Credential validation avoids making a paid API request; the first job is the authoritative server-side credential check.
- Dollar estimates depend on the usage rates configured for your Zoom Build account and may differ from the final invoice.
- The app uses the first audio stream. Multi-track selection is a planned enhancement.
- The FFmpeg binaries are not redistributed in this repository.

