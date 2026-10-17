import Foundation

/// Расшифровка разговора + решение «вопрос ли это» + запрос в OpenRouter с потоковым ответом.
final class Brain {
    enum Event {
        case thinking                // запрос ушёл
        case answer(String)          // текущий текст ответа (накопленный)
        case done(String)            // ответ закончен
        case skipped(String)         // реплика не вопрос
        case error(String)
    }
    var onEvent: ((Event) -> Void)?
    var paused = false

    private let key: String
    private var lines: [(who: String, text: String)] = []
    private var themBuf = ""
    private var meBuf = ""
    private var unanswered: [String] = []   // реплики собеседника после последней подсказки
    private var current: Task<Void, Never>?
    private let lock = NSLock()

    init(key: String) { self.key = key }

    static let systemPrompt = """
    Ты — незаметный помощник кандидата на онлайн-собеседовании. Тебе дают расшифровку разговора: «Собеседник» — интервьюер, «Я» — кандидат, которому ты помогаешь. Расшифровка автоматическая и может искажать слова — восстанавливай смысл по контексту.

    Твоя задача — дать кандидату ответ, который он сразу скажет своими словами.
    Правила ответа:
    - 3–5 предложений, разговорный русский; технические термины можно по-английски (JOIN, API, latency и т.п.).
    - Сразу суть. Без приветствий и обращений по имени, без «Отличный вопрос», без пересказа вопроса.
    - Если вопрос про SQL или код (даже устный: «как бы вы нашли…») — 1–2 предложения сути и короткий рабочий запрос/код в блоке ```.
    - Если вопрос про опыт, а профиля нет — дай структуру ответа с местами для подстановки в [квадратных скобках].
    - Если вопрос про опыт кандидата и есть профиль — опирайся на профиль, конкретные факты оттуда.
    - Не используй заголовки и длинные списки.
    """

    // MARK: расшифровка

    func themFinal(_ text: String, speechFinal: Bool) {
        lock.lock(); if !text.isEmpty { themBuf += (themBuf.isEmpty ? "" : " ") + text }; lock.unlock()
        if speechFinal { commitThem() }
    }
    func themUtteranceEnd() { commitThem() }

    func meFinal(_ text: String, speechFinal: Bool) {
        lock.lock()
        if !text.isEmpty { meBuf += (meBuf.isEmpty ? "" : " ") + text }
        if speechFinal, !meBuf.isEmpty { append(who: "Я", meBuf); meBuf = "" }
        lock.unlock()
    }
    func meUtteranceEnd() {
        lock.lock(); if !meBuf.isEmpty { append(who: "Я", meBuf); meBuf = "" }; lock.unlock()
    }

    private func append(who: String, _ text: String) {
        lines.append((who, text))
        if lines.count > 200 { lines.removeFirst(lines.count - 200) }
        Log.write("transcript \(who): \(text)")
    }

    private func commitThem() {
        lock.lock()
        let t = themBuf.trimmingCharacters(in: .whitespaces)
        themBuf = ""
        guard !t.isEmpty else { lock.unlock(); return }
        append(who: "Собеседник", t)
        let words = t.split(whereSeparator: { $0 == " " }).count
        if words >= 3 {
            unanswered.append(t)
            if unanswered.count > 4 { unanswered.removeFirst() }
        }
        let shouldAsk = words >= 3 && !paused
        let pending = unanswered
        lock.unlock()
        if shouldAsk { ask(auto: true, pending: pending, userText: nil, image: nil) }
    }

    private func transcriptText(maxChars: Int = 12000) -> String {
        var out: [String] = []
        var total = 0
        for l in lines.reversed() {
            let s = "\(l.who): \(l.text)"
            total += s.count
            if total > maxChars { break }
            out.append(s)
        }
        return out.reversed().joined(separator: "\n")
    }

    // MARK: запросы

    /// Ручной запрос из формы: текст и/или картинка.
    func askManual(text: String, image: Data?) {
        ask(auto: false, pending: [], userText: text, image: image)
    }

    private func ask(auto: Bool, pending: [String], userText: String?, image: Data?) {
        lock.lock()
        let transcript = transcriptText()
        lock.unlock()
        var system = Self.systemPrompt
        let profile = AppPaths.profileText()
        if !profile.isEmpty { system += "\n\nПрофиль кандидата:\n" + profile }

        var prompt = "Расшифровка разговора (последние минуты):\n" + (transcript.isEmpty ? "(пока пусто)" : transcript) + "\n\n"
        if auto {
            prompt += "Новые реплики собеседника, на которые ещё не было подсказки:\n«" + pending.joined(separator: " ") + "»\n\n"
            prompt += "Если в них есть вопрос или задание для кандидата (в том числе косвенное: «расскажите…», «а как бы вы…», «интересно, как вы…») — дай подсказку. "
            prompt += "Если это не вопрос (реплика-связка, рассказ о компании или о себе, ответ на вопрос кандидата, small talk, «угу, понятно») — ответь ровно одним словом SKIP и ничего больше."
        } else {
            let t = (userText ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if image != nil {
                prompt += "Кандидат прислал скриншот экрана" + (t.isEmpty ? ". Найди на нём задачу или вопрос (SQL, код, схема, текст) и дай решение." : " и пишет: «\(t)»")
            } else {
                prompt += "Кандидат пишет: «\(t)». Дай подсказку с учётом разговора."
            }
        }

        var content: Any = prompt
        if let image {
            content = [
                ["type": "text", "text": prompt],
                ["type": "image_url", "image_url": ["url": "data:image/jpeg;base64," + image.base64EncodedString()]],
            ]
        }
        let body: [String: Any] = [
            "model": Settings.model,
            "stream": true,
            "max_tokens": image != nil ? 900 : 450,
            "temperature": 0.3,
            "messages": [["role": "system", "content": system], ["role": "user", "content": content]],
        ]

        current?.cancel()
        current = Task { [weak self] in
            guard let self else { return }
            await self.stream(body: body, auto: auto)
        }
    }

    private func stream(body: [String: Any], auto: Bool) async {
        var req = URLRequest(url: URL(string: "https://openrouter.ai/api/v1/chat/completions")!)
        req.httpMethod = "POST"
        req.timeoutInterval = 60
        req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        if !auto { onEvent?(.thinking) }
        let started = Date()
        var text = ""
        var decided = !auto      // для авто-запроса сначала решаем: ответ или SKIP
        do {
            let (bytes, resp) = try await URLSession.shared.bytes(for: req)
            if let http = resp as? HTTPURLResponse, http.statusCode != 200 {
                var err = ""
                for try await l in bytes.lines { err += l; if err.count > 300 { break } }
                onEvent?(.error("OpenRouter \(http.statusCode): \(err.prefix(200))"))
                return
            }
            for try await line in bytes.lines {
                if Task.isCancelled { return }
                guard line.hasPrefix("data: ") else { continue }
                let payload = line.dropFirst(6)
                if payload == "[DONE]" { break }
                guard let d = payload.data(using: .utf8),
                      let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
                      let choices = j["choices"] as? [[String: Any]],
                      let delta = choices.first?["delta"] as? [String: Any],
                      let chunk = delta["content"] as? String, !chunk.isEmpty else { continue }
                text += chunk
                if !decided {
                    let head = text.trimmingCharacters(in: .whitespacesAndNewlines)
                    if head.count < 4 && "SKIP".hasPrefix(head.uppercased()) { continue }
                    if head.uppercased().hasPrefix("SKIP") {
                        Log.write("auto: SKIP")
                        onEvent?(.skipped(head))
                        return
                    }
                    decided = true
                    Log.write(String(format: "auto: answering, first token %.2fs", Date().timeIntervalSince(started)))
                    lock.withLock { unanswered.removeAll() }
                }
                onEvent?(.answer(text))
            }
            if Task.isCancelled { return }
            if decided && !text.isEmpty {
                Log.write(String(format: "answer done %.2fs: %@", Date().timeIntervalSince(started), text))
                onEvent?(.done(text))
            } else if !decided {
                onEvent?(.skipped(text))
            }
        } catch {
            if Task.isCancelled || (error as? URLError)?.code == .cancelled { return }
            onEvent?(.error(error.localizedDescription))
        }
    }

    func reset() {
        lock.lock(); lines.removeAll(); unanswered.removeAll(); themBuf = ""; meBuf = ""; lock.unlock()
        current?.cancel()
    }
}
