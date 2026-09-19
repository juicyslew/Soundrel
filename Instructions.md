You are the project lead for improving Soundrel, a small native Windows desktop
application for controlling music and ambience during tabletop role-playing
games.

Soundrel already has a working MVP. Do not scaffold a new application or replace
the existing implementation. Inspect the repository first, understand the
current architecture and behavior, and make focused improvements to the MVP.

Produce direct, readable changes rather than introducing a generalized
architecture. Existing behavior not explicitly changed by this document should
continue to work.

MVP status
==========

The existing application already provides:

- Local MP3 and WAV library discovery.
- Filesystem-based playlist groups and playlists.
- Continuous shuffled music playback.
- Active and pending playlists.
- An explicit FIFO track queue.
- Hard-cut and crossfade transitions.
- Pause, resume, skip, clear-queue, and stop-all behavior.
- Quick and slow master fades.
- Multiple looping ambience sources.
- Individual ambience levels.
- Separate music, ambience, and master volume controls.
- Local JSON settings.
- A self-contained, offline Windows x64 release.
- Automated tests and a manual test checklist.

Treat these capabilities as the baseline. Improve them without rebuilding
working systems unnecessarily.

Non-negotiable decisions
========================

- Continue using C#, .NET 10 LTS, and WPF.
- Continue using NAudio 3 for mixing and WASAPI output.
- Continue using NLayer and NLayer.NAudioSupport for managed MP3 decoding.
- Support MP3 and WAV files only.
- Target Windows 10/11 x64.
- Keep the application completely local and offline.
- Do not add Electron, React, JavaScript, TypeScript, HTML, CSS, WebView2, or
  another web-based UI technology.
- Do not add Spotify, FMOD, cloud services, servers, accounts, authentication,
  telemetry, online APIs, metadata lookup, update checking, or networking.
- Do not add a database, dependency-injection container, mediator framework,
  plugin system, provider framework, or separate service process.
- Continue storing simple settings in JSON under LocalApplicationData.
- Preserve self-contained win-x64 publishing.
- Prefer small changes to the existing classes over new abstraction layers.
- Preserve existing user changes and avoid unrelated cleanup.
- Do not commit, push, reset, or discard changes unless explicitly requested.
- Do not change the library folder convention.
- Do not regress missing-file handling, unreadable-file handling, output-fault
  handling, shuffle-bag behavior, queue ordering, or stale-event protection.

Existing architecture
=====================

Work with the current structure rather than replacing it. Important existing
components include:

- MainWindow and MainViewModel
- AudioEngine and IAudioEngine
- PlaybackController
- LibraryScanner
- SettingsService and AppSettings
- Library and playback model classes
- Focused test doubles and unit tests

Continue using one NAudio mixing pipeline with music, ambience, persistent
volume, and master-fade stages.

Audio fades must remain sample-counted operations in the audio pipeline. Do not
implement fades using WPF timers, sleeps on the UI thread, or repeated UI volume
updates.

Do not perform file opening, disposal, UI work, or other blocking work directly
on the real-time audio callback thread.

Existing library convention
===========================

Continue using a local library structured like:

  Soundscape Library/
    Music/
      Forest/
        Calm/
          track-1.mp3
          track-2.wav
        Tense/
          track-1.mp3
      Tavern/
        music-1.mp3
    Ambience/
      rain.wav
      wind.mp3

Rules:

- A leaf directory under Music is a playlist.
- A parent containing related playlists is displayed as a group.
- A directory directly containing music files can itself be a playlist.
- Files directly under Ambience are looping ambience sources.
- Unsupported extensions are ignored.
- Display names come from filenames without extensions.
- Playlist and group discovery remains filesystem-based.

Improvement goals
=================

This refinement pass must accomplish the following:

1. Make library entries easier to read.
2. Give Ambience a prominent, dedicated section of the main interface.
3. Make large ambience libraries searchable and substantially more compact.
4. Simplify ambience, transition-mode, and master-fade controls.
5. Make session information concise and place it near transport controls.
6. Put Clear Queue inside the Queue section rather than the transport controls.
7. Add Play Now for individual tracks.
8. Show the selected playlist's group path.
9. Make every volume slider respond proportionally when its track is clicked.
10. Smoothly fade individual ambience volume changes.
11. Make fast, medium, and slow fade durations configurable through JSON.
12. Use a configurable stagger to reduce crossfade overlap.
13. Make Stop All fade the complete output before stopping it.
14. Automatically fade in when playback begins from a globally stopped state.
15. Improve selected group and playlist colors and text contrast.

Configurable fade settings
==========================

Use the existing settings file:

  %LocalAppData%/Soundrel/settings.json

Add these settings:

```json
{
  "fastFadeSeconds": 2,
  "mediumFadeSeconds": 5,
  "slowFadeSeconds": 10,
  "crossfadeStaggerSeconds": 1
}
```

These keys are additions to the existing settings object, not a replacement for
the existing library-path, transition-mode, and volume settings.

Requirements:

- Fast fade defaults to 2 seconds.
- Medium fade defaults to 5 seconds.
- Slow fade defaults to 10 seconds.
- Crossfade stagger defaults to 1 second.
- Numeric values may contain fractional seconds.
- Load these settings when the application starts.
- Hot reloading is not required.
- Do not add an in-application editor for these values.
- The user must be able to close Soundrel, edit the JSON, and have the new values
  take effect on the next launch.
- Preserve these values whenever Soundrel saves unrelated settings.
- Migrate older settings versions without losing the selected library path,
  transition preference, or saved volume levels.
- Invalid values must not crash startup.
- Fade durations must be finite and greater than zero.
- Crossfade stagger must be finite, non-negative, and no greater than the medium
  fade duration.
- Use documented defaults for invalid timing fields and display a useful warning.
- Do not silently rewrite a valid manual configuration with hard-coded values.

Timing usage
============

Use the configured durations consistently:

- Fast and Slow are the two user-selectable master-fade speeds.
- Medium is not another master-fade button.
- Playlist crossfades use the Medium duration.
- Ambience play fades use the Medium duration.
- Ambience stop fades use the Medium duration.
- Active ambience source-volume changes use the Medium duration.
- Music, ambience-bus, and master-volume sliders remain immediate.

A duration represents the time required for a full-scale zero-to-one or
one-to-zero fade. When reversing or starting from an intermediate gain, scale
the remaining duration by the remaining distance so the rate stays consistent.

Master fade controls
====================

Replace the four existing master-fade buttons with two compact controls:

1. One fade-direction button.
2. One Fast/Slow speed toggle.

Fade-direction behavior:

- At full gain, the button says `Fade Out`.
- While fading in, the button says `Fade Out` and can reverse the fade.
- At zero gain, the button says `Fade In`.
- While fading out, the button says `Fade In` and can reverse the fade.
- Do not offer Fade In while already targeting full gain.
- Do not offer Fade Out while already targeting zero gain.
- A reversal must begin smoothly from the current gain.
- Do not jump to an endpoint when reversing.
- Disable the fade-direction control when there is no active audio for it to
  affect.

Fade-speed behavior:

- The toggle switches between Fast and Slow.
- Make the current selection visually and textually unambiguous.
- Show the configured duration in a tooltip or compact label where practical.
- Default to Fast when the application launches.
- Persisting the selected Fast/Slow toggle across launches is not required.
- Changing the toggle controls newly initiated or reversed fades. It does not
  need to retime a fade already in progress.

Stop All behavior
=================

Stop All remains a global music-and-ambience operation, but it must no longer
stop active audio abruptly.

When Stop All is pressed:

1. Fade the complete master output to zero using the currently selected Fast or
   Slow duration.
2. Begin from the master gain that is active at that moment.
3. Do not stack duplicate fade operations.
4. If already at zero gain, skip the unnecessary fade.
5. After reaching zero, stop and dispose all music and ambience sources.
6. Clear pending and transitional playback state.
7. Prevent delayed completion callbacks from restarting or advancing playback.
8. Leave the master fade gain at zero.
9. Preserve the active-playlist selection and explicit queue, matching the
   existing Stop All semantics. Clear Queue remains a separate operation.
10. Keep the WPF UI responsive while the fade completes.

Repeated Stop All presses must not create multiple stop sequences. Other
playback commands must remain serialized safely with the stop operation.

Starting playback from stopped
==============================

Whenever Soundrel goes from having no active music or ambience to playing a new
source, the complete output must automatically fade in using the selected Fast
or Slow speed.

This applies to:

- Playlist Play Now.
- Individual-track Play Now.
- Starting ambience when no other source is active.
- Any other action that begins audio from a globally stopped state.

Requirements:

- Begin the new source with the master fade gain at zero.
- Start the source and fade the master to full using the selected duration.
- The fade must be sample-counted.
- A fresh application launch counts as a globally stopped state.
- Playback following Stop All must fade in because Stop All leaves master fade
  gain at zero.
- If the final source stops and the application later starts another source,
  the new source must fade in.
- Resuming paused music is not a stopped-to-playing transition and must not
  trigger an automatic master fade.
- Starting music while ambience is already active is not a globally stopped
  transition.
- Starting ambience while music exists, including paused music, is not a
  globally stopped transition.
- Replacing an active or paused music track uses the selected hard-cut or
  crossfade behavior rather than a new master fade-in.

Crossfade behavior
==================

Crossfades currently start outgoing and incoming envelopes at the same time.
Change them to use a staggered transition.

Required sequence:

1. Start the outgoing fade immediately.
2. Use the configured Medium duration for the outgoing fade.
3. Wait for `crossfadeStaggerSeconds`.
4. Start the incoming track and its Medium fade-in.
5. Do not let the incoming track advance silently during the stagger delay.
6. Dispose the outgoing source after its fade completes.
7. Cancel or ignore stale delayed starts and completion callbacks when another
   transition supersedes the current one.

With the default settings:

- The outgoing fade begins at 0 seconds.
- The incoming source and fade begin at 1 second.
- Each envelope uses a 5-second full-scale duration.

An offset of zero restores simultaneous fade starts. An offset equal to the
Medium duration removes deliberate overlap. Do not allow an offset greater than
the Medium duration.

Continue using simple smooth curves, including the existing equal-power curves
where practical. Do not add a complex crossfade editor.

Immediate transition control
============================

Replace the separate Hard Cut and Crossfade buttons with one toggle button.

Requirements:

- The button must clearly show the currently selected mode, for example
  `Transition: Hard Cut` or `Transition: Crossfade`.
- Clicking it switches to the other mode.
- Continue persisting the selected mode.
- Playlist Play Now and individual-track Play Now use the selected mode.
- Do not repeat the immediate-mode value in the session summary.
- The ordinary Play Now button does not need to repeat the selected mode in its
  own label.

Individual-track Play Now
=========================

Each track in the selected playlist must have:

- Play Now
- Queue

Individual-track Play Now must:

1. Start the exact selected track rather than choosing randomly.
2. Make that track's source playlist the active playlist.
3. Clear the pending playlist.
4. Preserve explicitly queued tracks.
5. Use the selected Hard Cut or Crossfade mode when replacing active music.
6. Use the automatic master fade-in when starting from a globally stopped state.
7. Return to ordinary active-playlist selection after the exact track ends,
   subject to the existing queue precedence rules.
8. Handle missing or unreadable files without crashing or leaving playback
   state stuck.
9. Avoid duplicate or stale completion events when it replaces another track.

The source playlist must be passed explicitly. Do not infer it by searching for
a matching filename because different playlists may contain tracks with the same
name.

Selected playlist naming
========================

The Selected Playlist heading must include its group path when the playlist
belongs to a group.

Use this convention:

  Group / Playlist

For nested groups, show the relevant path:

  Parent Group / Child Group / Playlist

For an ungrouped playlist, show only the playlist name.

Derive the display path from the scanned Music hierarchy. Do not include the
library root or the literal `Music` directory.

Use the same qualified playlist display where it prevents ambiguity in concise
current, active, pending, or queue information.

Main window layout
==================

Keep a single WPF window optimized for live use.

Rework the main content into three dedicated areas:

- Library
- Selected Playlist
- Ambience

The Library and Selected Playlist sections are currently wider than necessary.
Reduce their share of the window and use the recovered space for Ambience.

A reasonable starting proportion is approximately:

- Library: 20 percent
- Selected Playlist: 30 percent
- Ambience: 50 percent

The exact proportions may be adjusted to keep the window usable at its existing
minimum size. Ambience must still feel like a primary section rather than an
item below Session and Queue.

Requirements:

- Ambience receives a full-height dedicated panel in the main content area.
- Ambience has its own scrolling region.
- Scrolling Ambience must not hide transport, session, or queue information.
- Session and Queue must not share the Ambience scroll viewer.
- Keep the library-folder controls and primary status display accessible.
- Preserve clear playback-error and scan-error reporting.
- Remove duplicated scan-detail presentation if the primary status display
  already communicates the same information.
- Avoid increasing the minimum window size unless necessary.
- Verify the layout at the default window size and at the existing minimum size.

Library presentation
====================

Increase the size of displayed group and playlist names in the Library tree.

Requirements:

- Use at least a 16-pixel font for group and playlist entries.
- Keep group and playlist hierarchy visually clear.
- Keep row height and padding large enough for reliable clicking.
- Do not make the Library column wider merely to accommodate excessive padding.

Improve selection styling for both groups and playlists:

- Selected text must have strong contrast against its background.
- Do not use foreground and selection colors that are close in brightness or hue.
- Distinguish selection from hover and keyboard focus.
- Keep text readable when the window is unfocused.
- Preserve visible hierarchy and indentation.
- Use the existing dark visual language rather than replacing the application
  with an unrelated theme.

Session and transport
=====================

Remove the large, vertically listed Session panel.

Place a concise session summary in or near the persistent transport area. Keep
only information useful during live operation:

- Now playing track
- Current/source playlist
- Active playlist
- Pending playlist

Use compact horizontal or wrapped presentation as space allows.

Do not repeat these fields in the session summary:

- File
- Playback state
- Elapsed time
- Duration
- Immediate transition mode
- Master output fade gain
- Master fade state

Playback state and elapsed/duration already belong in Transport. Transition mode
is already shown by its toggle. Master fade direction is already shown by the
fade button.

Transport should contain:

- Pause/Resume
- Skip
- Stop All
- Playback state
- Elapsed/duration

Clear Queue must not remain in Transport.

Queue section
=============

Keep the explicit FIFO Queue as its own compact section.

Requirements:

- Put Clear Queue beside the queue heading or queue contents.
- Do not show Clear Queue among Pause, Skip, and Stop All.
- Disable Clear Queue when the queue is empty.
- Keep queued track names and source playlists readable.
- Give the queue its own bounded presentation or scrolling behavior.
- The queue must not displace or hide the dedicated Ambience panel.
- Preserve FIFO behavior and existing queue precedence.
- Individual-track Play Now and playlist Play Now do not clear the explicit
  queue.

Ambience section
================

Ambience must be a dedicated primary section with:

- A section heading.
- A search field.
- A compact independently scrolling sound list.
- One play/stop toggle per sound.
- One usable individual volume slider per sound.
- A compact state and percentage display where useful.

Ambience search
===============

Add a simple search field above the ambience list.

Requirements:

- Filter by displayed ambience name.
- Use case-insensitive substring matching.
- Update the displayed list as text changes.
- Clearing the field restores the complete list.
- Filtering changes presentation only.
- Hidden ambience sources continue playing.
- Clearing or changing the search must not restart, stop, or recreate sources.
- Do not rescan the library for each search change.
- Do not add advanced search syntax, tags, categories, or indexing.

Compact ambience rows
=====================

The current ambience cards consume too much vertical space and use sliders that
are longer than necessary.

Replace them with compact rows.

Each row should contain, in a compact arrangement:

- Ambience name
- Short playback state, if it remains useful
- Play/Stop toggle
- Individual volume slider
- Percentage value

Requirements:

- Avoid large card padding and multiple unnecessary lines.
- Keep each slider usable rather than reducing it to a tiny control.
- A fixed or bounded slider width around 120 to 180 pixels is reasonable.
- Truncate long names with a tooltip rather than expanding every row.
- Keep buttons large enough for reliable live operation.
- At the default window size, substantially more ambience entries must be
  visible than in the MVP.
- Aim to display approximately ten or more rows without scrolling when the
  normal status and transport areas are present.
- Do not sacrifice keyboard accessibility to achieve density.

Ambience play/stop toggle
=========================

Replace separate Play and Stop buttons with one state-aware button.

Behavior:

- Stopped: show `Play`.
- Fading Out: show `Play`; clicking reverses smoothly toward playing.
- Playing: show `Stop`.
- Fading In: show `Stop`; clicking reverses smoothly toward stopped.

Do not create overlapping copies of the same ambience source. Reversals must use
the existing source and begin from its current lifecycle gain.

Ambience source-volume fades
============================

Changing an individual ambience volume must fade to the new target rather than
jumping immediately.

Requirements:

- Use the configured Medium duration.
- Ramp from the source gain active at the time of the request.
- Scale duration by remaining gain distance.
- A new slider target replaces the previous in-progress source-gain ramp.
- Do not jump to the previous target before beginning the new ramp.
- Source-volume gain remains independent of the ambience play/stop lifecycle
  gain.
- For a stopped source, remember the target value and use it when the source is
  next played.
- Changing one ambience slider must not affect another source.
- Do not apply this behavior to the music, ambience-bus, or master-volume
  sliders. Those three higher-level controls remain immediate.

Volume slider interaction
=========================

Every volume slider must support clicking directly on the slider track.

This applies to:

- Individual ambience volume
- Music volume
- Ambience-bus volume
- Master volume

Requirements:

- Clicking at approximately 25 percent of the track sets approximately 25
  percent volume.
- Clicking at approximately 50 percent sets approximately 50 percent.
- Clicking must not collapse to only zero or one hundred percent.
- Preserve dragging.
- Preserve arrow, Page Up, Page Down, Home, and End keyboard behavior.
- Keep displayed percentages synchronized with the requested value.
- Use WPF's proportional track behavior or a correct coordinate-to-value
  calculation rather than treating every click as an endpoint.
- Individual ambience clicks request a Medium source-gain fade.
- Higher-level volume clicks apply immediately.

Concurrency and failure behavior
================================

Preserve the MVP's serialized playback-command behavior.

New operations must remain safe under rapid input:

- Repeated Play Now commands
- Repeated Stop All commands
- Fade reversals
- Ambience play/stop reversals
- Repeated ambience gain changes
- Crossfade commands issued during an existing crossfade
- Output failure during a delayed crossfade start
- Application shutdown during a fade

Requirements:

- A stale delayed crossfade must not start after a newer command.
- A stale fade callback must not dispose a replacement source.
- Stop All must prevent stale events from restarting playback.
- Application shutdown may stop immediately and does not need to wait through a
  user-facing fade.
- Errors must be visible and must not crash or hang the application.
- Do not block the UI thread while waiting for audio transitions.

Persistence
===========

Continue persisting:

- Selected library path
- Preferred immediate transition mode
- Music volume
- Ambience volume
- Master volume
- Fast fade duration
- Medium fade duration
- Slow fade duration
- Crossfade stagger

The per-source ambience slider values and selected Fast/Slow runtime toggle do
not need to persist unless doing so requires only a trivial change and introduces
no additional complexity.

Continue using a small versioned JSON file. Do not add another settings system or
database.

Documentation
=============

Update existing documentation only where behavior has changed.

README updates should include:

- Configurable timing settings and their file location.
- Default timing values.
- The single transition-mode toggle.
- The single master fade button and Fast/Slow toggle.
- Automatic fade-in from stopped.
- Fading Stop All.
- Individual-track Play Now.
- Ambience search and compact controls.

Update the manual test checklist to reflect the new behavior. Remove statements
that claim crossfade and ambience durations are fixed at two seconds.

Do not rewrite unrelated documentation.

Testing policy
==============

Testing is required, but it must remain deliberately focused.

The application is simple, readily available for manual testing, and has one
primary stakeholder. The user will manually evaluate subjective sound quality,
layout, control density, and live usability. Do not spend excessive time or
tokens pursuing exhaustive automated coverage.

Do not:

- Introduce a WPF UI automation framework.
- Add tests for every label, color, margin, or XAML placement.
- Chase line-coverage targets.
- Rewrite the existing test suite.
- Create an elaborate timing simulator solely for this refinement.
- Repeatedly run the complete suite after minor XAML changes.
- Repeat multi-hour playback testing.
- Repeat clean-machine, offline, hardware-device, installer, or publish testing
  unless a change directly affects those systems.
- Produce a new release artifact unless the user requests one.
- Delegate redundant testing or review work to multiple agents.

Add or update targeted tests for behavior that is easy to regress:

- Loading timing defaults.
- Loading valid custom timing values.
- Handling invalid timing values.
- Preserving timing values when saving other settings.
- Staggering an incoming crossfade without silently advancing it.
- Cancelling a stale delayed crossfade.
- Fading an active ambience source toward a new gain target.
- Replacing an in-progress ambience gain target.
- Individual-track Play Now changing the active playlist.
- Individual-track Play Now clearing pending and preserving the queue.
- Stop All fading before stopping and leaving master fade gain at zero.
- Starting from stopped automatically fading in.
- Pause/Resume not causing an automatic fade-in.
- Fade direction reversal from the current gain.

Use manual smoke testing for:

- Layout proportions.
- Library text size.
- Group and playlist selection contrast.
- Ambience search.
- Compact ambience density.
- Play/Stop toggle labels.
- Fade button labels.
- Transition toggle labels.
- Clear Queue placement.
- Track Play Now buttons.
- Proportional slider clicking.
- Subjective crossfade overlap.
- Subjective ambience gain smoothness.

Verification commands
=====================

Use targeted test filters during implementation where useful.

At final verification, run the complete build and suite once:

```powershell
dotnet build Soundrel.slnx --configuration Release
dotnet test Soundrel.slnx --configuration Release --no-build
```

A self-contained publish, clean-machine test, extended playback session, or
hardware-device test is not required for this refinement unless the relevant
code or project configuration was changed in a way that creates a specific
release risk.

If a full test fails for a reason unrelated to this work, investigate enough to
identify and report it. Do not expand the scope into unrelated repairs without
user approval.

Required planning and execution
===============================

Do not modify the repository immediately.

You are responsible for planning, coordinating, implementing, and reviewing the
work, but you must stop at every milestone and receive explicit user approval
before continuing.

General workflow
================

1. Inspect the repository, current code, tests, documentation, and user changes.
2. Compare the current MVP behavior with this document.
3. Present a concise implementation plan as Milestone 0.
4. Identify any genuine blocker or requirement conflict.
5. Wait for explicit user approval before editing files.
6. For each approved milestone:
   - Create a focused task list.
   - Delegate only clearly isolated work where delegation saves time.
   - Do not assign multiple agents to overlapping files.
   - Give workers exact behavior, files, constraints, acceptance criteria, and
     focused verification commands.
   - Inspect all returned changes directly.
   - Review the diff for scope and correctness.
   - Run only the milestone's focused checks.
   - Resolve relevant failures before presenting the milestone.
7. At the end of each milestone, stop all further work and report:
   - What was implemented
   - Important files changed
   - Tests and builds run
   - Results
   - Short manual review instructions
   - Known limitations
   - Decisions needed before continuing
8. Do not begin the next milestone until the user explicitly approves it.
9. If the user requests changes, keep them within the current milestone.
10. Present the revised milestone again and wait for approval.
11. Approval must be explicit. Silence or unrelated feedback is not approval.
12. Ensure no worker remains active and no future-milestone work has begun when
    pausing.
13. Do not commit or push unless explicitly requested.
14. Do not use excessive worker agents, reviews, or tests for this small
    application.

Milestones
==========

Milestone 0: Repository review and plan approval
------------------------------------------------

- Inspect the current application and tests.
- Identify the existing settings version and migration path.
- Identify the current master, crossfade, ambience lifecycle, and ambience
  source-gain stages.
- Identify the current playback-command serialization mechanism.
- Identify the current layout and bindings that will be retained or replaced.
- Present a concise implementation plan organized around Milestones 1 through 4.
- Call out any requirement that cannot be implemented cleanly within the
  existing architecture.
- Do not modify files.
- Wait for explicit user approval.

Milestone 1: Timing configuration and audio transitions
-------------------------------------------------------

Implement the underlying timing and sample-pipeline behavior:

- Add Fast, Medium, Slow, and crossfade-stagger settings.
- Add settings migration and validation.
- Preserve timing settings during later saves.
- Remove hard-coded crossfade and ambience durations.
- Use Medium for crossfades and ambience lifecycle fades.
- Add the sample-counted crossfade stagger.
- Ensure the incoming source does not advance during its delay.
- Add smooth Medium-duration ambience source-gain ramps.
- Support replacing and reversing relevant in-progress ramps.
- Protect delayed starts and completion callbacks from stale transitions.
- Add only focused settings and audio-engine tests.
- Run the relevant targeted tests and a Release build.
- Present results and wait for explicit user approval.

Do not redesign the window during this milestone.

Milestone 2: Playback workflows and view-model behavior
-------------------------------------------------------

Implement application-level behavior:

- Add exact individual-track Play Now.
- Make the source playlist active.
- Clear pending playlist while preserving the explicit queue.
- Use the selected immediate transition mode.
- Replace separate immediate-mode actions with toggle behavior.
- Replace four master-fade actions with one state-aware direction action and one
  Fast/Slow selection.
- Implement smooth master-fade reversal.
- Make Stop All fade all output before stopping.
- Leave master fade gain at zero after Stop All.
- Automatically fade in when starting from a globally stopped state.
- Ensure Resume does not trigger the stopped-state fade.
- Add qualified `Group / Playlist` display data.
- Add ambience filtering state and toggle labels needed by the new UI.
- Add or update focused controller and view-model tests.
- Run the relevant targeted tests and a Release build.
- Present results and wait for explicit user approval.

Do not perform the full visual redesign before this milestone is approved.

Milestone 3: Main-window redesign and control polish
----------------------------------------------------

Implement the WPF presentation:

- Create dedicated Library, Selected Playlist, and Ambience sections.
- Narrow Library and Selected Playlist and give Ambience the largest share.
- Give Ambience its own search and scrolling region.
- Replace ambience cards with compact rows.
- Replace separate ambience Play and Stop buttons with one toggle.
- Keep individual ambience sliders usable but bounded in width.
- Increase Library group and playlist text size.
- Improve selected group and playlist colors and contrast.
- Show qualified selected-playlist names.
- Add Play Now beside Queue for individual tracks.
- Replace Hard Cut and Crossfade buttons with one toggle.
- Replace four master-fade buttons with the compact direction and speed controls.
- Move concise session information near Transport.
- Keep state and elapsed/duration in Transport.
- Move Clear Queue into the Queue section.
- Remove redundant session fields.
- Make every volume slider support proportional track clicking.
- Preserve visible error reporting.
- Verify default-size and minimum-size layouts.
- Run a Release build and perform a short manual smoke test.
- Do not add automated UI infrastructure.
- Present screenshots or clear manual review instructions if useful.
- Wait for explicit user approval.

Milestone 4: Focused final verification and documentation
---------------------------------------------------------

- Apply any user-requested polish from Milestone 3.
- Update README behavior and configuration documentation.
- Update the manual test checklist.
- Run the complete Release build once.
- Run the complete automated test suite once.
- Perform a short local smoke test covering the changed workflows.
- Do not perform extended, clean-machine, hardware, offline, or publish testing
  unless a specific change made it necessary.
- Report any remaining subjective checks for the user to perform.
- Present the final result and wait for explicit user approval.

Worker-agent requirements
=========================

When workers are useful, each worker must receive:

- The exact milestone and isolated task
- Relevant files
- Required behavior
- Explicit exclusions
- Acceptance criteria
- Focused build or test commands
- Instructions to avoid unrelated changes
- Instructions to report changed files, checks, and risks

After a worker returns:

- Inspect the repository rather than relying only on its summary.
- Review the affected code and diff.
- Confirm that behavior matches this document.
- Run only the relevant focused checks.
- Correct incomplete or over-engineered work.
- Do not delegate a second broad review merely to duplicate the first.

Final acceptance
================

Completion requires:

- Configurable Fast, Medium, Slow, and crossfade-stagger settings.
- Staggered Medium-duration crossfades.
- Medium-duration ambience lifecycle and source-volume fades.
- Compact master fade and transition controls.
- Fading Stop All.
- Automatic fade-in from stopped, but not from pause.
- Individual-track Play Now with correct active-playlist behavior.
- A dedicated searchable and compact Ambience section.
- A narrower Library and Selected Playlist layout.
- Qualified grouped playlist names.
- Improved selection contrast.
- Correct proportional slider clicking.
- Clear Queue located in the Queue section.
- Concise session information near Transport.
- Relevant focused tests passing.