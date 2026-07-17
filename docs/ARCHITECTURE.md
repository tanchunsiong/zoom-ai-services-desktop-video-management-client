# Architecture

```text
WPF shell
  ├─ Windows Credential Manager
  ├─ LibVLCSharp playback + VTT overlay
  └─ JobQueueService
       ├─ JSON queue/settings stores
       ├─ ffprobe metadata
       ├─ FFmpeg audio stream-copy parts
       ├─ Zoom Scribe (bounded parallel calls)
       ├─ timeline merge + WebVTT
       └─ Zoom Translator (optional, cue markers preserved)
```

`ZTranscribe.Core` targets plain `net10.0` and owns models and contracts. `ZTranscribe.Infrastructure` is also platform-neutral: it shells out to configured FFmpeg binaries and uses `HttpClient`. `ZTranscribe.App` targets `net10.0-windows` and contains WPF, LibVLCSharp.WPF, and Windows Credential Manager interop.

## Processing lifecycle

1. Probe source with ffprobe and reject files without an audio stream.
2. Map the source codec to a Zoom-supported container.
3. Extract time-bounded audio-only parts using `-map 0:a:0 -vn -c:a copy`.
4. Reject any part above Zoom Scribe's request size limit.
5. Send up to the configured number of parts concurrently.
6. Offset each returned segment by its part start, sort, and renumber.
7. Write original WebVTT and transcript JSON.
8. If requested, translate cue batches while retaining marker-to-timestamp mapping. Bridge non-English pairs through English.
9. Write translated WebVTT.
10. Delete the work directory regardless of outcome and persist the final queue state.

## Failure boundaries

- Process failures show the final useful FFmpeg/ffprobe message.
- HTTP 429, 502, 503, and 504 responses are retried twice with delay and `Retry-After` support.
- Other upstream errors retain status and a bounded response excerpt, never credentials.
- Interrupted in-flight states are recovered to Queued at the next launch.

## Data locations

- Queue: `%LOCALAPPDATA%\Z Transcribe\queue.json`
- Non-secret settings: `%LOCALAPPDATA%\Z Transcribe\settings.json`
- Temporary audio: `%LOCALAPPDATA%\Z Transcribe\work\<job-id>`
- Credentials: Windows Credential Manager target `ZTranscribe.ZoomBuildCredentials`
- Outputs: configurable, or `Z Transcribe Outputs\<source-name>` beside source

