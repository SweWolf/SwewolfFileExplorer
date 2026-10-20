# Slideshow and play modes: plan (item 1 of `teratool-features.md`)

Drafted together with the user on 2026-10-20. **Nothing is built yet.** The five decisions at the end are open: the user is thinking them over.

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

## Decisions still open (the user is thinking about them)

1. Where do the controls live (a Play bar like the Filter bar, the info panel, or a menu and a settings window)?
2. Hybrid playlist, or the explorer decides everything?
3. Does a click on a new music track keep the player in the background, or bring it to the front (for video, probably the front)?
4. What is played: the list as shown (sorted and filtered), only files of the same kind as the one started on (pictures, music or video)?
5. Start with phase 1 (pictures) now?
