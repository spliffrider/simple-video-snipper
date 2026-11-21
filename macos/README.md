# Simple Video Snipper for macOS

Simple Video Snipper is a menu bar recorder for macOS 15 and later. It records a selected area, app window, or Chrome tab. Use **Control–Option–Shift–R** to select an area and **Stop Recording** in the floating controls or menu bar to finish.

## Build and run

From the repository root, use Xcode 16 or later or the matching Command Line Tools with a macOS 15 SDK and Swift 6.

```sh
bash macos/build-app.sh
open "$HOME/Applications/Simple Video Snipper.app"
```

The build runs in `~/Library/Caches/SimpleVideoSnipper/build` and creates an ad-hoc signed app. Set `SNIPPER_BUILD_DIR` or `SNIPPER_APP_DIR` to override those locations. Use an APFS build and app directory when the checkout is on exFAT.

On first launch, confirm the temporary recordings folder. Allow Simple Video Snipper in **System Settings → Privacy & Security → Screen & System Audio Recording** before the first native capture. macOS may ask you to quit and reopen the app. The microphone prompt appears when you enable the mic.

- **Record Area** opens a selection on the display under the pointer. Drag the bottom-right handle to adjust the selection, then choose **Continue**.
- **Record Window** lists on-screen app windows. The saved video keeps its initial frame size when the source resizes. A window that is no longer on screen ends the recording.
- **Record Chrome Tab** opens the bundled recorder in Google Chrome. Use Chrome’s picker to select the source and start from the recorder tab. Stop from the browser or menu bar.
- **Settings** controls the save mode, format, export folder, and optional copying to the Finder clipboard.

Under **Settings → Recording**, turn on **Start area recording on release** to skip the selection and audio confirmation steps for area captures. Drag an area at least 32 by 32 pixels and release to start. Press **Escape** to cancel the selection before recording starts. Auto-start is off by default.

Set defaults for system audio, microphone, and microphone input in the same section. Manual recording lets you override these settings for a single capture. System audio captures the Mac’s output mix. When the selected microphone is unavailable, Snipper shows a notice and starts with the remaining sources.

Under **Settings → Snip Shortcut**, choose Mac modifiers and a key, then click **Save Shortcut**. **Use Default** selects **Control–Option–Shift–R**. The **Record Area** menu item shows the configured shortcut. If macOS rejects a change or the preference cannot be written, the active shortcut stays registered.

## Edit and save

After Stop, choose **Save**, **Edit**, **Keep for later**, or **Discard…**. **Save** uses your Ask every time or Quick Save settings. **Keep for later** leaves the original recording in **Unsaved Recordings**. **Discard…** asks you to confirm before removing the recording.

In **Edit**, drag the timeline handles to trim and click the track to scrub. The highlighted region is the part that plays and exports. Use **Set start** or **Set end** to trim at the playhead, or **Reset trim** to restore the full clip. Pause and drag on the frame to set a crop. **Add music…** starts a local audio file at the start of the edited clip. Shorter music leaves silence after it ends while the video continues. **Mute original audio** lets you use only the music or export a silent clip.

Press **Space** or click **Play** to play or pause the current edits in the editor. Click the timeline to seek and use **Back 5s** or **Forward 5s** to skip within the kept range. Moving a trim handle pauses playback at that handle. Preview volume only affects playback in the editor. **Save** uses your current edits and preferred export settings. If a save fails, **Retry** and **Save As…** reuse the edits, and **Keep for later** keeps the original recording.

Choose **Export GIF…** in the editor to preview an animated GIF of the trimmed and cropped clip. **Small** uses up to 480 pixels on the longest side at 10 fps, and **Clear** uses up to 720 pixels at 12 fps. The preview shows the encoded dimensions and file size. You can play or pause, switch between **Fit** and **Actual size**, and hold the last frame for one second. Add an optional multiline caption and place it at the top, center, or bottom of the GIF. The editor keeps your caption when you go back to adjust the crop or trim. **Save GIF…** saves the previewed GIF and keeps the original recording available for MP4 or WebM export.

Editing and GIF export use FFmpeg and FFprobe. WebM exports and Chrome tab exports use FFmpeg. The app checks Homebrew’s Apple silicon and Intel locations. An unedited native MP4 can be saved directly.

```sh
brew install ffmpeg
```

## Verify a build

The following commands create local recordings of app-owned fixtures. Use new output paths. They require a desktop session and screen recording access.

```sh
snipper="$HOME/Applications/Simple Video Snipper.app/Contents/MacOS/SimpleVideoSnipper"
check_dir="$(mktemp -d)"
"$snipper" --verify-crop "$check_dir/crop.mp4"
"$snipper" --verify-window "$check_dir/window.mp4"
```

The area check tests reverse-drag geometry at 1× and 2× and decodes the actual display crop at the current scale. It checks the four colors while an excluded Snipper window overlaps the grid. The window check resizes and closes a capture source, then checks the decoded dimensions and letterboxing. Use `--verify-system-audio` with a fresh MP4 path while playing a tone in another app to exercise system audio.

For the loopback handoff, run `"$snipper" --verify-companion "$check_dir/chrome"` and open the printed URL in Chrome. Choose a test tab. The command exits when the browser stops and the WebM is staged. Use `--verify-companion-stop` to send Stop from the desktop after recording starts.

## Distribution

Use `SNIPPER_SIGN_IDENTITY` with a Developer ID Application identity to build with the hardened runtime. For direct distribution, submit the signed app archive through Apple’s notarization service and staple the ticket before publishing. [Apple notarization guide](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)
