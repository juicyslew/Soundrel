# Soundrel

Soundrel is a native Windows WPF soundboard for tabletop sessions. It plays
music and ambience from local files and works completely offline: there are no
accounts, online services, downloads, or network requirements.

## Features

- Continuous shuffled music playback with playlist and exact-track **Play Now**,
  **After Current**, pause/resume, skip, **Stop All**, and an explicit FIFO
  queue. **Clear Queue** is in the Queue section.
- A single **Transition: Hard Cut / Crossfade** toggle. Crossfades use the
  configured Medium duration and stagger.
- A state-aware master **Fade In / Fade Out** control and a **Fast / Slow**
  runtime speed toggle. Stop All fades the complete output before stopping
  sources; starting new audio from globally stopped state fades it in.
- Dedicated, searchable **Ambience** area with compact tiles, independent
  play/stop and source-volume controls, and hidden sources that continue
  playing while filtered out.
- **Ambient Presets** per library. Presets can be selected, applied again,
  saved or updated, and deleted with confirmation. A preset captures the
  currently enabled ambience sounds and their requested source volumes; an
  empty preset turns ambience off.
- Separate music, ambience-bus, and master levels. Active volume changes ramp
  at the configured Medium rate, while the controls continue to show the
  requested targets. Volume tracks also support proportional clicking.
- Folder-based intensity groups, such as `Calm` and `Tense` under `Forest`,
  with qualified names such as `Forest / Calm` where needed.
- Managed MP3 decoding and WAV playback without optional codec packs.

## Requirements

- Windows 10 or Windows 11, x64.
- MP3 and WAV audio files in a local folder. Other formats are ignored.

Soundrel discovers playlists using a convention like this:

```text
Soundscape Library/
├─ Music/
│  ├─ Forest/
│  │  ├─ Calm/
│  │  │  ├─ track-1.mp3
│  │  │  └─ track-2.wav
│  │  └─ Tense/
│  │     └─ track-1.mp3
│  └─ Tavern/
│     └─ music-1.mp3
└─ Ambience/
   ├─ rain.wav
   └─ wind.mp3
```

Leaf directories under `Music` are playlists; a parent containing related leaf
playlists is shown as a group. A directory that directly contains music files
is itself a playlist. Audio files directly under `Ambience` are available as
looping ambience sources.

## Use

1. Start Soundrel and choose the root of a local sound library.
2. Select a playlist in **Library**. The **Selected Playlist** heading and
   session information use `Group / Playlist` paths when a group is present.
3. Use playlist **Play Now** or **After Current**. Use a track's **Play Now**
   to start that exact track, or **Queue** to add it to the FIFO queue. Play Now
   preserves explicitly queued tracks.
4. In **Ambience**, search by name and use each compact tile's play/stop toggle
   and volume slider. Filtering only changes what is shown; it does not stop
   hidden sounds.
5. Select **Transition: Hard Cut** or **Transition: Crossfade** for immediate
   playlist and track replacements. Use the master **Fade In / Fade Out**
   control and choose **Fade speed: Fast** or **Slow** for new or reversed
   master fades. **Stop All** fades to silence, then stops music and ambience;
   the active playlist selection and explicit queue remain available. Resume
   does not trigger a new automatic master fade-in.
6. Use **Ambient Presets** to select a preset (selection applies immediately),
   **Apply** it again, **Save** or update it by name, or **Delete** it after
   confirmation. Presets are stored separately for each library. Missing
   inactive files are skipped and reported while remaining in the preset.
   Rescan after changing files on disk.

## Timing settings

Settings use schema version 5 and are stored at
`%LocalAppData%\Soundrel\settings.json`. The timing fields are seconds:

```json
{
  "version": 5,
  "fastFadeSeconds": 2,
  "mediumFadeSeconds": 5,
  "slowFadeSeconds": 10,
  "crossfadeStaggerSeconds": 1
}
```

The defaults are Fast 2 seconds, Medium 5 seconds, Slow 10 seconds, and a
1-second crossfade stagger. Timing values must be finite; fade durations must
be greater than zero, and the stagger must be at least zero and no greater than
Medium. Close Soundrel before editing the JSON and restart it for changes to
take effect. Invalid fade durations recover to their documented defaults with
a warning; an invalid stagger recovers to the 1-second default capped at the
effective Medium duration. Saving other settings preserves valid timing values
and Ambient Presets.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
then run these commands from the repository root:

```powershell
dotnet restore Soundrel.slnx
dotnet build Soundrel.slnx --configuration Release
dotnet test Soundrel.slnx --configuration Release --no-build
```

To create the portable self-contained Windows x64 publish folder with the
`WinX64` publish profile:

```powershell
dotnet publish Soundrel.csproj --configuration Release -p:PublishProfile=WinX64
```

The publish folder convention is `artifacts/publish/win-x64/`. A distributable
ZIP can be made from that folder as `artifacts/Soundrel-WinX64.zip`, preserving
the folder contents at the root of the archive. The published build is
self-contained and includes its .NET runtime and required dependencies. Users
only need to extract the ZIP (or copy the folder) and run `Soundrel.exe`; no
.NET installation, installer, runtime bootstrapper, or network connection is
needed.

## Offline verification

For a release check, copy `artifacts/publish/win-x64/` or its ZIP and a local
MP3/WAV library to a clean Windows 10/11 x64 machine with no separately
installed .NET runtime. Disable all network adapters before extraction and
launch. Confirm that the package contains the executable, runtime files,
managed dependencies, and JSON files; starts without a bootstrapper or
download; discovers both formats; and supports a complete music-plus-ambience
session. Check volume persistence after restarting the application. The full
manual release checklist is in
[`MANUAL_TEST_CHECKLIST.md`](MANUAL_TEST_CHECKLIST.md).

## Known limits

- Soundrel supports local files only: MP3 and WAV; it does not stream or fetch
  audio.
- MP3 encoder padding can affect audible loop seams.
- Many loud sources mixed together may reach the final output clamp.
- Real output-device switching and other hardware-dependent checks still
  require manual verification on the target Windows hardware.

## License

Soundrel is available under the [MIT License](LICENSE).
