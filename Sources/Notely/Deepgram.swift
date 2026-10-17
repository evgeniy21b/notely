import Foundation

/// Потоковое распознавание Deepgram (WebSocket). Один поток на один источник звука.
final class DeepgramStream: NSObject {
    let label: String
    var onFinal: ((String, Bool) -> Void)?      // текст, конец фразы (speech_final)
    var onUtteranceEnd: (() -> Void)?
    var onStatus: ((Bool) -> Void)?              // подключён / нет

    private let key: String
    private var task: URLSessionWebSocketTask?
    private var session: URLSession!
    private var running = false
    private var connected = false
    private var keepAlive: Timer?
    private let q = DispatchQueue(label: "notely.dg")

    private static let url = URL(string:
        "wss://api.deepgram.com/v1/listen?model=nova-3&language=ru&encoding=linear16&sample_rate=16000&channels=1"
        + "&punctuate=true&smart_format=true&interim_results=true&endpointing=400&utterance_end_ms=1200&vad_events=true")!

    init(label: String, key: String) {
        self.label = label; self.key = key
        super.init()
        session = URLSession(configuration: .default)
    }

    func start() { running = true; connect() }

    func stop() {
        running = false
        keepAlive?.invalidate()
        task?.send(.string("{\"type\":\"CloseStream\"}")) { _ in }
        task?.cancel(with: .goingAway, reason: nil)
        task = nil
    }

    func send(_ pcm: Data) {
        q.async { [weak self] in
            guard let self, self.connected, let t = self.task else { return }
            t.send(.data(pcm)) { _ in }
        }
    }

    private func connect() {
        guard running else { return }
        var req = URLRequest(url: Self.url)
        req.setValue("Token \(key)", forHTTPHeaderField: "Authorization")
        let t = session.webSocketTask(with: req)
        task = t
        t.resume()
        q.async { self.connected = true }
        receive(t)
        DispatchQueue.main.async {
            self.keepAlive?.invalidate()
            self.keepAlive = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
                self?.task?.send(.string("{\"type\":\"KeepAlive\"}")) { _ in }
            }
        }
        Log.write("deepgram[\(label)]: connecting")
    }

    private func receive(_ t: URLSessionWebSocketTask) {
        t.receive { [weak self] result in
            guard let self, t === self.task else { return }
            switch result {
            case .success(let msg):
                if case .string(let s) = msg { self.handle(s) }
                self.receive(t)
            case .failure(let err):
                Log.write("deepgram[\(self.label)]: disconnected: \(err.localizedDescription)")
                self.q.async { self.connected = false }
                self.onStatus?(false)
                if self.running {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 2) { self.connect() }
                }
            }
        }
    }

    private var announced = false
    private func handle(_ s: String) {
        guard let d = s.data(using: .utf8),
              let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return }
        let type = j["type"] as? String ?? ""
        if type == "Metadata" || !announced { announced = true; onStatus?(true) }
        if type == "UtteranceEnd" { onUtteranceEnd?(); return }
        guard type == "Results",
              (j["is_final"] as? Bool) == true,
              let ch = j["channel"] as? [String: Any],
              let alts = ch["alternatives"] as? [[String: Any]],
              let text = alts.first?["transcript"] as? String else { return }
        let speechFinal = (j["speech_final"] as? Bool) ?? false
        if !text.isEmpty || speechFinal { onFinal?(text, speechFinal) }
    }
}
