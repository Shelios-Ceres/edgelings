import Foundation

/// One-click Codex account switching (owner call, 2026-08-08).
///
/// Codex keeps exactly one signed-in account in `~/.codex/auth.json`. This
/// parks a COPY of each account's credentials beside it and swaps the live
/// file on demand — the same thing a person does by hand with `mv`, minus
/// the mistakes. Two modes:
///   - MANUAL — the user picks from the menu, the file swaps.
///   - AUTO (opt-in, off by default) — when the live account's window
///     reads 100%, rotate to the next parked account. Modeled on the
///     open-source codex-auto pool tools, but keyed off the real
///     /wham/usage percentages instead of scraping terminal output for
///     failure text, so it never guesses. `UsageStore` owns the trigger
///     (it sees every fresh reading); this type owns policy + the swap.
enum CodexAccountSwitcher {
    /// A parked account: the label the user gave it plus its stored file.
    struct Account: Identifiable, Equatable {
        var id: String { label }
        let label: String
        let path: String
        /// Account identity read from the stored credentials, when present.
        let email: String?
    }

    private static var codexHome: String { NSString("~/.codex").expandingTildeInPath }
    /// Match the account and user, not tokens that Codex refreshes in place.
    static func activeLabel(in home: String = codexHome) -> String? {
        guard let live = try? Data(contentsOf: URL(fileURLWithPath: home + "/auth.json")) else { return nil }
        return accounts(in: home).first { parked in
            guard let stored = try? Data(contentsOf: URL(fileURLWithPath: parked.path)) else { return false }
            return sameAccount(stored, live)
        }?.label
    }

    static func accounts(in home: String = codexHome) -> [Account] {
        let fm = FileManager.default
        let storeDir = home + "/agentisland-accounts"
        guard let names = try? fm.contentsOfDirectory(atPath: storeDir) else { return [] }
        return names
            .filter { $0.hasSuffix(".json") }
            .sorted()
            .map { name in
                let path = storeDir + "/" + name
                return Account(
                    label: String(name.dropLast(5)),
                    path: path,
                    email: email(inFileAt: path)
                )
            }
    }

    /// Park the CURRENT login under `label`, so it can be switched back to.
    @discardableResult
    static func parkCurrent(as label: String, in home: String = codexHome) -> Bool {
        guard let live = try? Data(contentsOf: URL(fileURLWithPath: home + "/auth.json")) else { return false }
        return park(live, as: label, in: home)
    }

    private static func park(_ data: Data, as label: String, in home: String) -> Bool {
        let clean = sanitize(label)
        guard !clean.isEmpty else { return false }
        let fm = FileManager.default
        let storeDir = home + "/agentisland-accounts"
        do {
            try fm.createDirectory(atPath: storeDir, withIntermediateDirectories: true,
                                   attributes: [.posixPermissions: 0o700])
            let target = URL(fileURLWithPath: storeDir + "/" + clean + ".json")
            try write(data, to: target.path)
            return true
        } catch {
            NSLog("AgentIsland: could not park Codex account — %@", error.localizedDescription)
            return false
        }
    }

    /// Save the exact outgoing credentials before replacing the live login.
    @discardableResult
    static func activate(_ account: Account, in home: String = codexHome) -> Bool {
        guard inStore(account.path, home: home),
              let data = try? Data(contentsOf: URL(fileURLWithPath: account.path)) else { return false }
        let livePath = home + "/auth.json"
        do {
            let live = try readIfPresent(livePath)
            if let live {
                // Selecting the current account must never restore its older tokens.
                if sameAccount(live, data) { return true }
                let matching = accounts(in: home).filter { parked in
                    guard let stored = try? Data(contentsOf: URL(fileURLWithPath: parked.path)) else { return false }
                    return sameAccount(stored, live)
                }
                if matching.isEmpty {
                    let suffix = UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(30)
                    guard park(live, as: "previous-\(suffix)", in: home) else { return false }
                } else {
                    // Refresh every alias, so a later rotation cannot select a stale copy.
                    for parked in matching {
                        if try Data(contentsOf: URL(fileURLWithPath: parked.path)) != live {
                            try write(live, to: parked.path)
                        }
                    }
                }
            }
            // A CLI refresh or another switch invalidates the saved snapshot.
            guard try readIfPresent(livePath) == live else { return false }
            try write(data, to: livePath)
            return true
        } catch {
            NSLog("AgentIsland: could not activate Codex account — %@", error.localizedDescription)
            return false
        }
    }

    // MARK: - Auto-switch (opt-in)

    private static let autoSwitchKey = "codexAutoSwitchWhenExhausted"

    /// Off by default: silently swapping logins surprises anyone who did
    /// not ask for it, and a mid-session file swap is visible to a running
    /// Codex CLI. The owner flips it on once and forgets it.
    static var autoSwitchEnabled: Bool {
        get { UserDefaults.standard.bool(forKey: autoSwitchKey) }
        set { UserDefaults.standard.set(newValue, forKey: autoSwitchKey) }
    }

    /// The next account worth rotating to: parked, not the live one, not
    /// yet tried this exhaustion episode. Stable label order, so rotation
    /// walks the pool deterministically instead of ping-ponging.
    static func rotationCandidate(excluding tried: Set<String>, in home: String = codexHome) -> Account? {
        do {
            let live = try readIfPresent(home + "/auth.json")
            let saved = accounts(in: home)
            let attempted = saved.filter { tried.contains($0.label) }.compactMap {
                try? Data(contentsOf: URL(fileURLWithPath: $0.path))
            }
            return saved.first { account in
                guard !tried.contains(account.label),
                      let stored = try? Data(contentsOf: URL(fileURLWithPath: account.path)) else { return false }
                if let live, sameAccount(stored, live) { return false }
                return !attempted.contains { sameAccount(stored, $0) }
            }
        } catch { return nil }
    }

    @discardableResult
    static func forget(_ account: Account) -> Bool {
        guard inStore(account.path, home: codexHome) else { return false }
        return (try? FileManager.default.removeItem(atPath: account.path)) != nil
    }

    // MARK: - Helpers (unit-tested)

    private struct Identity: Equatable {
        let account: String
        let user: String?
    }

    static func sameAccount(_ first: Data, _ second: Data) -> Bool {
        if first == second { return true }
        guard let firstIdentity = identity(in: first) else { return false }
        return firstIdentity == identity(in: second)
    }

    private static func identity(in data: Data) -> Identity? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tokens = root["tokens"] as? [String: Any] else { return nil }
        var account = nonEmpty(tokens["account_id"])
        var user: String?
        var subject: String?
        for key in ["id_token", "access_token"] {
            guard let token = tokens[key] as? String else { continue }
            let parts = token.split(separator: ".", omittingEmptySubsequences: false)
            guard parts.count == 3 else { continue }
            var payload = String(parts[1]).replacingOccurrences(of: "-", with: "+")
                .replacingOccurrences(of: "_", with: "/")
            payload += String(repeating: "=", count: (4 - payload.count % 4) % 4)
            guard let decoded = Data(base64Encoded: payload),
                  let claims = try? JSONSerialization.jsonObject(with: decoded) as? [String: Any] else { continue }
            subject = subject ?? nonEmpty(claims["sub"])
            if let auth = claims["https://api.openai.com/auth"] as? [String: Any] {
                account = account ?? nonEmpty(auth["chatgpt_account_id"])
                user = user ?? nonEmpty(auth["chatgpt_user_id"])
            }
        }
        // Email alone is not an identity: one user may have several workspaces.
        return account.map { Identity(account: $0, user: user ?? subject) }
    }

    private static func nonEmpty(_ value: Any?) -> String? {
        guard let text = value as? String, !text.isEmpty else { return nil }
        return text
    }

    private static func inStore(_ path: String, home: String) -> Bool {
        let parent = URL(fileURLWithPath: path).standardizedFileURL.deletingLastPathComponent()
        let store = URL(fileURLWithPath: home + "/agentisland-accounts").standardizedFileURL
        return parent.path == store.path
    }

    private static func readIfPresent(_ path: String) throws -> Data? {
        do { return try Data(contentsOf: URL(fileURLWithPath: path)) }
        catch let error as CocoaError where error.code == .fileReadNoSuchFile { return nil }
    }

    private static func write(_ data: Data, to path: String) throws {
        try data.write(to: URL(fileURLWithPath: path), options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
    }

    /// Labels become filenames: keep it to safe characters so a stray "/"
    /// or ".." can never escape the store directory.
    static func sanitize(_ raw: String) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_ "))
        return String(String.UnicodeScalarView(
            raw.unicodeScalars.filter { allowed.contains($0) }
        )).trimmingCharacters(in: .whitespaces).prefix(40).description
    }

    /// Codex writes the account email under `tokens.account_id`-adjacent
    /// fields; read the first email-shaped string so the picker can show who
    /// each parked login belongs to. Never logs the token itself.
    static func email(inJSON object: [String: Any]) -> String? {
        for (_, value) in object {
            if let string = value as? String, string.contains("@"), !string.contains(" ") {
                return string
            }
            if let nested = value as? [String: Any], let found = email(inJSON: nested) {
                return found
            }
        }
        return nil
    }

    private static func email(inFileAt path: String) -> String? {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return email(inJSON: object)
    }
}
