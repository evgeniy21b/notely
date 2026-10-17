import Foundation

/// Контекст всей сессии (голос обеих сторон, текстовые вопросы, скриншоты, прошлые подсказки)
/// + решение «вопрос ли это» + потоковый запрос в OpenRouter.
final class Brain {
    enum Event {
        case thinking
        case answer(String)
        case done(String)
        case skipped(String)
        case error(String)
    }
    enum Kind { case them, me, typed, screen, hint }
    struct Entry { let time: Date; let kind: Kind; let text: String }

    var onEvent: ((Event) -> Void)?
    var paused = false

    private let key: String
    private var entries: [Entry] = []
    private var recentThem: [(Date, Set<String>)] = []   // фильтр эха динамиков
    private var themBuf = ""
    private var meBuf = ""
    private var unanswered: [String] = []
    private var lastShot: (time: Date, jpeg: Data)?
    private var summary = ""                 // конспект ранней части сессии
    private var summarizedUpTo = 0           // entries[..<summarizedUpTo] уже в конспекте
    private var summarizing = false
    // последние 12 минут — дословно (NOTELY_VERBATIM_SEC / NOTELY_MIN_VERBATIM — только для тестов)
    private static let verbatimWindow: TimeInterval = Double(ProcessInfo.processInfo.environment["NOTELY_VERBATIM_SEC"] ?? "") ?? 12 * 60
    private static let minVerbatimEntries = Int(ProcessInfo.processInfo.environment["NOTELY_MIN_VERBATIM"] ?? "") ?? 20
    private var current: Task<Void, Never>?
    private let lock = NSLock()
    private static let shotTTL: TimeInterval = 600       // скриншот живёт в контексте 10 минут

    init(key: String) { self.key = key }

    static let systemPrompt = """
    Ты — незаметный помощник кандидата на онлайн-собеседовании. Тебе дают разговор этой сессии: конспект начала и последние минуты дословно. «Собеседник» — интервьюер, «Я» — кандидат, которому ты помогаешь, «Я (написал тебе)» — вопрос кандидата текстом, «Скриншот» — момент, когда кандидат снял экран, «Твоя подсказка» — что ты уже подсказывал. Расшифровка автоматическая и может искажать слова — восстанавливай смысл по контексту.

    Держи в голове всю сессию: если возвращаются к задаче или теме, обсуждавшейся раньше (даже полчаса назад), опирайся на то, что уже было сказано, показано на экране и что ответил кандидат. Голос и экран — один контекст: собеседник может рассказывать задачу голосом и показывать её часть на экране.

    Твоя задача — дать кандидату ответ, который он сразу скажет своими словами.
    Правила ответа:
    - Лучше дать хоть какой-то ответ, чем никакого.
    - Простой вопрос: 3–5 предложений, разговорный русский; технические термины можно по-английски (JOIN, API, latency и т.п.).
    - Большой вопрос (кейс «был такой случай, как бы решили», проектирование, «расскажите подробно»): отвечай ПОРЦИЯМИ, чтобы кандидат начал говорить сразу. Первая порция — 1–2 фразы, с которых можно начать прямо сейчас. Дальше порции по 2–3 предложения, каждая с новой строки после пустой строки, в том порядке, в каком их произносить (уточнение условий → подход → шаги → риски/метрики). До 5 порций. Без вводных фраз вроде «Это кейс-вопрос» или «Вот как начать» — сразу первая порция.
    - Сразу суть. Без приветствий и обращений по имени, без «Отличный вопрос», без пересказа вопроса.
    - Если вопрос про SQL или код (даже устный: «как бы вы нашли…») — 1–2 предложения сути и короткий рабочий запрос/код в блоке ```. Используй реальные названия таблиц и полей, если они звучали или видны на экране.
    - Если вопрос про опыт кандидата и есть профиль — опирайся на профиль, конкретные факты оттуда. Если профиля нет — дай структуру ответа с местами для подстановки в [квадратных скобках].
    - Не используй заголовки и длинные списки.
    - Если вопрос в расшифровке выглядит обрезанным или с искажёнными словами — не проси переспросить: по контексту угадай самый вероятный вопрос и сразу отвечай на него.
    - Реплики «Я» могут содержать эхо слов собеседника (динамики) — не путай их с ответами кандидата.
    """

    // MARK: голос

    func themFinal(_ text: String, speechFinal: Bool) {
        lock.lock(); if !text.isEmpty { themBuf += (themBuf.isEmpty ? "" : " ") + text }; lock.unlock()
        if speechFinal { commitThem() }
    }
    func themUtteranceEnd() { commitThem() }

    func meFinal(_ text: String, speechFinal: Bool) {
        lock.lock()
        if !text.isEmpty { meBuf += (meBuf.isEmpty ? "" : " ") + text }
        if speechFinal { commitMe() }
        lock.unlock()
    }
    func meUtteranceEnd() { lock.lock(); commitMe(); lock.unlock() }

    private func add(_ kind: Kind, _ text: String) {
        entries.append(Entry(time: Date(), kind: kind, text: text))
        maybeSummarize()
        Log.write("context \(kind): \(text.prefix(300))")
    }

    /// Динамики: микрофон слышит собеседника. Реплику «Я», почти целиком из его недавних слов, выбрасываем.
    private func commitMe() {
        let t = meBuf.trimmingCharacters(in: .whitespaces)
        meBuf = ""
        guard !t.isEmpty else { return }
        let words = Self.words(t)
        recentThem.removeAll { Date().timeIntervalSince($0.0) > 25 }
        let them = recentThem.reduce(into: Set<String>()) { $0.formUnion($1.1) }
        if !words.isEmpty {
            let overlap = Double(words.filter { them.contains($0) }.count) / Double(words.count)
            if overlap >= 0.6 { Log.write("echo dropped (\(Int(overlap * 100))%): \(t)"); return }
        }
        add(.me, t)
    }

    static func words(_ s: String) -> [String] {
        s.lowercased().components(separatedBy: CharacterSet.letters.union(.decimalDigits).inverted).filter { $0.count > 1 }
    }

    private func commitThem() {
        lock.lock()
        let t = themBuf.trimmingCharacters(in: .whitespaces)
        themBuf = ""
        guard !t.isEmpty else { lock.unlock(); return }
        add(.them, t)
        recentThem.append((Date(), Set(Self.words(t))))
        let words = t.split(whereSeparator: { $0 == " " }).count
        if words >= 3 {
            unanswered.append(t)
            if unanswered.count > 12 { unanswered.removeFirst() }
        }
        let shouldAsk = words >= 3 && !paused
        let pending = unanswered
        lock.unlock()
        if shouldAsk { ask(auto: true, pending: pending, userText: nil, image: nil) }
    }

    // MARK: ручной ввод

    /// Текст и/или картинка из формы (или ⌃⌥S). Всё попадает в контекст сессии.
    func askManual(text: String, image: Data?) {
        lock.lock()
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let image {
            lastShot = (Date(), image)
            add(.screen, t.isEmpty ? "кандидат снял экран" : "кандидат снял экран и спросил: \(t)")
        } else if !t.isEmpty {
            add(.typed, t)
        }
        lock.unlock()
        ask(auto: false, pending: [], userText: t, image: image)
    }

    // MARK: сборка контекста

    private static let tf: DateFormatter = { let f = DateFormatter(); f.dateFormat = "HH:mm"; return f }()

    private func line(_ e: Entry) -> String {
        let t = Self.tf.string(from: e.time)
        switch e.kind {
        case .them: return "[\(t)] Собеседник: \(e.text)"
        case .me: return "[\(t)] Я: \(e.text)"
        case .typed: return "[\(t)] Я (написал тебе): \(e.text)"
        case .screen: return "[\(t)] Скриншот: \(e.text)"
        case .hint: return "[\(t)] Твоя подсказка: \(e.text)"
        }
    }

    /// (конспект ранней части, дословный свежий кусок). Конспект кэшируется у провайдера и меняется редко.
    private func contextParts() -> (archive: String, recent: String) {
        let recent = entries[min(summarizedUpTo, entries.count)...].map(line).joined(separator: "\n")
        return (summary, recent)
    }

    /// Всё старше 12 минут (но не последние 20 записей) сжимаем в конспект — в фоне, не мешая подсказкам.
    private func maybeSummarize() {
        guard !summarizing else { return }
        let now = Date()
        var cut = summarizedUpTo
        while cut < entries.count - Self.minVerbatimEntries, now.timeIntervalSince(entries[cut].time) > Self.verbatimWindow { cut += 1 }
        let chunk = entries[summarizedUpTo..<cut]
        guard chunk.count >= (Self.minVerbatimEntries < 20 ? 3 : 15) || chunk.reduce(0, { $0 + $1.text.count }) > 6000 else { return }
        summarizing = true
        let prev = summary
        let text = chunk.map(line).joined(separator: "\n")
        Log.write("summary: compressing \(chunk.count) entries")
        Task { [weak self] in
            guard let self else { return }
            let prompt = """
            Ты ведёшь конспект собеседования для помощника кандидата. Обнови конспект, добавив новый кусок разговора.
            Сохрани ТОЧНО: все задачи и вопросы с условиями (названия таблиц, поля, цифры, ограничения), что было на скриншотах, что ответил кандидат (суть), что подсказывал ассистент (суть, ключевой код/SQL — кратко), договорённости и открытые хвосты.
            Small talk выкинь. Пиши сжато, пунктами по темам, с временем [HH:MM]. До 600 слов.

            Текущий конспект:
            \(prev.isEmpty ? "(пусто)" : prev)

            Новый кусок разговора:
            \(text)
            """
            let body: [String: Any] = ["model": "anthropic/claude-haiku-4.5", "max_tokens": 1200, "temperature": 0.1,
                                       "messages": [["role": "user", "content": prompt]]]
            var req = URLRequest(url: URL(string: "https://openrouter.ai/api/v1/chat/completions")!)
            req.httpMethod = "POST"; req.timeoutInterval = 90
            req.setValue("Bearer \(self.key)", forHTTPHeaderField: "Authorization")
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try? JSONSerialization.data(withJSONObject: body)
            var newSummary: String?
            if let (data, _) = try? await URLSession.shared.data(for: req),
               let j = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let c = (j["choices"] as? [[String: Any]])?.first?["message"] as? [String: Any],
               let t = c["content"] as? String, !t.isEmpty { newSummary = t }
            self.lock.withLock {
                if let newSummary { self.summary = newSummary; self.summarizedUpTo = cut }
                self.summarizing = false
            }
            Log.write(newSummary != nil ? "summary: updated, \(newSummary!.count) chars, verbatim from #\(cut)" : "summary: failed, keep verbatim")
        }
    }

    // MARK: запрос

    private func ask(auto: Bool, pending: [String], userText: String?, image: Data?) {
        lock.lock()
        let (archive, recent) = contextParts()
        var shot: Data? = image
        if shot == nil, let ls = lastShot, Date().timeIntervalSince(ls.time) < Self.shotTTL { shot = ls.jpeg }
        let shotAge = lastShot.map { Int(Date().timeIntervalSince($0.time) / 60) } ?? 0
        lock.unlock()

        var system = Self.systemPrompt
        let profile = AppPaths.profileText()
        if !profile.isEmpty { system += "\n\nПрофиль кандидата:\n" + profile }

        var parts: [[String: Any]] = []
        if !archive.isEmpty {
            parts.append(["type": "text", "text": "Конспект начала сессии (раньше последних ~12 минут):\n" + archive,
                          "cache_control": ["type": "ephemeral"]])
        }
        var tail = (archive.isEmpty ? "Разговор с начала сессии:\n" : "Последние минуты разговора дословно:\n") + (recent.isEmpty ? "(пока пусто)" : recent) + "\n\n"
        if shot != nil {
            tail += image != nil
                ? "Ниже — скриншот, который кандидат снял прямо сейчас.\n"
                : "Ниже — последний скриншот экрана (снят \(shotAge) мин назад). Используй его, если вопрос связан с тем, что было на экране.\n"
        }
        if auto {
            tail += "Новые реплики собеседника, на которые ещё не было подсказки:\n«" + pending.joined(separator: " ") + "»\n\n"
            tail += "Если в них есть вопрос или задание для кандидата (в том числе косвенное: «расскажите…», «а как бы вы…», «давайте посмотрим на…») — дай подсказку с учётом всего разговора и экрана. "
            tail += "Если это не вопрос (реплика-связка, рассказ о компании или о себе, ответ на вопрос кандидата, small talk, «угу, понятно») — ответь ровно одним словом SKIP и ничего больше."
        } else if image != nil {
            let t = userText ?? ""
            tail += "Кандидат снял экран" + (t.isEmpty ? "." : " и пишет: «\(t)».")
            tail += " Свяжи то, что на экране, с тем, что сейчас обсуждают голосом (задача могла звучать голосом, а на экране — её часть: таблицы, схема, код). "
            tail += "Начни ответ строкой «Экран: …» (до 20 слов — что там показано), затем ответ или решение по правилам."
        } else {
            tail += "Кандидат пишет тебе: «\(userText ?? "")». Ответь с учётом всего разговора."
        }
        parts.append(["type": "text", "text": tail])
        if let shot {
            parts.append(["type": "image_url", "image_url": ["url": "data:image/jpeg;base64," + shot.base64EncodedString()]])
        }

        let body: [String: Any] = [
            "model": Settings.model,
            "stream": true,
            "max_tokens": 1000,
            "temperature": 0.3,
            "usage": ["include": true],
            "messages": [["role": "system", "content": system], ["role": "user", "content": parts]],
        ]
        current?.cancel()
        current = Task { [weak self] in await self?.stream(body: body, auto: auto) }
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
        var decided = !auto
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
                      let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { continue }
                if let usage = j["usage"] as? [String: Any] {
                    let cached = (usage["prompt_tokens_details"] as? [String: Any])?["cached_tokens"] ?? 0
                    Log.write("usage: prompt \(usage["prompt_tokens"] ?? "?"), cached \(cached), out \(usage["completion_tokens"] ?? "?")")
                }
                guard let choices = j["choices"] as? [[String: Any]],
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
                Log.write(String(format: "answer done %.2fs", Date().timeIntervalSince(started)))
                lock.withLock { add(.hint, String(text.prefix(1500))) }
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
        lock.lock()
        entries.removeAll(); unanswered.removeAll(); recentThem.removeAll(); lastShot = nil; themBuf = ""; meBuf = ""
        summary = ""; summarizedUpTo = 0
        lock.unlock()
        current?.cancel()
    }
}
