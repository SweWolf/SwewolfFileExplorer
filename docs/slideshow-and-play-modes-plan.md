# Slideshow and play modes: plan (item 1 of `teratool-features.md`)

Drafted together with the user on 2026-10-20. **Phase 1 (pictures) is built (see the end of this file); the music and video phases are not.** The five decisions are made (see "Decisions" at the end); two small questions about the Play bar are still open.

## What TeraTool did

From its specification (§5.3 to §5.5, `G:\My Drive\TeraTool_spec\TeraTool_Specification.md`) and a screenshot of the old window (a box under the file list):

- **Play mode:** Slideshow / continuous, Single Play, Repeat, Demo Play (start each file at a set time, mm:ss) and Intro Play (always start at 0,
  ignoring the saved resume point).
- **Time per file (s):** how long a picture stays in a slideshow.
- **End of list action:** Go to next folder / Start from beginning / Close. A checkbox with "Time-out (s)" asks first and then does it by itself after
  the countdown.
- **Play / Pause** buttons. ("Save Playlist As..." belongs to item 3, `.m3u`.)

It was meant mostly for pictures and video, but it also played music files and other files.

## What the new explorer and player already have

- **Picture viewer:** the end-of-list choice with a countdown (`ChoiceDialog`, made for a slideshow) and `NextFolderFinder` (next folder with pictures, in
  tree order, same drive). Pictures are shown in the explorer's own viewer, in the same process.
- **SweWolf Media Player** (`D:\Dev\SwewolfMediaPlayer`, WinForms, LibVLCSharp): started once per file with a path and `--library-path`
  (`MediaPlayerLauncher`). It knows when a file ends (`EndReached`, `LibraryOnEnded`) but cannot tell the explorer, and nothing tells the explorer when it
  closes. It has no single-instance logic: every click on a music file starts another window.
- The library (`LibraryStore`) is shared and keeps read marks and resume positions.

## The two music cases the user named

1. **Clicking another file while one plays** must not open another player window.
2. **Continuous playing of a folder** (the next file starts when one ends) needs the player to say that it is finished.

Both need a **named pipe** (`System.IO.Pipes`; both programs are .NET): the explorer finds the running player through it and sends it commands, and the
player sends events back. A mutex or the pipe name tells whether a player is already running; if there is no pipe the explorer starts the player as it
does today (so an older player without a pipe keeps working).

### Who owns the playlist

- **Hybrid (recommended):** the explorer sends the whole ordered list and the mode once. The player goes on to the next track itself (no gap; it keeps
  playing if the explorer is closed). The player tells the explorer "now playing X" (the list selection and "File 14/1001" follow) and, at the end
  of the list, asks the explorer what to do; the explorer does "next folder" with its own code (`NextFolderFinder` needs a "what kind of file" parameter:
  today it only looks for pictures) and sends the new list.
- **Explorer decides everything:** simpler, but the music stops when the explorer is closed, and each track costs a round trip.

### The pipe

- **Messages:** one JSON line each way, with a protocol version. Explorer to player: `play` (path, library path, the list with the start index, mode,
  seconds, start offset, "activate window" flag), `queue`, `stop`. Player to explorer: `nowPlaying`, `listEnded`, `closed`.
- **Safety:** the pipe is limited to the current user (`PipeOptions.CurrentUserOnly`). The player accepts only play, queue and stop, checks that every path exists
  and is a media file, and never runs anything it is sent. A message that cannot be read is ignored. Timeouts on every connect, so a stuck or missing player
  never freezes the explorer.
- **Shared code:** the small pipe class is copied between the two repos like `LibraryStore.cs` (History list in the header, namespace changes), and the message
  format is documented once here or in `docs/`.

## Where the controls could go (open)

The old tool had a box under the list. Options: a **Play bar** shown or hidden by a toolbar button, like the Filter bar (Play/Pause, mode, seconds,
end-of-list action, "ask first" with its time-out); the empty left column of the info panel; or only a small settings window plus a menu.

## Phases (each can be tested and committed on its own)

1. **Picture slideshow, all inside the explorer:** Play/Pause, seconds per picture, the existing end-of-list choice with its countdown. Small and low risk.
2. **One player window:** the pipe, "click another track reuses the window", the player's `closed` / `ended` events.
3. **Continuous playing of a folder:** the hybrid playlist, "now playing" sync with the list, "next folder" for music and video.
4. **Modes:** Repeat, Demo Play (start offset), Intro Play (ignore the resume point). They are parameters of the play command.
5. **Later, if wanted:** mixed folders (pictures and videos in one slideshow: the explorer would hand over between its viewer and the player) and `.m3u`
   playlists (item 3).

## Decisions (the user, 2026-10-20)

1. **Controls: a Play bar like the Filter bar**, shown and hidden by a toolbar button, **below the list** (under the "File 35/100" info line, not above the
   grid). Like TeraTool's box (Play mode, Time per file (s), End of list action, the "ask first" checkbox with its Time-out (s)), with one change:
   **Play and Pause are one toggle button**, plus **Previous** and **Next**.
2. **The explorer decides everything** (as in TeraTool, where it was all one program): the player only says "ended" / "closed" and the explorer picks the next
   item and sends it. The hybrid (player owns the list) stays a possible later change. Accepted drawback: the music stops when the explorer is closed.
3. **A new track brings the player to the front** (video must be in front; a setting for it may come later).
4. **A mix is allowed**: one playlist may hold pictures, music and video. Pictures go to the explorer's own viewer for N seconds, music and video to the
   player until they end, so the explorer hands over between the two (this is why decision 2 fits).
5. **Start with phase 1 (pictures)**, look at it, and continue later.

## Phase 1 built (2026-10-20)

- **The Play bar:** a "Play" toggle in the toolbar (and Ctrl+P) shows it, under the "File 35/100" line and above the preview picture; hidden at the start.
  Previous / Play / Next, Play Mode (Slide Show / Single Play), Time per File (s), End of List Action, Ask First, Time-out (s); TeraTool's defaults.
- **Play** plays the pictures of the list from the selected one in the picture viewer; **the Pause key (and P while a slide show runs) pauses and resumes**;
  at the end of the list the chosen action is taken (with the question and its countdown when Ask First is on).
- Answers to the two open questions: the bar sits directly under the info line; the viewer pauses with the Pause key and P.
- **Next:** phase 2 (one player window through the named pipe; the explorer decides everything), then phases 3 and 4.
