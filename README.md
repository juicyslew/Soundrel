# Soundrel

Soundrel is a native Windows desktop soundboard for tabletop sessions. It
plays music and ambience from local files and is designed to work completely
offline: there are no accounts, online services, downloads, or network
requirements.

## Features

- Continuous shuffled music playback with Play Now, After Current, pause,
  skip, Stop All, and an explicit FIFO track queue.
- Hard cuts or fixed two-second crossfades when switching playlists.
- Quick two-second and slow ten-second master fade in/out.
- Multiple looping ambience sources with independent controls and separate
  music, ambience, and master levels.
- Folder-based intensity groups, such as `Calm` and `Tense` under `Forest`.
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
2. Select a playlist and use **Play Now** or **After Current**. Queue individual
   tracks when they should play in FIFO order.
3. Use the music, ambience, master, crossfade, and fade controls during the
   session. Rescan after changing files on disk.

Basic settings are stored at
`%LocalAppData%/Soundrel/settings.json`, including the selected library and
volume levels.

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
installed .NET runtime.
Disable all network adapters before extraction and launch. Confirm that the
package contains the executable, runtime files, managed dependencies, and JSON
files; starts without a bootstrapper or download; discovers both formats; and
supports a complete music-plus-ambience session. Check volume persistence after
restarting the application. The full manual release checklist is in
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
