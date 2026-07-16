# macOS roadmap

The core and infrastructure projects intentionally avoid WPF types. A macOS client can reuse them and replace only the shell integrations:

- Build an Avalonia or .NET MAUI macOS UI with the same Queue / Review / Settings information architecture.
- Replace `WindowsCredentialVault` with a Keychain-backed `ICredentialVault`.
- Use LibVLCSharp.Avalonia or an mpv host for playback.
- Resolve Homebrew/system FFmpeg paths and retain identical stream-copy arguments.
- Store app state under the macOS Application Support directory through a platform path provider (the current `AppPaths` needs extraction before that work).
- Add a `macos-latest` CI build and signed/notarized packaging.

Before production macOS work, move `AppPaths` behind an interface and move queue collection notifications behind a dispatcher abstraction so UI changes are raised on the correct platform thread.

