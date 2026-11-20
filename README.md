# Simple Video Snipper v1 build plan

Simple Video Snipper is a Windows x64 tray app for recording short clips. Press a global shortcut, select an area, window, or Chrome tab, and start recording. After Stop, save the clip to a chosen folder or use Quick Save. An optional setting copies the finished file to the Windows clipboard. Recordings stay on the device.

The [macOS build plan](docs/macos-build-plan.md) covers the native Mac implementation and the shared Chrome recorder.

## Install on Windows

Run the Windows x64 setup on Windows 10 22H2 or Windows 11. On first install, confirm the default temporary recordings folder or browse to another local folder. Setup tests that it can write and delete a file there. The installer uses your Windows account and includes the app runtime and FFmpeg. Chrome is needed for Chrome tab recording.

Change the temporary location in **Settings → Temporary recordings**. New recordings go to the new folder, and **Unsaved recordings** still lists clips in earlier folders. To upgrade, exit Simple Video Snipper from the tray and run the new setup. Upgrade and uninstall preserve settings and recordings.

To build setup from source, install the .NET 10 SDK and Inno Setup 6.7 or newer. Run `.\installer\Build-Installer.ps1` from the repository root. The script publishes a self-contained Windows x64 app, verifies the pinned FFmpeg archive, and writes setup to `.tools\installer\dist`. The build produces an unsigned installer. [Inno Setup downloads](https://jrsoftware.org/inno-setup/downloads) · [.NET self-contained deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/)

For repeatable clean-install and upgrade tests, build two versions with the same unused `-Profile SimpleVideoSnipper.SetupCheck` value, such as `0.1.0` and `0.1.1`. Pass those executables to `installer/Test-Installer.ps1` with the profile and upgrade version. The tests use a separate installation identity and preserve your regular app data.

## Try area or window recording

Run `dotnet run -c Release` with the .NET 10 SDK.

1. For an area, move the pointer to the display you want and press **Ctrl+Alt+R**, or choose **Record area**. Draw a rectangle, adjust it with its lower-right handle, and choose **Continue**. For a window, choose **Record window**, select an app window, and choose **Continue**.
2. Turn on **Microphone**, **System audio**, or both before choosing **Record**. Both start off. Select a mic and check its level. For system audio, choose the output the sound is playing through. The list initially selects the Windows default.
3. Use the floating **Stop recording** button or the tray menu. The shortcut brings the recording controls forward. If the selected window resizes, the video keeps its original dimensions and fits the new window inside the frame.
4. Finish in the desktop save flow. Recordings with audio contain one mix.

Change the global area shortcut under **Settings → Snip shortcut**. Choose modifiers and a key, then save. **Use default** selects **Ctrl+Alt+R**. If Windows rejects a combination, the current shortcut stays active. [Hotkey registration](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)

## Try the Chrome recording prototype

Install the .NET 10 SDK and Chrome, then run `dotnet run -c Release` on Windows.

1. Click **Open Chrome recorder** in the desktop window or tray menu.
2. Select **Include tab audio**, **Include microphone**, or both. Click **Choose tab** and select the Chrome tab you want to record. Enable tab audio in Chrome’s sharing dialog if needed. Chrome prompts for the microphone if you selected it.
3. Click **Record**. Keep the recorder page open and use **Stop recording** on the page, in the desktop window, or in the tray.
4. Finish in the desktop save flow. Once Chrome says recording finished, the app holds the recording locally and you can close the companion tab.

## Save and share recordings

After Stop, choose **Save**, **Edit**, or **Keep for later**. In **Edit**, drag the two timeline handles to trim. Click the track to scrub, or use **Set start** and **Set end** to trim at the playhead. Pause and drag on the preview to crop. **Add music** mixes a local audio file from the start of the edited clip. Adjust its volume or choose **Mute original audio** for music alone. Press **Space** or click **Play** to play or pause the edited clip in the editor. **Back 5s** and **Forward 5s** move the playhead within the kept range. Use **Save** to export the edits.

Choose **Export GIF…** to preview a silent loop of the trimmed and cropped clip. Use **Small** for up to 480 pixels and 10 fps, or **Clear** for up to 720 pixels and 12 fps. Both presets limit the longest side. Switch between **Fit** and **Actual size**, pause playback, or hold the final frame for one second. **Save GIF…** saves the preview as a separate file and keeps the original recording available in the editor.

Open **Settings** in the app or tray to set the preferred folder, format, and clipboard behavior. **Ask every time** opens the Windows Save As dialog when you choose to save. **Quick Save** uses your preferences and adds a numeric suffix if the filename is taken. The defaults are Ask every time, MP4, `Videos\Simple Video Snipper`, and auto-copy off.

Both native and Chrome recordings can be exported as MP4 or WebM. The installer bundles FFmpeg and FFprobe. It uses Windows Media Foundation for H.264. When running from source, put an [FFmpeg build](https://ffmpeg.org/download.html) with `libx264`, `libvpx-vp9`, `libopus`, and AAC on PATH or in `.tools/ffmpeg.exe` before building. Editing also needs `ffprobe.exe` on PATH or in `.tools`. A same-format save without edits preserves the existing encoding. WebM is remuxed when FFmpeg is available so playback can seek by time.

If saving fails, choose **Retry**, **Save As**, or **Keep for later**. To resume after closing the dialog or restarting, open **Unsaved recordings** in the tray. Completed recordings wait in a subfolder of the temporary recordings location. Successful saves offer **Open**, **Show in folder**, and **Copy video file**. The clipboard receives the saved file so it can be pasted into apps that accept files. Earlier prototype clips remain in `%LOCALAPPDATA%\SimpleVideoSnipper\Recordings`.

## Capture flow and defaults

1. Open the capture toolbar from the tray or with `Ctrl+Alt+R`. The shortcut supports Windows-key combinations that register successfully and keeps the previous binding if a new one conflicts. `Win+Shift+R` already opens Windows Snipping Tool. [Hotkey registration](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) · [Snipping Tool shortcuts](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots)
2. Start in **Area** mode. Drag an adjustable rectangle within one monitor, select an open application in **Window**, or confirm a source in Chrome’s picker for **Browser tab**. Show the selected source and a **Record** button. Esc cancels selection. A browser-window capture follows the app window, including its controls and whichever tab is visible. A tab capture stays with the selected page as the user switches tabs.
3. Offer **Microphone** and **System audio** for area and window, and **Microphone** and **Tab audio** for Chrome. Audio defaults to off. Show a microphone selector and level meter when enabled. If the mic disconnects, continue with the remaining streams and report the loss. System audio includes other sounds on the selected Windows output device. [WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
4. Show a recording indicator, elapsed time, and **Stop**, with a second Stop action in the tray. Pressing the shortcut during capture brings the controls forward. Keep snipper controls out of the recording. If the source closes, end the session and offer to save the captured clip. Show **Finishing** while writing the file. [Windows capture exclusion](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
5. Default to **Ask every time** with filename, folder, and format prefilled. **Quick Save** uses a unique filename, the preferred format, and the preferred folder, initially `Videos\Simple Video Snipper`. Offer MP4 (H.264 and AAC, when audio is enabled) and WebM (VP9 and Opus). Default to MP4. On success, offer **Open**, **Show in folder**, and **Copy video file**. Auto-copy is off by default and copies the saved file using the Windows file clipboard format. Show **Copied** after clipboard success and provide a retry if the clipboard is busy. [Windows file clipboard format](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard)

## Temporary recordings setup

On first install, the **Temporary recordings folder** page prefills `%LOCALAPPDATA%\SimpleVideoSnipper\Recordings`. Confirm it or choose another local folder. Setup creates a subfolder for pending clips and tests it with the installing user’s permissions. If the test fails, setup shows an error and lets you choose another folder. Upgrades reuse the chosen path. [Inno Setup directory page](https://jrsoftware.org/inno-setup/documentation/topic_isxfunc_createinputdirpage.htm) · [Per-user install mode](https://jrsoftware.org/inno-setup/documentation/topic_setup_privilegesrequired.htm)

Check access and free space before Record. Write each session to an app-owned temporary file as it records. Export via a partial file in the destination folder and publish the final filename only after the write succeeds. If export fails, retain the staged clip and offer **Retry**, **Save As**, or **Discard**. Reopen finalized unsaved clips on the next launch. Remove only the app’s temporary clip after successful export or explicit discard. A storage setting change applies to new captures and preserves access to pending clips from the old folder.

## Implementation milestones

| Milestone | Work and acceptance check |
| --- | --- |
| 1. Prove the Chrome handoff | Build a local companion page served on loopback by the desktop app. A user click invokes `getDisplayMedia`; request the tab picker, verify the selected surface, and disable switching the capture source. Write `MediaRecorder` chunks in order to disk through a session-scoped local connection. Test recording a Chrome tab with its audio, switching tabs, and stopping from both the tray and the browser. The desktop app receives a playable clip without an upload or a manual download. [Chrome source controls](https://developer.chrome.com/docs/web-platform/screen-sharing-controls) · [MediaRecorder chunks](https://www.w3.org/TR/mediastream-recording/) |
| 2. Ship an internal area-to-MP4 build | Extend the .NET 10 tray app with a global shortcut and a per-monitor DPI-aware selection overlay. Use physical pixels for the rectangle and crop Windows Graphics Capture frames before encoding at 30 fps. Test a visible pixel grid at two Windows display scales and confirm that the rectangle is accurate, the app controls stay out of the clip, and the result plays as MP4. [WinForms DPI settings](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop#applicationhighdpimode) · [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) |
| 3. Add window capture and audio | Add open-window selection to the native capture path. Keep output dimensions fixed and fit a resized window inside the frame. Capture mic and system sound through WASAPI in native modes. In tab mode, mix the selected tab audio and optional mic in the companion page. Timestamp audio and video on a shared timeline and test each audio source alone, both together, and a mic disconnect during a multi-minute capture. |
| 4. Complete the shared export and settings flow | Route all three capture modes through the same staging, export, and clipboard code. Prototype FFmpeg as the shared exporter with MP4 and WebM presets. Reuse already compatible streams where possible. Verify both formats from each mode, Save As and Quick Save, paste into a target that accepts files, and run a retry after making the destination unwritable. Verify that a failed export leaves the temporary clip intact. [FFmpeg codec documentation](https://ffmpeg.org/ffmpeg-codecs.html) |
| 5. Package and test the installer | Build a per-user Inno Setup installer. On a clean Windows 11 account, test the default and a custom temporary folder, an unwritable path, changing the folder in Settings, and an upgrade. Install on a machine without development tools and record, save, and copy a clip from every capture mode. |
