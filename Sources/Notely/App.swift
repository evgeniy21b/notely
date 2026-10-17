import AppKit
import AVFoundation
import Carbon
import ScreenCaptureKit

// MARK: глобальные горячие клавиши (Carbon: не требуют доступа «Универсальный доступ»)

enum HotKeys {
    static var handlers: [UInt32: () -> Void] = [:]
    static var refs: [EventHotKeyRef?] = []
    static let mods = UInt32(controlKey | optionKey)

    static func install() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            let h = HotKeys.handlers[hk.id]
            DispatchQueue.main.async { h?() }
            return noErr
        }, 1, &spec, nil, nil)
    }

    static func add(_ keyCode: Int, _ handler: @escaping () -> Void) {
        let id = UInt32(handlers.count + 1)
        var ref: EventHotKeyRef?
        RegisterEventHotKey(UInt32(keyCode), mods, EventHotKeyID(signature: OSType(0x4E544C59), id: id), GetApplicationEventTarget(), 0, &ref)
        refs.append(ref)
        handlers[id] = handler
    }
}

// MARK: скриншот экрана без нашего окна

enum Screenshot {
    static func capture() async throws -> (NSImage, Data) {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { throw NSError(domain: "Notely", code: 3) }
        let me = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let filter = SCContentFilter(display: display, excludingApplications: me, exceptingWindows: [])
        let cfg = SCStreamConfiguration()
        cfg.width = min(2400, display.width * 2)
        cfg.height = Int(Double(cfg.width) * Double(display.height) / Double(display.width))
        cfg.showsCursor = false
        let cg = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
        let rep = NSBitmapImageRep(cgImage: cg)
        let jpeg = rep.representation(using: .jpeg, properties: [.compressionFactor: 0.75]) ?? Data()
        return (NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height)), jpeg)
    }

    /// Снимок как его увидит запись/шеринг экрана (наше окно НЕ исключаем) — для проверки невидимости.
    static func captureAsShared() async throws -> URL {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { throw NSError(domain: "Notely", code: 3) }
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let cfg = SCStreamConfiguration()
        cfg.width = display.width; cfg.height = display.height
        let cg = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
        let url = AppPaths.dir.appendingPathComponent("invisibility-test.png")
        try NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])?.write(to: url)
        return url
    }

    static func jpeg(from img: NSImage) -> Data? {
        guard let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .jpeg, properties: [.compressionFactor: 0.75])
    }
}

// MARK: приложение

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var overlay: OverlayController!
    private var brain: Brain!
    private var themDG: DeepgramStream!
    private var meDG: DeepgramStream!
    private let sys = SystemAudioCapture()
    private let mic = MicCapture()
    private var statusItem: NSStatusItem!
    private var paused = false
    private var themLevel: Float = 0, meLevel: Float = 0
    private var themActiveUntil = Date.distantPast   // пока звучит собеседник (+0.6 с хвост) — микрофон глушим (эхо динамиков)
    private var levelTimer: Timer?
    private var problems: [String] = []

    func applicationDidFinishLaunching(_ n: Notification) {
        AppPaths.ensureProfile()
        overlay = OverlayController()
        overlay.panel.orderFrontRegardless()
        buildMenu()

        guard let dgKey = Keychain.get("notely-deepgram"), let orKey = Keychain.get("notely-openrouter") else {
            overlay.setAnswer("Нет ключей в связке ключей (notely-deepgram / notely-openrouter).")
            return
        }
        brain = Brain(key: orKey)
        brain.onEvent = { [weak self] e in DispatchQueue.main.async { self?.handle(e) } }

        themDG = DeepgramStream(label: "them", key: dgKey)
        themDG.onFinal = { [weak self] t, sf in self?.brain.themFinal(t, speechFinal: sf) }
        themDG.onUtteranceEnd = { [weak self] in self?.brain.themUtteranceEnd() }
        meDG = DeepgramStream(label: "me", key: dgKey)
        meDG.onFinal = { [weak self] t, sf in self?.brain.meFinal(t, speechFinal: sf) }
        meDG.onUtteranceEnd = { [weak self] in self?.brain.meUtteranceEnd() }

        sys.onPCM = { [weak self] d, l in
            guard let self, !self.paused else { return }
            self.themLevel = max(self.themLevel, l)
            if l > 0.04 { self.themActiveUntil = Date().addingTimeInterval(0.6) }
            self.themDG.send(d)
        }
        sys.onError = { [weak self] e in DispatchQueue.main.async { self?.overlay.setStatus("звук созвона отвалился: \(e)") } }
        mic.onPCM = { [weak self] d, l in
            guard let self, !self.paused else { return }
            self.meLevel = max(self.meLevel, l)
            // собеседник говорит — шлём тишину вместо микрофона, чтобы его голос из динамиков не попал в «Я»
            self.meDG.send(Date() < self.themActiveUntil ? Data(count: d.count) : d)
        }

        overlay.onSend = { [weak self] text, img in
            guard let self else { return }
            self.brain.askManual(text: text.isEmpty ? "" : text, image: img.flatMap { Screenshot.jpeg(from: $0) })
        }
        overlay.onScreenshot = { [weak self] in self?.attachScreenshot(send: false) }

        HotKeys.install()
        HotKeys.add(kVK_Space) { [weak self] in self?.toggleVisible() }
        HotKeys.add(kVK_ANSI_I) { [weak self] in self?.showAndFocus() }
        HotKeys.add(kVK_ANSI_S) { [weak self] in self?.attachScreenshot(send: true) }
        HotKeys.add(kVK_ANSI_P) { [weak self] in self?.togglePause() }
        HotKeys.add(kVK_DownArrow) { [weak self] in self?.overlay.scroll(by: 60) }
        HotKeys.add(kVK_UpArrow) { [weak self] in self?.overlay.scroll(by: -60) }

        levelTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.overlay.setLevel(them: self.themLevel, me: self.meLevel)
            self.themLevel = 0; self.meLevel = 0
        }
        startListening()
    }

    private func startListening() {
        themDG.start(); meDG.start()
        Task { @MainActor in
            do { try await sys.start() } catch {
                problems.append("нет доступа к звуку созвона: дай Notely «Запись экрана и системного звука» в настройках и перезапусти")
                Log.write("system audio error: \(error)")
            }
            let granted = await AVCaptureDevice.requestAccess(for: .audio)
            if granted {
                do { try mic.start() } catch { problems.append("микрофон: \(error.localizedDescription)") }
            } else {
                problems.append("нет доступа к микрофону (это не критично: твои ответы просто не попадут в контекст)")
            }
            refreshStatus()
            if !problems.isEmpty { overlay.setAnswer(problems.joined(separator: "\n")) }
        }
    }

    private func refreshStatus() {
        let model = Settings.models.first { $0.id == Settings.model }?.title ?? Settings.model
        overlay.setStatus(paused ? "пауза" : "слушаю · \(model)")
    }

    private func handle(_ e: Brain.Event) {
        switch e {
        case .thinking: overlay.setStatus("думаю…")
        case .answer(let t): overlay.setStatus("отвечаю…"); overlay.setAnswer(t)
        case .done(let t): overlay.setAnswer(t); refreshStatus()
        case .skipped: refreshStatus()
        case .error(let s): overlay.setStatus("ошибка"); overlay.setAnswer("Ошибка: \(s)", dim: true); Log.write("error: \(s)")
        }
    }

    private func toggleVisible() {
        if overlay.panel.isVisible { overlay.panel.orderOut(nil) } else { overlay.panel.orderFrontRegardless() }
    }

    private func showAndFocus() {
        overlay.panel.orderFrontRegardless()
        overlay.focusInput()
    }

    private func togglePause() {
        paused.toggle()
        brain?.paused = paused
        refreshStatus()
        rebuildMenu()
    }

    private func attachScreenshot(send: Bool) {
        Task { @MainActor in
            do {
                let (img, jpeg) = try await Screenshot.capture()
                if send {
                    overlay.panel.orderFrontRegardless()
                    brain.askManual(text: "", image: jpeg)
                } else {
                    overlay.attach(img)
                }
            } catch {
                overlay.setAnswer("Скриншот не получился: \(error.localizedDescription). Нужен доступ «Запись экрана».", dim: true)
            }
        }
    }

    // MARK: меню в строке меню

    private func buildMenu() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "note.text", accessibilityDescription: "Notely")
        rebuildMenu()
    }

    private func rebuildMenu() {
        let m = NSMenu()
        m.addItem(withTitle: "Показать / свернуть  (⌃⌥Space)", action: #selector(menuToggle), keyEquivalent: "").target = self
        m.addItem(withTitle: paused ? "Продолжить слушать  (⌃⌥P)" : "Пауза  (⌃⌥P)", action: #selector(menuPause), keyEquivalent: "").target = self
        m.addItem(withTitle: "Новый разговор (очистить контекст)", action: #selector(menuReset), keyEquivalent: "").target = self
        m.addItem(.separator())
        let models = NSMenuItem(title: "Модель", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for (i, mdl) in Settings.models.enumerated() {
            let it = NSMenuItem(title: mdl.title, action: #selector(menuModel(_:)), keyEquivalent: "")
            it.target = self; it.tag = i; it.state = mdl.id == Settings.model ? .on : .off
            sub.addItem(it)
        }
        models.submenu = sub
        m.addItem(models)
        m.addItem(withTitle: "Профиль (резюме, вакансия)…", action: #selector(menuProfile), keyEquivalent: "").target = self
        m.addItem(withTitle: "Проверить невидимость окна", action: #selector(menuInvisibility), keyEquivalent: "").target = self
        m.addItem(.separator())
        m.addItem(withTitle: "Выйти", action: #selector(menuQuit), keyEquivalent: "").target = self
        statusItem.menu = m
    }

    @objc private func menuToggle() { toggleVisible() }
    @objc private func menuPause() { togglePause() }
    @objc private func menuReset() { brain?.reset(); overlay.setAnswer("Контекст очищен. Слушаю.", dim: true) }
    @objc private func menuModel(_ s: NSMenuItem) { Settings.model = Settings.models[s.tag].id; refreshStatus(); rebuildMenu() }
    @objc private func menuProfile() { AppPaths.ensureProfile(); NSWorkspace.shared.open(AppPaths.profile) }
    @objc private func menuQuit() { NSApp.terminate(nil) }

    @objc private func menuInvisibility() {
        overlay.panel.orderFrontRegardless()
        overlay.setAnswer("ПРОВЕРКА НЕВИДИМОСТИ: если ты видишь этот текст на открывшемся снимке — окно видно при шеринге.")
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 400_000_000)
            do {
                let url = try await Screenshot.captureAsShared()
                NSWorkspace.shared.open(url)
                overlay.setAnswer("Открыл снимок экрана глазами записи. Если на нём НЕТ этого окна — при шеринге его не видно.", dim: true)
            } catch {
                overlay.setAnswer("Проверка не удалась: \(error.localizedDescription)", dim: true)
            }
        }
    }
}

// MARK: тестовый режим без окна: аудиофайл как «собеседник»

final class FileTest {
    let brain: Brain
    let dg: DeepgramStream
    let feeder: FileFeeder
    init(path: String, speed: Double) {
        let dgKey = Keychain.get("notely-deepgram")!, orKey = Keychain.get("notely-openrouter")!
        brain = Brain(key: orKey)
        dg = DeepgramStream(label: "them", key: dgKey)
        feeder = FileFeeder(url: URL(fileURLWithPath: path), speed: speed)
        brain.onEvent = { e in
            switch e {
            case .done(let t): print("\n=== ПОДСКАЗКА ===\n\(t)\n=================\n")
            case .skipped: print("(не вопрос, пропуск)")
            case .error(let s): print("ОШИБКА: \(s)")
            default: break
            }
        }
        dg.onFinal = { [brain] t, sf in if !t.isEmpty { print("  [собеседник] \(t)\(sf ? " ⏎" : "")") }; brain.themFinal(t, speechFinal: sf) }
        dg.onUtteranceEnd = { [brain] in brain.themUtteranceEnd() }
        feeder.onPCM = { [dg] d, _ in dg.send(d) }
        feeder.onDone = { print("-- файл закончился, жду последние ответы 15 с --"); DispatchQueue.main.asyncAfter(deadline: .now() + 15) { exit(0) } }
    }
    func run() {
        dg.start()
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { try? self.feeder.start() }
    }
}
