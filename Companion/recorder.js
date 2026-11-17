"use strict";

const audio = document.querySelector("#audio");
const microphone = document.querySelector("#microphone");
const action = document.querySelector("#action");
const cancel = document.querySelector("#cancel");
const statusLine = document.querySelector("#status");
const mimeType = "video/webm;codecs=vp8,opus";
const maxBufferedBytes = 32 * 1024 * 1024;

let phase = "idle";
let stream;
let inputMic;
let mixedStream;
let mixer;
let recorder;
let socket;

function render(nextPhase, message) {
  phase = nextPhase;
  action.textContent = {
    idle: "Choose tab",
    choosing: "Choosing tab…",
    ready: "Record",
    connecting: "Starting…",
    recording: "Stop recording",
    finishing: "Finishing…",
  }[phase];
  action.disabled = !["idle", "ready", "recording"].includes(phase);
  cancel.hidden = phase !== "ready";
  audio.disabled = phase !== "idle";
  microphone.disabled = phase !== "idle";
  statusLine.textContent = message;
}

function releaseTracks() {
  stream?.getTracks().forEach(track => track.stop());
  inputMic?.getTracks().forEach(track => track.stop());
  mixedStream?.getAudioTracks().forEach(track => track.stop());
  void mixer?.close();
  stream = undefined;
  inputMic = undefined;
  mixedStream = undefined;
  mixer = undefined;
}

function reset(message) {
  const previousRecorder = recorder;
  const previousSocket = socket;
  recorder = undefined;
  socket = undefined;
  if (previousRecorder && previousRecorder.state !== "inactive") {
    previousRecorder.stop();
  }
  releaseTracks();
  previousSocket?.close();
  render("idle", message);
}

async function chooseTab() {
  render("choosing", "Select a Chrome tab in the share dialog.");
  let requestingMicrophone = false;
  try {
    const controller = typeof CaptureController === "function" ? new CaptureController() : undefined;
    stream = await navigator.mediaDevices.getDisplayMedia({
      controller,
      video: { displaySurface: "browser", frameRate: { ideal: 30, max: 30 } },
      audio: audio.checked,
      selfBrowserSurface: "exclude",
      surfaceSwitching: "exclude",
      monitorTypeSurfaces: "exclude",
      systemAudio: "exclude",
    });
    const [videoTrack] = stream.getVideoTracks();
    if (videoTrack?.getSettings().displaySurface !== "browser") {
      reset("Choose an individual Chrome tab in the share dialog.");
      return;
    }
    controller?.setFocusBehavior("no-focus-change");
    if (audio.checked && stream.getAudioTracks().length === 0) {
      reset("Tab audio was requested. Choose the tab again and enable audio in Chrome’s share dialog.");
      return;
    }
    if (microphone.checked) {
      render("choosing", "Allow Chrome to use your microphone.");
      requestingMicrophone = true;
      inputMic = await navigator.mediaDevices.getUserMedia({ audio: true });
      if (videoTrack.readyState !== "live") {
        reset("Chrome stopped sharing the tab. Choose a tab to try again.");
        return;
      }
      inputMic.getAudioTracks()[0].addEventListener("ended", () => {
        if (phase === "recording") {
          render("recording", "The microphone became unavailable. Tab recording is continuing.");
        } else if (phase === "ready" || phase === "connecting") {
          reset("The microphone became unavailable. Choose the tab to try again.");
        }
      }, { once: true });
    }
    videoTrack.addEventListener("ended", () => {
      if (phase === "recording") {
        stopRecording();
      } else if (phase === "ready" || phase === "connecting") {
        reset("Chrome stopped sharing the tab. Choose a tab to try again.");
      }
    }, { once: true });
    const sources = [audio.checked && "tab audio", microphone.checked && "microphone"].filter(Boolean);
    render("ready", sources.length ? `Tab selected. Audio sources: ${sources.join(" and ")}.` : "Tab selected.");
  } catch (error) {
    reset(requestingMicrophone ? `Could not use the microphone. ${error.message}` : error.name === "NotAllowedError"
      ? "Tab selection was canceled or blocked by Chrome."
      : `Could not select a tab. ${error.message}`);
  }
}

async function startRecording() {
  render("connecting", "Connecting to the desktop app…");
  try {
    let recordingStream = stream;
    if (inputMic) {
      mixer = new AudioContext();
      const destination = mixer.createMediaStreamDestination();
      const sources = audio.checked ? [stream, inputMic] : [inputMic];
      for (const input of sources) {
        const gain = mixer.createGain();
        gain.gain.value = 1 / sources.length;
        mixer.createMediaStreamSource(input).connect(gain).connect(destination);
      }
      mixedStream = new MediaStream([...stream.getVideoTracks(), ...destination.stream.getAudioTracks()]);
      const current = mixer;
      await current.resume();
      if (mixer !== current) {
        return;
      }
      recordingStream = mixedStream;
    }
    const hasAudio = recordingStream.getAudioTracks().length > 0;
    const type = hasAudio ? mimeType : "video/webm;codecs=vp8";
    const media = new MediaRecorder(recordingStream, { mimeType: type });
    const endpoint = new URL("record", location.href);
    endpoint.protocol = "ws:";
    const connection = new WebSocket(endpoint);
    recorder = media;
    socket = connection;

    media.addEventListener("dataavailable", ({ data }) => {
      if (socket !== connection || data.size === 0) {
        return;
      }
      if (connection.readyState !== WebSocket.OPEN ||
          connection.bufferedAmount + data.size > maxBufferedBytes) {
        reset("The desktop app could not receive the recording.");
        return;
      }
      connection.send(data);
    });
    media.addEventListener("error", () => {
      if (socket === connection) {
        reset("Chrome could not record the selected tab.");
      }
    });
    media.addEventListener("stop", () => {
      if (socket === connection) {
        releaseTracks();
        if (connection.readyState === WebSocket.OPEN) {
          render("finishing", "Saving the clip to the local recordings folder…");
          connection.send("finish");
        } else {
          reset("The connection closed before the clip could finish saving.");
        }
      }
    });

    connection.addEventListener("message", ({ data }) => {
      if (socket !== connection) {
        return;
      }
      if (data === "ready") {
        try {
          media.start(1000);
          render("recording", hasAudio ? "Recording tab with audio." : "Recording tab.");
        } catch (error) {
          reset(`Could not start recording. ${error.message}`);
        }
      } else if (data === "stop") {
        stopRecording();
      } else if (data.startsWith("saved:")) {
        reset(`Saved in the local recordings folder. ${data.slice(6)}`);
      } else if (data.startsWith("error:")) {
        reset(data.slice(6));
      }
    });
    connection.addEventListener("close", () => {
      if (socket === connection) {
        reset(phase === "connecting"
          ? "Could not start. Check that the desktop app is open and another recording is not running."
          : "The desktop app disconnected before confirming the save.");
      }
    });
  } catch (error) {
    reset(`Could not start recording. ${error.message}`);
  }
}

function stopRecording() {
  if (phase === "recording" && recorder.state !== "inactive") {
    render("finishing", "Finishing the recording…");
    recorder.stop();
  }
}

action.addEventListener("click", () => {
  if (phase === "idle") {
    void chooseTab();
  } else if (phase === "ready") {
    void startRecording();
  } else if (phase === "recording") {
    stopRecording();
  }
});

cancel.addEventListener("click", () => reset("Choose the tab you want to record."));
