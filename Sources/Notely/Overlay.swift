import AppKit

/// Плавающая панель поверх всего. sharingType = .none — просим систему не отдавать окно в запись/шеринг экрана.
final class OverlayPanel: NSPanel {
    var onPasteImage: ((NSImage) -> Void)?

    init(rect: NSRect) {
        super.init(contentRect: rect, styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        sharingType = .none
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        isMovableByWindowBackground = true
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = true
    }
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    // У приложения без меню нет Cmd+C/V/A/X — обрабатываем сами. Картинку из буфера прикрепляем.
    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard e.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command,
              let ch = e.charactersIgnoringModifiers?.lowercased() else { return super.performKeyEquivalent(with: e) }
        switch ch {
        case "v":
            let pb = NSPasteboard.general
            if pb.string(forType: .string) == nil, let img = NSImage(pasteboard: pb) { onPasteImage?(img); return true }
            return NSApp.sendAction(#selector(NSText.paste(_:)), to: nil, from: self)
        case "c": return NSApp.sendAction(#selector(NSText.copy(_:)), to: nil, from: self)
        case "x": return NSApp.sendAction(#selector(NSText.cut(_:)), to: nil, from: self)
        case "a": return NSApp.sendAction(#selector(NSText.selectAll(_:)), to: nil, from: self)
        case "z": return NSApp.sendAction(Selector(("undo:")), to: nil, from: self)
        default: return super.performKeyEquivalent(with: e)
        }
    }
}

final class OverlayController: NSObject, NSTextFieldDelegate {
    let panel: OverlayPanel
    var onSend: ((String, NSImage?) -> Void)?
    var onScreenshot: (() -> Void)?

    private let answerView = NSTextView()
    private let scroll = NSScrollView()
    private let status = NSTextField(labelWithString: "запуск…")
    private let themDot = NSView()
    private let meDot = NSView()
    private let input = NSTextField()
    private let thumb = NSImageView()
    private var attached: NSImage?
    private var heightFraction: CGFloat = 0.30

    override init() {
        panel = OverlayPanel(rect: NSRect(x: 0, y: 0, width: 800, height: 300))
        super.init()
        build()
        layoutOnScreen()
        panel.onPasteImage = { [weak self] img in self?.attach(img) }
    }

    private func build() {
        let bg = NSVisualEffectView()
        bg.material = .hudWindow
        bg.blendingMode = .behindWindow
        bg.state = .active
        bg.appearance = NSAppearance(named: .vibrantDark)
        bg.wantsLayer = true
        bg.layer?.cornerRadius = 12
        bg.layer?.masksToBounds = true
        panel.contentView = bg

        for (dot, tip) in [(themDot, "звук созвона"), (meDot, "микрофон")] {
            dot.wantsLayer = true
            dot.layer?.cornerRadius = 4
            dot.layer?.backgroundColor = NSColor.systemGray.cgColor
            dot.toolTip = tip
            dot.translatesAutoresizingMaskIntoConstraints = false
            dot.widthAnchor.constraint(equalToConstant: 8).isActive = true
            dot.heightAnchor.constraint(equalToConstant: 8).isActive = true
        }
        status.font = .systemFont(ofSize: 11)
        status.textColor = .secondaryLabelColor
        status.lineBreakMode = .byTruncatingTail
        let hint = NSTextField(labelWithString: "⌃⌥Space скрыть · ⌃⌥I ввод · ⌃⌥S скрин · ⌃⌥P пауза · ⌃⌥ +/− шрифт · ⌃⌥ ←/→ ширина")
        hint.font = .systemFont(ofSize: 10)
        hint.textColor = .tertiaryLabelColor
        hint.lineBreakMode = .byTruncatingTail
        hint.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let top = NSStackView(views: [themDot, meDot, status, NSView(), hint])
        top.spacing = 6
        top.alignment = .centerY

        answerView.isEditable = false
        answerView.isSelectable = true
        answerView.drawsBackground = false
        answerView.textContainerInset = NSSize(width: 2, height: 4)
        answerView.isVerticallyResizable = true
        answerView.autoresizingMask = [.width]
        scroll.documentView = answerView
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        scroll.autohidesScrollers = true

        thumb.imageScaling = .scaleProportionallyUpOrDown
        thumb.isHidden = true
        thumb.translatesAutoresizingMaskIntoConstraints = false
        thumb.widthAnchor.constraint(equalToConstant: 44).isActive = true
        thumb.heightAnchor.constraint(equalToConstant: 26).isActive = true
        thumb.toolTip = "прикреплён скриншот (Esc — убрать)"

        input.placeholderString = "Вопрос текстом или Cmd+V скриншот → Enter"
        input.font = .systemFont(ofSize: 14)
        input.delegate = self
        input.target = self
        input.action = #selector(sendPressed)
        input.focusRingType = .none
        input.bezelStyle = .roundedBezel

        let shot = NSButton(title: "Скрин", target: self, action: #selector(shotPressed))
        shot.bezelStyle = .rounded
        let send = NSButton(title: "Отправить", target: self, action: #selector(sendPressed))
        send.bezelStyle = .rounded
        send.keyEquivalent = ""
        let bottom = NSStackView(views: [thumb, input, shot, send])
        bottom.spacing = 6
        input.setContentHuggingPriority(.defaultLow, for: .horizontal)

        let col = NSStackView(views: [top, scroll, bottom])
        col.orientation = .vertical
        col.alignment = .leading
        col.spacing = 6
        col.edgeInsets = NSEdgeInsets(top: 8, left: 12, bottom: 10, right: 12)
        col.translatesAutoresizingMaskIntoConstraints = false
        bg.addSubview(col)
        NSLayoutConstraint.activate([
            col.leadingAnchor.constraint(equalTo: bg.leadingAnchor),
            col.trailingAnchor.constraint(equalTo: bg.trailingAnchor),
            col.topAnchor.constraint(equalTo: bg.topAnchor),
            col.bottomAnchor.constraint(equalTo: bg.bottomAnchor),
            top.widthAnchor.constraint(equalTo: col.widthAnchor, constant: -24),
            scroll.widthAnchor.constraint(equalTo: col.widthAnchor, constant: -24),
            bottom.widthAnchor.constraint(equalTo: col.widthAnchor, constant: -24),
        ])
        scroll.setContentHuggingPriority(.defaultLow, for: .vertical)
        scroll.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        setAnswer("Слушаю созвон. Когда собеседник задаст вопрос, подсказка появится здесь.", dim: true)
    }

    // MARK: геометрия

    func layoutOnScreen() {
        guard let screen = NSScreen.main else { return }
        let vf = screen.visibleFrame
        let w = max(420, min(vf.width - 20, vf.width * Settings.widthFraction))
        let h = max(180, vf.height * heightFraction)
        panel.setFrame(NSRect(x: vf.midX - w / 2, y: vf.maxY - h - 6, width: w, height: h), display: true)
    }

    func changeWidth(_ delta: CGFloat) {
        Settings.widthFraction = min(0.98, max(0.3, Settings.widthFraction + delta))
        let f = panel.frame
        guard let screen = panel.screen ?? NSScreen.main else { return }
        let w = max(420, min(screen.visibleFrame.width - 20, screen.visibleFrame.width * Settings.widthFraction))
        panel.setFrame(NSRect(x: f.midX - w / 2, y: f.minY, width: w, height: f.height), display: true)
    }

    func changeFont(_ delta: CGFloat) {
        Settings.fontSize = min(32, max(11, Settings.fontSize + delta))
        setAnswer(lastText, dim: lastDim)
    }

    // MARK: содержимое

    private var lastText = ""
    private var lastDim = false

    func setAnswer(_ text: String, dim: Bool = false) {
        lastText = text; lastDim = dim
        let size = Settings.fontSize
        let color: NSColor = dim ? .secondaryLabelColor : .white
        let out = NSMutableAttributedString()
        // ``` блоки кода рисуем моноширинным на подложке, остальное — markdown (жирный, `код`)
        let parts = text.components(separatedBy: "```")
        for (i, part) in parts.enumerated() {
            if i % 2 == 1 {
                var code = part
                if let nl = code.firstIndex(of: "\n"), !code[..<nl].contains(" ") { code = String(code[code.index(after: nl)...]) } // убрать «sql»
                code = code.trimmingCharacters(in: .newlines)
                let a = NSMutableAttributedString(string: "\n" + code + "\n", attributes: [
                    .font: NSFont.monospacedSystemFont(ofSize: size - 2, weight: .regular),
                    .foregroundColor: NSColor(calibratedRed: 0.75, green: 0.9, blue: 1, alpha: 1),
                    .backgroundColor: NSColor.white.withAlphaComponent(0.08),
                ])
                out.append(a)
            } else {
                out.append(markdown(part, size: size, color: color))
            }
        }
        let p = NSMutableParagraphStyle(); p.lineSpacing = 3
        out.addAttribute(.paragraphStyle, value: p, range: NSRange(location: 0, length: out.length))
        answerView.textStorage?.setAttributedString(out)
        answerView.scrollToBeginningOfDocument(nil)
    }

    private func markdown(_ text: String, size: CGFloat, color: NSColor) -> NSAttributedString {
        let attr: NSMutableAttributedString
        if let md = try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)) {
            attr = NSMutableAttributedString(attributedString: NSAttributedString(md))
        } else {
            attr = NSMutableAttributedString(string: text)
        }
        let full = NSRange(location: 0, length: attr.length)
        attr.addAttribute(.foregroundColor, value: color, range: full)
        attr.enumerateAttribute(.font, in: full) { value, range, _ in
            var f = NSFont.systemFont(ofSize: size)
            if let old = value as? NSFont {
                let traits = old.fontDescriptor.symbolicTraits
                if traits.contains(.monoSpace) { f = .monospacedSystemFont(ofSize: size - 1, weight: .regular) }
                else if traits.contains(.bold) { f = .boldSystemFont(ofSize: size) }
            }
            attr.addAttribute(.font, value: f, range: range)
        }
        // inline-markdown не знает про шрифт без атрибутов — проставим всем
        attr.enumerateAttribute(.font, in: full) { value, range, _ in
            if value == nil { attr.addAttribute(.font, value: NSFont.systemFont(ofSize: size), range: range) }
        }
        return attr
    }

    func setStatus(_ s: String) { status.stringValue = s }

    func setLevel(them: Float?, me: Float?) {
        func color(_ l: Float) -> CGColor { l > 0.08 ? NSColor.systemGreen.cgColor : NSColor.systemGray.withAlphaComponent(0.8).cgColor }
        if let them { themDot.layer?.backgroundColor = color(them) }
        if let me { meDot.layer?.backgroundColor = color(me) }
    }

    func scroll(by dy: CGFloat) {
        let clip = scroll.contentView
        var p = clip.bounds.origin
        p.y = max(0, p.y + dy)
        clip.scroll(to: p)
        scroll.reflectScrolledClipView(clip)
    }

    // MARK: ввод

    func focusInput() {
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(input)
    }

    func attach(_ img: NSImage) {
        attached = img
        thumb.image = img
        thumb.isHidden = false
        focusInput()
    }

    func clearAttachment() { attached = nil; thumb.image = nil; thumb.isHidden = true }

    @objc private func sendPressed() {
        let t = input.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty || attached != nil else { return }
        onSend?(t, attached)
        input.stringValue = ""
        clearAttachment()
    }

    @objc private func shotPressed() { onScreenshot?() }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy sel: Selector) -> Bool {
        if sel == #selector(NSResponder.cancelOperation(_:)) {
            if attached != nil { clearAttachment() } else { input.stringValue = ""; panel.resignKey() }
            return true
        }
        return false
    }
}
