# Windows test plan

## Smoke test

1. Confirm the app starts with no credentials and directs the user to Settings.
2. Save credentials, close/reopen, and confirm the API key reloads while the secret is not displayed.
3. Configure FFmpeg/ffprobe and add MP4, M4A, MP3, and WAV samples by picker and drag/drop.
4. Confirm generated audio uses the original codec, sample rate, and channels with ffprobe.
5. Run each supported transcription language.
6. Run Japanese → Chinese and verify the activity shows two translation steps.
7. Close the app with queued jobs, reopen, and verify persistence.
8. Cancel an active job and verify its temporary work directory is removed.
9. Double-click a Ready job, switch original/translated captions, and seek by double-clicking cues.
10. Process the same clip once normally, remove/re-add it with 2× enabled, and process it again. Confirm FFmpeg uses `atempo=2.0`, the accelerated upload is approximately half the duration, and both VTT files align to the same points in the original video.

## Long media

- Test media longer than 15 minutes and verify multiple Scribe calls produce one monotonic original-timeline VTT.
- Test a source longer than three hours and compare cues around every segment boundary.
- Test high-bitrate PCM where a 15-minute part may exceed 100 MB; reduce segment minutes and retry.

## Failure cases

- Missing FFmpeg and ffprobe paths.
- Video without audio.
- Unsupported audio codec that would require transcoding.
- Invalid credentials / missing AI Services entitlement.
- HTTP 429 and transient 5xx responses.
- Read-only output directory, source removed after queuing, and network loss mid-upload.
