# UX research and decisions

The first cut borrows established patterns instead of inventing a new media workflow.

## Competitor patterns reviewed

- [Subtitle Edit’s main window](https://subtitleedit.github.io/subtitleedit/features/main-window.html) keeps the subtitle grid, selected cue editor, video, waveform, and playback actions in one configurable workspace. Z Transcribe follows the same review principle with video and a clickable caption timeline side by side, while postponing waveform editing.
- [HandBrake’s queue](https://handbrake.fr/docs/en/1.2.0/advanced/queue.html) makes “add to queue,” “start queue,” editing, and job status explicit. Z Transcribe uses a persistent queue with separate add/start controls and visible per-job stage/progress.
- [Descript automatic transcription](https://help.descript.com/hc/en-us/articles/10249424286477-Automatic-transcription) and [caption workflow](https://help.descript.com/hc/en-us/articles/37469585005197-Add-and-style-captions) reinforce importing media first, treating the timed transcript as the central artifact, and previewing captions against media. Z Transcribe similarly promotes completed jobs into a review workspace and keeps translation tied to the original cue boundaries.

## Information architecture

The left rail has only three destinations:

1. **Queue** — ingest, language configuration, current state, retry/remove/review.
2. **Review** — original media, VLC subtitle overlay, caption timeline, track switch.
3. **Settings** — credentials, FFmpeg paths, output and concurrency.

The global command bar keeps frequent queue operations available without burying them in menus. A persistent footer reports the latest app-level update.

## Feedback model

Every job moves through `Queued → Preparing → Transcribing → Translating → Ready`, with `Failed` and `Canceled` terminal alternatives. Progress and plain-language activity remain visible in the queue. Failures retain an actionable error and can be retried without re-adding the source.

## Next UX increments

- Per-job activity drawer with request IDs and sanitized diagnostics.
- Waveform, editable cue text, merge/split cues, and keyboard-first subtitle navigation.
- Multi-select queue actions, priorities, scheduled processing, and “pause after current.”
- Audio-track chooser for multi-language media.
- First-run prerequisite and credential wizard.

