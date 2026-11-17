# Simple Video Snipper v1 build plan

Simple Video Snipper is a Windows 11 x64 tray app for recording short clips. Press a global shortcut, select an area, window, or Chrome tab, and start recording. After Stop, save the clip to a chosen folder or use Quick Save. An optional setting copies the finished file to the Windows clipboard. Recordings stay on the device.

The [macOS build plan](docs/macos-build-plan.md) covers the native Mac implementation and the shared Chrome recorder.

## Try area or window recording

Run `dotnet run -c Release` with the .NET 10 SDK on Windows 11.

1. For an area, move the pointer to the display you want and press **Ctrl+Alt+R**, or choose **Record area**. Draw a rectangle, adjust it with its lower-right handle, and choose **Continue**. For a window, choose **Record window**, select an app window, and choose **Continue**.
2. Turn on **Microphone**, **System audio**, or both before choosing **Record**. Both start off. Select a mic and check its level. For system audio, choose the output the sound is playing through. The list initially selects the Windows default.
3. Use the floating **Stop recording** button or the tray menu. The shortcut brings the recording controls forward. If the selected window resizes, the video keeps its original dimensions and fits the new window inside the frame.
4. Choose **Open recordings folder** in the tray for the H.264 MP4 in `%LOCALAPPDATA%\SimpleVideoSnipper\Recordings`. If either audio source was enabled, the file contains one AAC mix.

## Try the Chrome recording prototype

Install the .NET 10 SDK and Chrome, then run `dotnet run -c Release` on Windows.

1. Click **Open Chrome recorder** in the desktop window or tray menu.
2. Select **Include tab audio**, **Include microphone**, or both. Click **Choose tab** and select the Chrome tab you want to record. Enable tab audio in Chrome’s sharing dialog if needed. Chrome prompts for the microphone if you selected it.
3. Click **Record**. Keep the recorder page open and use **Stop recording** on the page, in the desktop window, or in the tray.
4. Open the `.webm` from **Open recordings folder** in the tray. The prototype saves directly to `%LOCALAPPDATA%\SimpleVideoSnipper\Recordings`. An interrupted transfer can leave a `.webm.partial` with the bytes received so far.

## Capture flow and defaults

1. Open the capture toolbar from the tray or with `Ctrl+Alt+R`. Let users rebind the shortcut, including Windows-key combinations that register successfully. Keep the previous binding if a new one conflicts. `Win+Shift+R` already opens Windows Snipping Tool. [Hotkey registration](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) · [Snipping Tool shortcuts](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots)
2. Start in **Area** mode. Drag an adjustable rectangle within one monitor, select an open application in **Window**, or confirm a source in Chrome’s picker for **Browser tab**. Show the selected source and a **Record** button. Esc cancels selection. A browser-window capture follows the app window, including its controls and whichever tab is visible. A tab capture stays with the selected page as the user switches tabs.
3. Offer **Microphone** and **System audio** for area and window, and **Microphone** and **Tab audio** for Chrome. Audio defaults to off. Show a microphone selector and level meter when enabled. If the mic disconnects, continue with the remaining streams and report the loss. System audio includes other sounds on the selected Windows output device. [WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
4. Show a recording indicator, elapsed time, and **Stop**, with a second Stop action in the tray. Pressing the shortcut during capture brings the controls forward. Keep snipper controls out of the recording. If the source closes, end the session and offer to save the captured clip. Show **Finishing** while writing the file. [Windows capture exclusion](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
5. Default to **Ask every time** with filename, folder, and format prefilled. **Quick Save** uses a unique filename, the preferred format, and the preferred folder, initially `Videos\Simple Video Snipper`. Offer MP4 (H.264 and AAC, when audio is enabled) and WebM (VP9 and Opus). Default to MP4. On success, offer **Open**, **Show in folder**, and **Copy video file**. Auto-copy is off by default and copies the saved file using the Windows file clipboard format. Show **Copied** after clipboard success and provide a retry if the clipboard is busy. [Windows file clipboard format](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard)

## Temporary recordings setup

Add a **Temporary recordings folder** page to the per-user installer. Prefill `%LOCALAPPDATA%\SimpleVideoSnipper\Recordings` and require the user to confirm it or choose another local folder. Use the copy “This is where recordings are held until you save or discard them.” Create the directory and run a create, write, and delete test with the installing user’s permissions. Keep the user on the page with an actionable error if the test fails. Persist the path in per-user settings and let users change it under **Settings → Storage**. Upgrades reuse the chosen path. [Inno Setup directory page](https://jrsoftware.org/inno-setup/documentation/topic_isxfunc_createinputdirpage.htm) · [Per-user install mode](https://jrsoftware.org/inno-setup/documentation/topic_setup_privilegesrequired.htm)

Check access and free space before Record. Write each session to an app-owned temporary file as it records. Export via a partial file in the destination folder and publish the final filename only after the write succeeds. If export fails, retain the staged clip and offer **Retry**, **Save As**, or **Discard**. Reopen finalized unsaved clips on the next launch. Remove only the app’s temporary clip after successful export or explicit discard. A storage setting change applies to new captures and preserves access to pending clips from the old folder.

## Implementation milestones

| Milestone | Work and acceptance check |
| --- | --- |
| 1. Prove the Chrome handoff | Build a local companion page served on loopback by the desktop app. A user click invokes `getDisplayMedia`; request the tab picker, verify the selected surface, and disable switching the capture source. Write `MediaRecorder` chunks in order to disk through a session-scoped local connection. Test recording a Chrome tab with its audio, switching tabs, and stopping from both the tray and the browser. The desktop app receives a playable clip without an upload or a manual download. [Chrome source controls](https://developer.chrome.com/docs/web-platform/screen-sharing-controls) · [MediaRecorder chunks](https://www.w3.org/TR/mediastream-recording/) |
| 2. Ship an internal area-to-MP4 build | Extend the .NET 10 tray app with a global shortcut and a per-monitor DPI-aware selection overlay. Use physical pixels for the rectangle and crop Windows Graphics Capture frames before encoding at 30 fps. Test a visible pixel grid at two Windows display scales and confirm that the rectangle is accurate, the app controls stay out of the clip, and the result plays as MP4. [WinForms DPI settings](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop#applicationhighdpimode) · [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) |
| 3. Add window capture and audio | Add open-window selection to the native capture path. Keep output dimensions fixed and fit a resized window inside the frame. Capture mic and system sound through WASAPI in native modes. In tab mode, mix the selected tab audio and optional mic in the companion page. Timestamp audio and video on a shared timeline and test each audio source alone, both together, and a mic disconnect during a multi-minute capture. |
| 4. Complete the shared export and settings flow | Route all three capture modes through the same staging, export, and clipboard code. Prototype FFmpeg as the shared exporter with MP4 and WebM presets. Reuse already compatible streams where possible. Verify both formats from each mode, Save As and Quick Save, paste into a target that accepts files, and run a retry after making the destination unwritable. Verify that a failed export leaves the temporary clip intact. [FFmpeg codec documentation](https://ffmpeg.org/ffmpeg-codecs.html) |
| 5. Package and test the installer | Build a per-user Inno Setup installer. On a clean Windows 11 account, test the default and a custom temporary folder, an unwritable path, changing the folder in Settings, and an upgrade. Install on a machine without development tools and record, save, and copy a clip from every capture mode. |
