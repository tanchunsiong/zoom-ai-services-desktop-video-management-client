# Windows test plan

## Smoke test

1. Confirm the app starts with no credentials and directs the user to Settings.
2. Save credentials, close/reopen, and confirm the API key reloads while the secret is not displayed.
3. Configure FFmpeg/ffprobe and add MP4, M4A, MP3, and WAV samples by picker and drag/drop.
4. Confirm generated audio uses the original codec, sample rate, and channels with ffprobe.
5. Confirm captions, translated captions, transcript JSON, and summary are source-named files beside the media and no output directory is created.
6. Run each supported transcription language.
7. Run Japanese → Chinese and verify the activity shows two translation steps.
8. Close the app with queued jobs, reopen, and verify persistence.
9. Cancel an active job and verify its temporary work directory is removed.
10. Double-click a Ready job, switch original/translated captions, and seek by double-clicking cues.

## Long media

- Test media longer than 15 minutes and verify multiple Scribe calls produce one monotonic original-timeline VTT.
- Test a source longer than three hours and compare cues around every segment boundary.
- Test high-bitrate PCM where a 15-minute part may exceed 100 MB; reduce segment minutes and retry.

## Failure cases

- Missing FFmpeg and ffprobe paths.
- Video without audio.
- AC3, WMA, or another incompatible codec; confirm PCM WAV fallback preserves sample rate and channels and uses a 90,000,000-byte target per part below the 100 MB request limit.
- Force a successful, failed, and canceled Scribe call; confirm each converted part and its work directory are removed.
- Invalid credentials / missing AI Services entitlement.
- HTTP 429 and transient 5xx responses.
- Read-only source directory, source removed after queuing, and network loss mid-upload.

