import AVFoundation
import CoreMedia
import ScreenCaptureKit

/// Любой PCM -> 16 кГц, моно, Int16 (формат, который ждёт Deepgram linear16).
final class PCMConverter {
    private let outFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16000, channels: 1, interleaved: true)!
    private var converter: AVAudioConverter?
    private var inFormat: AVAudioFormat?

    /// Возвращает (байты PCM16, уровень громкости 0...1).
    func convert(_ buffer: AVAudioPCMBuffer) -> (Data, Float)? {
        if inFormat == nil || inFormat! != buffer.format {
            inFormat = buffer.format
            converter = AVAudioConverter(from: buffer.format, to: outFormat)
        }
        guard let converter, buffer.frameLength > 0 else { return nil }
        let ratio = outFormat.sampleRate / buffer.format.sampleRate
        let cap = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 64
        guard let out = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: cap) else { return nil }
        var fed = false
        var err: NSError?
        converter.convert(to: out, error: &err) { _, status in
            if fed { status.pointee = .noDataNow; return nil }
            fed = true; status.pointee = .haveData; return buffer
        }
        guard err == nil, out.frameLength > 0, let ch = out.int16ChannelData else { return nil }
        let n = Int(out.frameLength)
        var sum: Float = 0
        for i in 0..<n { let v = Float(ch[0][i]) / 32768; sum += v * v }
        let rms = sqrt(sum / Float(n))
        return (Data(bytes: ch[0], count: n * 2), min(1, rms * 8))
    }
}

/// Звук созвона (всё, что играет на маке) через ScreenCaptureKit. Нужен доступ «Запись экрана».
final class SystemAudioCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    var onPCM: ((Data, Float) -> Void)?
    var onError: ((String) -> Void)?
    private var stream: SCStream?
    private let converter = PCMConverter()
    private let audioQueue = DispatchQueue(label: "notely.sysaudio")
    private let videoQueue = DispatchQueue(label: "notely.video")

    func start() async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { throw NSError(domain: "Notely", code: 1, userInfo: [NSLocalizedDescriptionKey: "нет дисплея"]) }
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let cfg = SCStreamConfiguration()
        cfg.capturesAudio = true
        cfg.excludesCurrentProcessAudio = true
        cfg.sampleRate = 48000
        cfg.channelCount = 1
        cfg.width = 2
        cfg.height = 2
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        cfg.queueDepth = 3
        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        try await s.startCapture()
        stream = s
        Log.write("system audio: started")
    }

    func stop() {
        stream?.stopCapture { _ in }
        stream = nil
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sb.isValid,
              let desc = CMSampleBufferGetFormatDescription(sb),
              let asbdPtr = CMAudioFormatDescriptionGetStreamBasicDescription(desc) else { return }
        var asbd = asbdPtr.pointee
        guard let fmt = AVAudioFormat(streamDescription: &asbd) else { return }
        let frames = AVAudioFrameCount(CMSampleBufferGetNumSamples(sb))
        guard frames > 0, let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: frames) else { return }
        buf.frameLength = frames
        guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sb, at: 0, frameCount: Int32(frames), into: buf.mutableAudioBufferList) == noErr else { return }
        if let (data, level) = converter.convert(buf) { onPCM?(data, level) }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        Log.write("system audio stopped: \(error.localizedDescription)")
        onError?(error.localizedDescription)
    }
}

/// Микрофон (твой голос) — только для контекста разговора, ответы по нему не запускаются.
final class MicCapture {
    var onPCM: ((Data, Float) -> Void)?
    private let engine = AVAudioEngine()
    private let converter = PCMConverter()

    func start() throws {
        let input = engine.inputNode
        let fmt = input.outputFormat(forBus: 0)
        guard fmt.sampleRate > 0 else { throw NSError(domain: "Notely", code: 2, userInfo: [NSLocalizedDescriptionKey: "микрофон не найден"]) }
        input.installTap(onBus: 0, bufferSize: 2048, format: fmt) { [weak self] buf, _ in
            guard let self, let (data, level) = self.converter.convert(buf) else { return }
            self.onPCM?(data, level)
        }
        engine.prepare()
        try engine.start()
        Log.write("mic: started \(fmt.sampleRate) Hz")
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
    }
}

/// Тестовый режим: читает аудиофайл и отдаёт его как «звук созвона» в реальном времени.
final class FileFeeder {
    var onPCM: ((Data, Float) -> Void)?
    var onDone: (() -> Void)?
    private let url: URL
    private let speed: Double
    init(url: URL, speed: Double) { self.url = url; self.speed = speed }

    func start() throws {
        let file = try AVAudioFile(forReading: url)
        let converter = PCMConverter()
        let chunk = AVAudioFrameCount(file.processingFormat.sampleRate * 0.1)
        let interval = 0.1 / speed
        DispatchQueue.global().async {
            while true {
                guard let buf = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: chunk) else { break }
                do { try file.read(into: buf, frameCount: chunk) } catch { break }
                if buf.frameLength == 0 { break }
                if let (d, l) = converter.convert(buf) { self.onPCM?(d, l) }
                Thread.sleep(forTimeInterval: interval)
            }
            self.onDone?()
        }
    }
}
