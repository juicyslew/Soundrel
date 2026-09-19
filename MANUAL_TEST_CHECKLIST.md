# Manual Test Checklist

All checks are intentionally unperformed. Leave an item unchecked until its setup, actions, and expected results have been completed; record the build, Windows version, devices, duration, and any failure when running it.

## Common setup

- Use Windows 10 or 11 x64 and a local library containing playable MP3 and WAV files.
- Create at least three music playlists (`A`, `B`, and `C`) with two or more tracks each, plus known tracks `Q1` and `Q2` for queue tests. Include one short ambience MP3 and one short ambience WAV for Milestone 4.
- Test local files only. Do not install codecs or allow the application to obtain anything from the network.

## Milestone 2: Core music playback and robustness

- [ ] **Long-running playback**
  - **Setup:** Load a mixed MP3/WAV playlist with at least three playable tracks.
  - **Actions:** Start the playlist and leave it playing for a representative live session (at least two hours). Observe natural track endings and use Skip at least once.
  - **Expected:** Playback remains responsive and continuous, elapsed time/state stay coherent, each natural ending or Skip advances exactly once, and no track repeats until the playlist's playable shuffle bag is exhausted.

- [ ] **Hard cuts, pending promotion, and FIFO queue behavior**
  - **Setup:** Start playlist `A` in hard-cut mode. Set `B` with After Current and queue `Q1`, then `Q2`.
  - **Actions:** Let the current track end (repeat once using Skip) and confirm the resulting order. Then queue `Q1` and `Q2` again, set a pending playlist, and use Play Now on `C` while music is playing.
  - **Expected:** At an ending or Skip, `B` is promoted to active before `Q1` plays; `Q1` then `Q2` play in FIFO order without changing active playlist `B`, after which playback returns to `B`. Play Now stops the prior music immediately without overlap, makes `C` active, clears the pending playlist, starts a shuffled `C` track, and preserves any explicitly queued tracks.

- [ ] **Rapid repeated button presses (core controls)**
  - **Setup:** Play music in hard-cut mode and make the current, active, pending, and queue displays visible.
  - **Actions:** Rapidly repeat and alternate Play Now, After Current, Queue, Pause/Resume, Skip, and Clear Queue for at least 30 seconds; press Stop All while commands or an end-of-track transition may be in flight.
  - **Expected:** Commands and end notifications do not create conflicting transitions, duplicate advances, overlapping hard-cut sources, crashes, or hangs. Accepted queue entries remain FIFO, displayed state remains coherent, and Stop All leaves silence with no stale transition restarting audio.

- [ ] **Remove a file during playback**
  - **Setup:** While one track is playing, queue a different known file followed by another playable file.
  - **Actions:** Delete or rename the first queued file in Explorer, then let the current track end or press Skip.
  - **Expected:** The missing file is skipped with a useful visible error; the application does not crash or become stuck, and playback advances once to the next playable queued or active-playlist track.

- [ ] **Switch or unplug an output device** *(hardware-dependent)*
  - **Setup:** Play a local MP3 or WAV through the current Windows default output device; have another output device available where possible.
  - **Actions:** Change the Windows default output device during playback, then disconnect an active removable output device and restore a valid default device.
  - **Expected:** The application remains responsive and does not crash or hang. Audio either follows the available device or stops with a useful playback error; after a valid device is available, playback can be started again. Record the observed device migration and recovery behavior.

- [ ] **Run with the window unfocused**
  - **Setup:** Start a playlist containing short MP3 and WAV tracks.
  - **Actions:** Focus another application or minimize Soundrel for at least ten minutes, allowing multiple natural track transitions, then return to Soundrel.
  - **Expected:** Playback and track advancement continue while unfocused, without gaps caused by loss of focus; on return, the displayed track, elapsed time, playlist, queue, and playback state are current.

- [ ] **Run with all networking disabled** *(Milestone 2 functional check; repeat on the Milestone 5 artifact)*
  - **Setup:** Put the library on a local disk, disable all network adapters, and verify Windows reports no network connection before launching Soundrel.
  - **Actions:** Launch the application, select/rescan the local library, play both MP3 and WAV, use Play Now and After Current, queue tracks, and complete a playlist transition.
  - **Expected:** Startup, discovery, controls, and playback work without a connection, download, sign-in, network prompt, or network-related delay or error.

## Milestone 3: Crossfades and master fades

- [ ] **Crossfades**
  - **Setup:** Start playlist `A`, select crossfade mode (the fixed 2-second immediate crossfade), set `B` with After Current, and queue `Q1` then `Q2`.
  - **Actions:** Use Play Now on `C`; during later transitions, repeat rapid Play Now and Skip commands.
  - **Expected:** The outgoing and incoming tracks overlap with smooth opposing gain envelopes and no hard cut, gap, stuck source, or excessive level spike. Play Now makes `C` active, clears pending `B`, and preserves `Q1`/`Q2` in FIFO order. Rapid commands settle into one coherent transition without stale audio or duplicate advancement.

- [ ] **Quick and slow fade-in and fade-out**
  - **Setup:** Play a steady, audible local track at a safe fixed output level; the implemented master fades are fixed at 2 seconds (quick) and 10 seconds (slow).
  - **Actions:** Run quick fade-out and fade-in, then slow fade-out and fade-in; time each operation and repeat once while the window is unfocused.
  - **Expected:** Quick fades take approximately 2 seconds and slow fades approximately 10 seconds. Gain changes smoothly to the intended endpoint without abrupt steps, UI stalls, or dependence on window focus.

## Milestone 4: Ambience

- [ ] **WAV/MP3 looping and source fades**
  - **Setup:** Start one short ambience WAV and one short ambience MP3 with music.
  - **Actions:** Let each source complete at least three loops; start and stop each source and listen for the fade-in and fade-out.
  - **Expected:** Both formats loop without unintended stops, seams, or clipping; each fade is smooth and audibly approximately 2 seconds.

- [ ] **Ambience fade reversal and simultaneous sources**
  - **Setup:** Start two ambience sources with different content.
  - **Actions:** Stop one source, then play it again during its fade-out; adjust each source gain independently while both play.
  - **Expected:** Stop-to-play reverses the same source cleanly, without a stale fade or abrupt transition; per-source gain affects only its source and both sources remain simultaneous.

- [ ] **Music controls independent from ambience**
  - **Setup:** Play music with at least two ambience sources.
  - **Actions:** Pause and skip music while ambience continues; adjust music, ambience, and master levels separately.
  - **Expected:** Music pause/skip does not stop or restart ambience, and each level affects only its intended bus or all audio as applicable.

- [ ] **Level persistence and ambience-only master fades**
  - **Setup:** Run ambience without music and set distinct music, ambience, and master levels.
  - **Actions:** Fade the master out and in, use Stop All, restart the application, and inspect the levels.
  - **Expected:** Master fades affect ambience-only playback smoothly, Stop All stops all audio and clears transitions, and all three levels persist across restart.

- [ ] **Stop All during transitions**
  - **Setup:** Start or stop ambience sources while fades are in progress.
  - **Actions:** Press Stop All during fade-in, fade-out, and stop-to-play reversal.
  - **Expected:** All audio stops immediately and no stale transition restarts a source.

- [ ] **Sibling intensity playlists**
  - **Setup:** Prepare sibling intensity playlists such as `Calm` and `Tense` under one group while ambience is playing.
  - **Actions:** Use Play Now on one sibling and After Current on the other.
  - **Expected:** Play Now switches immediately; After Current changes the pending playlist without interrupting the current track, and ambience remains independently controllable.

- [ ] **Deleted or missing ambience files**
  - **Setup:** Start an ambience source, then delete or rename its file; also prepare an MP3 with a short loop boundary.
  - **Actions:** Let the source loop or restart, and listen closely at the MP3 seam.
  - **Expected:** Missing ambience is skipped or stopped with a useful error without a crash or stuck transition; the MP3 seam has no audible clipping or unintended gap.

## Milestone 5: Published artifact and offline release

- [ ] **Inspect and run the portable artifact offline** *(hardware/manual release gate)*
  - **Setup:** On a clean Windows 10/11 x64 machine with no separately installed .NET runtime or optional codec pack, copy `artifacts/publish/win-x64/` or `artifacts/Soundrel-WinX64.zip` and a local MP3/WAV library onto the machine. Verify that the folder/ZIP contains `Soundrel.exe`, the published runtime files, managed dependencies, and JSON files. Disable all network adapters before extraction and first launch.
  - **Actions:** Extract the ZIP if needed and launch the executable. Confirm there is no installer, runtime bootstrapper, missing-runtime prompt, dependency download, or network request. Select the local library and complete a music-plus-ambience session using Play Now, After Current, FIFO queueing, Skip, Pause/Resume, Clear Queue, Stop All, crossfade, and master fades. Set distinct music, ambience, and master volumes, restart Soundrel, and inspect the restored levels.
  - **Expected:** The self-contained artifact extracts and starts without a .NET install or network connection. Both MP3 and WAV music, looping ambience, documented controls, and volume persistence work fully offline on the clean Windows 10/11 x64 machine.
