# Z Scribe Desktop

A Windows desktop media queue for Zoom AI Services Scribe, Translator, and Summarizer. Add video or audio files, process them in order, and review the resulting captions and summaries alongside the original media in an embedded VLC player.

This is an open-source early working cut intended for Windows testing. The processing core is UI-neutral so a native-feeling macOS shell can follow without rewriting the queue, VTT, Zoom, or FFmpeg orchestration.

## What is included

- Persistent drag-and-drop media queue with retry, cancel, removal, progress, and event status.
- Transcription choices limited to English (`en-US`), Simplified Chinese (`zh-CN`), Japanese (`ja-JP`), Spanish (`es-ES`), and Italian (`it-IT`).
- Optional cue-preserving translation. Non-English pairs such as Japanese → Chinese are routed through English because Zoom Translator requires English on one side.
- FFmpeg/FFprobe integration that selects the first audio stream. Compatible mono/stereo codecs use `-vn -c:a copy`; incompatible or multi-channel media, including AC3 and WMA, is decoded to a Zoom-compatible 128 kbps MP3 with a stereo downmix when needed.
- Long-media segmentation into 15-minute audio-only parts, two concurrent Scribe calls by default, and restored original-timeline timestamps.
- Source-named `.vtt`, translated `.vtt`, transcript JSON, and summary sidecars written directly beside each source file without creating an output folder.
- Row-level Zoom Summarizer output is enabled by default, can be disabled per job, is saved as a source-named `.summary.md` sidecar, and is shown in the Review panel. Before transcription, its estimate is derived from media duration and language-aware character density, then includes a 10% contingency; actual cost continues to use measured API usage.
- Embedded LibVLCSharp player with software-decoded video, captions below the player, and a seekable caption timeline.
- Separate per-job and all-jobs `Estimate` and `Actual` cost columns, itemized for Scribe, Translator, and Summarizer. Pre-transcript character estimates are language-aware; completed jobs use measured Zoom API usage for Translator and Summarizer.
- Separate per-job and all-jobs processing-time estimates and actuals, itemized for Scribe, Translator, and Summarizer. Time estimates start as rough service-time heuristics and learn from completed jobs in the current queue, not provider SLAs; actuals are measured after a job completes.
- Summary responses are normalized to remove overlapping duplicate subsections while preserving distinct Recap, Summary, and Action Items content.
- Zoom API key and API secret stored in Windows Credential Manager, never in `settings.json` or the queue.

## Windows prerequisites

1. Windows 10/11 x64.
2. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for development, or the .NET 10 Desktop Runtime for a framework-dependent published build.
3. FFmpeg and FFprobe. Install a reputable Windows distribution, then either add its `bin` directory to `PATH` or enter the full executable paths in Settings.
4. Zoom Build API key and API secret with AI Services access.

Compilation is validated locally before changes are committed and pushed.

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
| PCM and every other FFmpeg-decodable codec, including AC3 and WMA | MP3 | encoded at 128 kbps and downmixed to mono/stereo when needed |

Audio segments target 80,000,000 raw file bytes. Scribe uploads follow Zoom's [official AI Services quickstart](https://github.com/zoom/AI-Services-Quickstart/) request shape: an `application/json` body containing a Base64 data URI. Base64 increases the HTTP body by about one third, so the extractor uses compact 128 kbps MP3 for codecs that cannot be copied directly. If the gateway still returns HTTP 413, the queue automatically halves the target and retries the audio parts. Compatibility encoding is lossy but preserves speech well while avoiding unsupported containers and oversized PCM uploads. A source that FFmpeg cannot decode, is encrypted, is corrupt, or has no audio stream still cannot be processed.

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

