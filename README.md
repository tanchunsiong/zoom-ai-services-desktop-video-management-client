# Z Scribe Desktop

A Windows desktop media queue for Zoom AI Services Scribe, Translator, and Summarizer. Add video or audio files, process them in order, and review the resulting captions and summaries alongside the original media in an embedded VLC player.

This is an open-source early working cut intended for Windows testing. The processing core is UI-neutral so a native-feeling macOS shell can follow without rewriting the queue, VTT, Zoom, or FFmpeg orchestration.

## What is included

- Persistent drag-and-drop media queue with retry, cancel, removal, progress, and event status.
- Case-insensitive queue search across media filenames, original and translated captions, transcript JSON, and summary sidecars, composed with the existing media-status filters.
- Live mode with selectable microphone or Windows speaker-loopback capture, a live input meter, vocabulary JSON, interim words, completed speech turns, and optional paired Zoom translation.
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

## Live mode

Open the Live tab, choose **Microphone** or **Speaker loopback**, select the input device and transcription language, then choose **Start live**. Speaker loopback captures the audio mix playing through the selected Windows output endpoint. The app normalizes either source to little-endian 16 kHz mono PCM16 audio, assembles it into approximately 100 ms frames, and sends each frame as a binary WebSocket message.

The input meter reports peak level in dBFS, changes from green to orange above `-12 dBFS`, and holds red for one second when the source clips. Optional **Auto gain** applies up to `8x` software gain to the streamed PCM with a `-18 dBFS` RMS target and `-1 dBFS` peak headroom; it does not change the Windows microphone or speaker volume.

Choose **Stop** to stop audio capture, drain buffered frames, send `session.close`, and wait for final transcript segments before disconnecting. Live audio remains in memory and is not written to disk. When speaker output is silent, the app sends quiet keepalive frames so the Live session does not hit its idle timeout.

The app waits for Zoom's `session.updated` acknowledgement before streaming audio. While Zoom transitions its deployed endpoint, the Live session payload mirrors the locale, optional vocabulary, and PCM16 format in both the accepted top-level fields and the newer nested `config` and `audio.format` schema. Neither form includes VAD configuration.

The optional vocabulary editor accepts a vocabulary object, a top-level `vocabulary` object, or a full ASR payload containing `config.vocabulary`. It validates phrases, pronunciations, and aliases locally, remembers the JSON between launches, and includes the structured vocabulary in the next Live session. A ready-to-use example is provided on first launch.

Live translation is optional and remembered between launches. Each completed Scribe segment is translated independently with Zoom Translator Fast mode and displayed directly below its source caption. Non-English language pairs use the existing English bridge, and segment IDs keep translations paired correctly when requests finish out of order.

The Live transcript surface follows Zoom's beta quickstart behavior: any non-final event containing `transcript`, `text`, or `delta` replaces the fixed-height **Detected words / not yet final** textbox above the segment history, and `transcription.completed` moves finalized text into the completed list below.

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
- `src/ZTranscribe.App` — WPF UI, Windows Credential Manager, NAudio microphone and WASAPI loopback capture, LibVLCSharp playback.
- `tests/ZTranscribe.Core.Tests` — executable checks run locally without GitHub Actions.
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
- Speaker loopback captures the selected Windows playback endpoint's mixed output. Per-application audio selection is not currently available.
- The FFmpeg binaries are not redistributed in this repository.

