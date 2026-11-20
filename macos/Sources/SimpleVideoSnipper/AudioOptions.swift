import AVFoundation
import SwiftUI

struct AudioOptions {
    static let autoStartKey = "startAreaOnSelection"
    static let systemKey = "defaultSystemAudio"
    static let microphoneKey = "defaultMicrophone"
    static let microphoneIDKey = "defaultMicrophoneID"

    var system = false
    var microphone = false
    var microphoneID: String?

    static func saved(in defaults: UserDefaults) -> AudioOptions {
        let id = defaults.string(forKey: microphoneIDKey)
        return AudioOptions(system: defaults.bool(forKey: systemKey),
                            microphone: defaults.bool(forKey: microphoneKey),
                            microphoneID: id?.isEmpty == false ? id : nil)
    }

    static func microphoneInputs() -> [AVCaptureDevice] {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone, .external],
                                         mediaType: .audio, position: .unspecified).devices
    }

    static func microphoneInput(id: String?) -> AVCaptureDevice? {
        if let id { return microphoneInputs().first { $0.uniqueID == id } }
        return AVCaptureDevice.default(for: .audio)
    }

    func availableForRecording() async -> (options: AudioOptions, notice: String?) {
        guard microphone else { return (self, nil) }
        let remaining = AudioOptions(system: system)
        guard let device = Self.microphoneInput(id: microphoneID), device.isConnected else {
            return (remaining, "The selected microphone is unavailable. Continuing with the available sources.")
        }
        guard await AVCaptureDevice.requestAccess(for: .audio) else {
            return (remaining, "Microphone access is denied. Continuing with the available sources.")
        }
        guard device.isConnected else {
            return (remaining, "The selected microphone disconnected. Continuing with the available sources.")
        }
        return (AudioOptions(system: system, microphone: true, microphoneID: device.uniqueID), nil)
    }
}

struct MicrophoneInputPicker: View {
    @Binding var selection: String
    @State private var inputs = AudioOptions.microphoneInputs()

    var body: some View {
        Picker("Microphone input", selection: $selection) {
            Text("System default").tag("")
            if !selection.isEmpty && !inputs.contains(where: { $0.uniqueID == selection }) {
                Text("Unavailable microphone").tag(selection)
            }
            ForEach(inputs, id: \.uniqueID) { device in
                Text(device.localizedName).tag(device.uniqueID)
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: AVCaptureDevice.wasConnectedNotification)) { _ in
            inputs = AudioOptions.microphoneInputs()
        }
        .onReceive(NotificationCenter.default.publisher(for: AVCaptureDevice.wasDisconnectedNotification)) { _ in
            inputs = AudioOptions.microphoneInputs()
        }
    }
}
