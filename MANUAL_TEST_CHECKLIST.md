# Manual Test Checklist

All checks are intentionally unperformed. Leave an item unchecked until its setup, actions, and expected results have been completed; record the build, Windows version, devices, duration, and any failure when running it.

## Common setup

- Use Windows 10 or 11 x64 and a local library containing playable MP3 and WAV files.
- Create at least three music playlists (`A`, `B`, and `C`) with two or more tracks each, plus known tracks `Q1` and `Q2` for queue tests. Include one short ambience MP3 and one short ambience WAV.
- Put `Calm` and `Tense` under a group such as `Forest`, and also prepare an ungrouped playlist if possible.
- Test local files only. Do not install codecs or allow the application to obtain anything from the network.

## Layout and basic controls

- [ ] **Default and minimum layout, density, and contrast**
  - **Setup:** Launch with the normal window size, then resize to the existing minimum size.
  - **Actions:** Inspect Library, Selected Playlist, Ambience, Session/Transport, Queue, and the volume controls at both sizes. Select groups and playlists with the window focused and unfocused.
  - **Expected:** All areas remain usable; Ambience is a dedicated searchable area with compact tiles and its own scrolling; transport/session and Queue remain visible; library names are readable; selected text has clear contrast and hierarchy; no required control is hidden or clipped.

- [ ] **Transition and master-fade labels**
  - **Setup:** Load a library and start audio.
  - **Actions:** Toggle the immediate transition control and the master fade-speed control while observing their labels. Start a fade at full and at zero output.
  - **Expected:** The transition control clearly identifies Hard Cut or Crossfade. One state-aware master control says Fade Out when targeting full output and Fade In when targeting zero output; the speed control clearly identifies Fast or Slow. Controls do not describe obsolete separate quick/slow or separate hard-cut/crossfade buttons.

- [ ] **Qualified playlist names and queue placement**
  - **Setup:** Select `Calm` and `Tense` under `Forest`; queue `Q1` and `Q2`.
  - **Actions:** Inspect Selected Playlist, session, pending, and queue displays. Find Clear Queue.
  - **Expected:** Grouped names use `Forest / Calm` (or the applicable path), ungrouped names remain concise, queued entries show their source playlist, and Clear Queue is in Queue rather than among transport actions.

- [ ] **Exact-track Play Now and FIFO behavior**
  - **Setup:** Start playlist `A`, set `B` with After Current, and queue `Q1` then `Q2`.
  - **Actions:** Use Play Now on a known non-first track in playlist `C`; repeat with a track from a grouped playlist; let it end or use Skip.
  - **Expected:** The selected file, not a random file, starts; its source playlist becomes active, pending is cleared, and `Q1`/`Q2` remain FIFO. After the exact track ends, ordinary active-playlist and queue precedence resumes. Missing or unreadable tracks report an error without stuck state.

- [ ] **Proportional volume-track clicks**
  - **Setup:** Have music, ambience, and active ambience sources available.
  - **Actions:** Click approximately 25% and 50% along each individual ambience, music, ambience-bus, and master slider track; also drag and use keyboard controls.
  - **Expected:** Each click requests approximately the clicked percentage rather than only zero or 100%; dragging and keyboard navigation remain usable and displayed target percentages stay synchronized.

## Transition timing and master fades

- [ ] **Configured crossfade duration and stagger**
  - **Setup:** With Soundrel closed, set valid timing values in `%LocalAppData%\Soundrel\settings.json`, for example Medium 4 seconds and stagger 1 second. Select Crossfade and start playlist `A`.
  - **Actions:** Use After Current or Skip to move to `B`, and repeat with Play Now on `C`.
  - **Expected:** The outgoing source begins its fade first; the incoming source begins after the configured stagger and does not silently advance during the delay. The envelopes take the configured Medium full-scale duration. Transitions settle once, with no stale source or duplicate advancement. Do not judge subjective audio quality; record observable timing and source/state behavior.

- [ ] **Master fade timing and reversal**
  - **Setup:** Configure valid Fast and Slow values, start steady audio, and select each speed in turn.
  - **Actions:** Start a master fade, reverse it before completion, then repeat at the other speed and while the window is unfocused.
  - **Expected:** New or reversed fades use the selected speed. Reversal continues from the currently rendered gain rather than jumping to an endpoint; the UI remains responsive and the output reaches the requested target.

- [ ] **Fading Stop All and automatic restart**
  - **Setup:** Run music and ambience, begin a fade, and select both Fast and Slow in separate repetitions.
  - **Actions:** Press Stop All once and then repeatedly during the fade. After output reaches silence, start music with playlist Play Now, start exact-track Play Now, and start ambience in separate repetitions.
  - **Expected:** Stop All fades the complete output using the selected speed, then stops sources and leaves master fade gain at zero. It preserves the active playlist selection and explicit queue, does not create duplicate stop sequences, and no stale callback restarts audio. New audio from globally stopped state fades in automatically. Resume from paused music does not trigger this automatic master fade.

## Ambience, search, and persistent volume

- [ ] **Ambience search and hidden playback**
  - **Setup:** Load several ambience names, including names that differ only by case or substring.
  - **Actions:** Search with case changes and partial names, start a source, filter it out, change the search, and clear the field.
  - **Expected:** Matching is case-insensitive substring filtering; the list updates without a rescan; hidden sources continue playing; changing or clearing search does not stop, restart, or recreate a source.

- [ ] **Ambience lifecycle and source-volume retargeting**
  - **Setup:** Start one short ambience WAV and one short ambience MP3 with music, and leave their tiles visible.
  - **Actions:** Let each source complete at least three loops. Stop one, play it again during its fade-out, change each source volume during playback, and issue another target before the first ramp completes. Pause and skip music while ambience continues.
  - **Expected:** Both formats remain active through their loops. Each play/stop lifecycle change ramps at the configured Medium rate from the rendered lifecycle gain; reversing reuses the source and continues from that gain. Each source-volume ramp retargets from its rendered source gain, remains independent of lifecycle gain and other sources, and does not create duplicate sources. Music pause/skip does not stop or restart ambience.

- [ ] **Music, ambience-bus, and master volume smoothness**
  - **Setup:** Run music and ambience together; note the requested percentages.
  - **Actions:** Change each persistent volume slider while relevant audio is active, change it again before the ramp completes, then pause music and repeat with inactive or paused-only stages.
  - **Expected:** Active music, ambience-bus, and master levels move toward each requested target at the configured Medium full-scale rate and retarget without jumping to the old target. The UI/settings retain the requested value. Inactive or paused-only stages apply immediately for the next playback/resume.

- [ ] **Ambience preset save and update**
  - **Setup:** In one library, enable two ambience sounds and set distinct requested individual source volumes; leave another sound off.
  - **Actions:** Type a preset name and Save. Change the active sounds/volumes, then save the same name with different capitalization and changed targets.
  - **Expected:** The preset is saved for the current library and the second save updates the existing name case-insensitively. It captures only target-on sounds and requested source volumes, not rendered lifecycle gain, music volume, or ambience-bus state.

- [ ] **Ambience preset selection, Apply, and empty preset**
  - **Setup:** Create a preset with two sounds, then create an empty preset.
  - **Actions:** Select each preset, use Apply on an already matching selection, and apply the empty preset while sounds are playing.
  - **Expected:** Selecting a preset applies it immediately; Apply reapplies it. Current extras fade out, desired missing sounds start or reverse normally, shared source volumes retarget, and the empty preset leaves all ambience off. No duplicate source is created.

- [ ] **Ambience preset delete, library scope, restart, and missing files**
  - **Setup:** Create same-named or differently named presets in two library roots. Include a preset sound that is later made unavailable while inactive.
  - **Actions:** Restart Soundrel, switch libraries, apply the presets, remove/rename the inactive file, apply again, and delete a selected preset.
  - **Expected:** Presets persist across restart but remain associated with their library; deleting requires confirmation and removes only the selected library's preset. Missing inactive files are skipped/reported and retained in the preset for later recovery.

## Settings and error handling

- [ ] **Settings JSON valid values and preservation**
  - **Setup:** Close Soundrel and edit `%LocalAppData%\Soundrel\settings.json` with schema version 5 and valid fractional timing values where the stagger is no greater than Medium.
  - **Actions:** Relaunch, verify the timing behavior, then change an unrelated setting such as a volume, library, transition mode, or Ambient Preset and allow it to save. Inspect the JSON after closing.
  - **Expected:** Values are interpreted as seconds on the next launch, remain present after unrelated saves, and existing library, transition, volume, and preset data is retained.

- [ ] **Settings JSON invalid values and recovery**
  - **Setup:** Close Soundrel and make one or more timing fields non-finite, non-numeric, zero/negative where prohibited, or set the stagger above Medium.
  - **Actions:** Launch and inspect status/warning reporting; exercise the app and close it.
  - **Expected:** Startup does not crash or hang; invalid fade durations use their documented defaults, while an invalid stagger uses the 1-second default capped at the effective Medium duration, with a useful warning. Valid unrelated settings and presets are not discarded, and later saves do not replace valid timing values with hard-coded defaults.

- [ ] **Rapid commands and failures**
  - **Setup:** Make current, active, pending, queue, master-fade, and ambience states visible. Prepare a missing/unreadable music file and ambience file where safe.
  - **Actions:** Rapidly alternate playlist/track Play Now, After Current, Queue, Pause/Resume, Skip, Clear Queue, ambience toggles, source-volume changes, fade reversals, and repeated Stop All for at least 30 seconds. Issue Play Now during a crossfade stagger and trigger a missing-file or output failure if available.
  - **Expected:** Commands serialize into coherent state without crashes, hangs, overlapping replacement sources, duplicate advances, stale delayed starts, or stale callbacks restarting audio. Failures are visible and recoverable; the UI remains responsive.

- [ ] **Missing files and loop boundaries**
  - **Setup:** Start an ambience source, then delete or rename its file; queue a music file that is then removed. Include a short MP3 loop.
  - **Actions:** Let sources loop or restart and advance the music queue.
  - **Expected:** Missing files are skipped or stopped with a useful error, playback does not become stuck, and the application remains usable. Record observable loop-boundary behavior without treating subjective audio quality as an exact pass criterion.

## Retained local and release validation

- [ ] **Long-running playback**
  - **Setup:** Load a mixed MP3/WAV playlist with at least three playable tracks.
  - **Actions:** Leave it playing for a representative live session (at least two hours), allowing natural endings and using Skip at least once.
  - **Expected:** Playback remains responsive and continuous, elapsed time/state stay coherent, each ending or Skip advances exactly once, and no track repeats until the playable shuffle bag is exhausted.

- [ ] **Output-device switching** *(hardware-dependent)*
  - **Setup:** Play local audio through the current Windows default output device; have another device available where possible.
  - **Actions:** Change the Windows default device during playback, disconnect an active removable device, and restore a valid default.
  - **Expected:** The application remains responsive and does not crash or hang. Audio either follows the available device or stops with a useful error; playback can be started again after recovery. Record observed device behavior.

- [ ] **All networking disabled**
  - **Setup:** Put the library on a local disk and disable all network adapters before launch.
  - **Actions:** Discover the library, play MP3 and WAV, use Play Now/After Current, queue tracks, and complete a playlist transition.
  - **Expected:** Startup, discovery, controls, and playback work without a connection, download, sign-in, network prompt, or network-related delay/error.

- [ ] **Portable artifact on a clean machine** *(optional release validation; not performed by this milestone)*
  - **Setup:** On a clean Windows 10/11 x64 machine with no separately installed .NET runtime or optional codec pack, copy `artifacts/publish/win-x64/` or `artifacts/Soundrel-WinX64.zip` and a local MP3/WAV library. Verify the expected executable, runtime, managed dependencies, and JSON files are present; disable networking.
  - **Actions:** Extract if needed and launch. Exercise the documented music, ambience, queue, transition, fade, preset, and persistence workflows.
  - **Expected:** The self-contained artifact starts without an installer, runtime prompt, dependency download, or network connection, and the documented offline workflows operate on the clean machine. This is release validation, not a Milestone 4 completion claim.
