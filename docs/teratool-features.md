# TeraTool features: what is done and what is left

The list comes from the specification of the predecessor, TeraTool (`G:\My Drive\TeraTool_spec\TeraTool_Specification.md`), compared with
what SweWolf File Explorer does (made 2026-10-19). `[x]` = built, `[ ]` = not built. The numbers are the ones used when the list was made, so
"item 8" means the same thing in a conversation. The "?" marks were not checked in the code at the time.

## Viewing and playing

- [x] **1. Slideshow and play modes.** Done (2026-10-21): the Play bar with slide show of pictures, music and video through SweWolf Media Player, Repeat, Demo Play and Intro Play. Slideshow, Single, Repeat, Demo Play and Intro Play, with seconds per picture. The countdown and the
  end-of-list choice of the picture viewer are already there and were made for this. A plan (phases, the named pipe to the media player, open decisions) is in `slideshow-and-play-modes-plan.md`.
- [x] **2. Bookmarks.** Done 2026-10-20 as `.lnk` shortcuts in a Bookmarks node at the top of the tree (Ctrl+D). A start point inside a video or music file is done too (2026-10-21): the player's Add Bookmark button, the time in the shortcut's Comment. Not done: a Bookmarks menu in the menu bar.
- [x] **3. Playlists and link collections.** Done 2026-10-21 for playlists: `.m3u` / `.m3u8` shown as folders in the tree, played by the Play bar, and created, edited, saved and renamed in the program. Not done: web links (streams) and link collections.
- [x] **4. Picture viewer extras.** Done (the user, 2026-10-22: "the picture viewer is really great"). The right-click menu is there; the folder name is shown when the viewer goes into another folder (by hand or in a slide show: `GoToFolder`); the mouse wheel goes to the next picture and Ctrl + wheel zooms, which the user wants to stay as it is. Maybe later: a setting that turns the wheel direction round (the user has seen it done the other way).
- [ ] **5. Text editor and hex editor.** Today `.nfo` and text files open in VS Code or Notepad.

## File operations

- [x] **6. Smart rename.** The Rename box (F2): the extension in a box of its own, Upper / Lower / Proper Case, "Use Smart Name"
  (`Artist - Title` for music; `Title (Year)` and `Title - s01e02` for video), the old name shown above. Not done: smart rename of several files at
  once, and the `/r "file"` command line (item 21).
- [ ] **7a. Move to Folder (the M key).** Pick a folder and move the selected files there.
- [x] **7b. Offer to move a video after a smart rename.** Setting "Auto-Suggest File Move When Renaming Video Files" (off by default): a
  movie goes to `Videos\Movies`, an episode to `Videos\TV Shows\<Show>`; the folder can be changed.
- [ ] **8. Add enumeration.** Number the selected files 01, 02, 03 and so on.
- [ ] **9. Delete folders that contain no files** (recursively).
- [ ] **10. Delete small images.** Remove the pictures below a size in pixels.
- [ ] **11. Set folder date.** Set the modified date of a folder by hand.
- [ ] **12. Extract with 7-Zip.** An explicit "Extract Here". Today a file is only unpacked to the temp folder when it is opened or copied.
- [x] **13. Open in Windows Explorer.** In the right-click menus of the tree and the list; a selected file is shown selected in its folder.

## List behavior

- [ ] **14. Sort options.** Shuffle, a natural (number-aware) name sort ?, a "lock sort" toggle, and manual row order with Move Up / Down.
- [ ] **15. Position and Info columns.** Percent watched; the library already has what it needs.
- [ ] **16. Hide instead of delete.** A per-file "hide" flag.
- [x] **17. Set Tags (write).** "Set Tags..." in the tree's menu and a double-click on the Tags line; written to the folder's `desktop.ini`, as TeraTool did. Not done: search by tag.
- [x] **18. Auto-refresh.** Done with a file system watcher instead of a timer (2026-10-20): the list of the active tab follows changes made by other programs, in place. `AppSettings.WatchFolders` (settings.json) turns it off; no settings window yet.
- [ ] **19. Go to time / Clear resume point.** Belongs in the media player; not known whether the explorer needs a menu item.

## Covers and miscellaneous

- [ ] **20. Cycle through cover candidates, or download covers automatically.**
- [ ] **21. `/r "file"` command line.** A rename-only start, used from Send To.
- [ ] **22. Mouse wheel behavior option.** Scroll, zoom or navigate.
- [ ] **23. A file or folder on the command line for other file types** than pictures (open the player or the default program).

## Left out on purpose

Flash, VLC, RealPlayer and the web browser wrapper, the downloader and its list editor, the PDF "send keys" trick, the screen-capture class, and
the INI settings file (settings live in `settings.json`).

## Also built (not in TeraTool)

Tabs, workspaces and sessions (Save Workspace suggests a name), the tree root, "More Options" (the real Windows menu), copy and move with a
progress window and Cancel, type-ahead in the file list, the Size column right-aligned, the info panel with its own info line and two drag
lines, Open in Terminal / PowerShell / Command Prompt (right-click for the administrator), reading a folder clicked in the tree in the background,
hidden extensions and folders, RAR / 7z / zip browsing, previews of video, music and RAW pictures, plots from `.nfo` files, and the library
database that also serves SweWolf Media Player. See `CLAUDE.md` for the details of each.
