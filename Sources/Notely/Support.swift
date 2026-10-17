import Foundation
import Security

/// Ключи API лежат в связке ключей macOS (service notely-deepgram / notely-openrouter).
enum Keychain {
    static func get(_ service: String) -> String? {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data,
              let s = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !s.isEmpty else { return nil }
        return s
    }
}

enum Log {
    static let url: URL = AppPaths.dir.appendingPathComponent("notely.log")
    static func write(_ msg: String) {
        let line = "\(ISO8601DateFormatter().string(from: Date())) \(msg)\n"
        FileHandle.standardError.write(line.data(using: .utf8)!)
        if let h = try? FileHandle(forWritingTo: url) {
            h.seekToEndOfFile(); h.write(line.data(using: .utf8)!); try? h.close()
        } else {
            try? line.data(using: .utf8)!.write(to: url)
        }
    }
}

enum AppPaths {
    static let dir: URL = {
        let d = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Notely", isDirectory: true)
        try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        return d
    }()
    static var profile: URL { dir.appendingPathComponent("profile.md") }

    static let profileTemplate = """
    # Профиль для подсказок
    <!-- Заполни перед собеседованием. Всё внутри таких скобок-комментариев в модель НЕ уходит. -->

    ## Резюме
    <!-- Вставь сюда своё резюме: опыт, проекты, стек, цифры. -->

    ## Вакансия
    <!-- Вставь описание вакансии и компании. -->

    ## Как отвечать
    <!-- Например: держать уровень middle+, не врать про то, чего не делал. -->
    """

    static func ensureProfile() {
        if !FileManager.default.fileExists(atPath: profile.path) {
            try? profileTemplate.write(to: profile, atomically: true, encoding: .utf8)
        }
    }

    /// Профиль без HTML-комментариев и пустых заголовков. Пустой шаблон = пустая строка.
    static func profileText() -> String {
        guard var s = try? String(contentsOf: profile, encoding: .utf8) else { return "" }
        while let r = s.range(of: "<!--"), let e = s.range(of: "-->", range: r.upperBound..<s.endIndex) {
            s.removeSubrange(r.lowerBound..<e.upperBound)
        }
        let meaningful = s.split(separator: "\n").filter {
            let t = $0.trimmingCharacters(in: .whitespaces)
            return !t.isEmpty && !t.hasPrefix("#")
        }
        return meaningful.isEmpty ? "" : s.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

enum Settings {
    static let models: [(id: String, title: String)] = [
        ("anthropic/claude-haiku-4.5", "Claude Haiku 4.5"),
        ("google/gemini-2.5-flash", "Gemini 2.5 Flash (быстрее)"),
        ("anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5 (умнее, медленнее)"),
    ]
    static var model: String {
        get { UserDefaults.standard.string(forKey: "model") ?? models[0].id }
        set { UserDefaults.standard.set(newValue, forKey: "model") }
    }
    static var fontSize: CGFloat {
        get { let v = UserDefaults.standard.double(forKey: "fontSize"); return v > 0 ? v : 17 }
        set { UserDefaults.standard.set(Double(newValue), forKey: "fontSize") }
    }
    static var widthFraction: CGFloat {
        get { let v = UserDefaults.standard.double(forKey: "widthFraction"); return v > 0 ? v : 0.5 }
        set { UserDefaults.standard.set(Double(newValue), forKey: "widthFraction") }
    }
}
