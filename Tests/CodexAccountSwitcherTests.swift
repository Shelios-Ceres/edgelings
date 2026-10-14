import Foundation

private enum TestFailure: Error { case assertion(String) }

private func expect(_ condition: Bool, _ message: String) throws {
    if !condition { throw TestFailure.assertion(message) }
}

private func credential(_ account: String, _ generation: String, user: String = "user-one") throws -> Data {
    let claims: [String: Any] = [
        "sub": user,
        "https://api.openai.com/auth": ["chatgpt_account_id": account, "chatgpt_user_id": user],
        "exp": generation,
    ]
    let payload = try JSONSerialization.data(withJSONObject: claims).base64EncodedString()
        .replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-")
        .replacingOccurrences(of: "/", with: "_")
    return try JSONSerialization.data(withJSONObject: [
        "email": "same@example.invalid",
        "tokens": ["account_id": account, "id_token": "e30.\(payload).fake",
                   "access_token": "FAKE-ACCESS-\(generation)", "refresh_token": "FAKE-REFRESH-\(generation)"],
        "last_refresh": generation,
    ])
}

private func testIdentity() throws {
    let old = try credential("team", "old")
    let fresh = try credential("team", "fresh")
    try expect(CodexAccountSwitcher.sameAccount(old, fresh), "refresh changed identity")
    try expect(!CodexAccountSwitcher.sameAccount(old, credential("personal", "fresh")), "same email merged workspaces")
    try expect(!CodexAccountSwitcher.sameAccount(old, credential("team", "fresh", user: "user-two")), "same workspace merged users")
    try expect(!CodexAccountSwitcher.sameAccount(Data("broken".utf8), fresh), "invalid JSON guessed identity")
}

private func testSwitching(in home: String) throws {
    let fm = FileManager.default
    let store = home + "/agentisland-accounts"
    try fm.createDirectory(atPath: store, withIntermediateDirectories: true)
    let live = URL(fileURLWithPath: home + "/auth.json")
    let a = CodexAccountSwitcher.Account(label: "a", path: store + "/a.json", email: nil)
    let alias = CodexAccountSwitcher.Account(label: "b-alias", path: store + "/b-alias.json", email: nil)
    let other = CodexAccountSwitcher.Account(label: "c-other", path: store + "/c-other.json", email: nil)
    let fresh = try credential("current", "fresh")
    try credential("current", "old").write(to: URL(fileURLWithPath: a.path))
    try credential("current", "older").write(to: URL(fileURLWithPath: alias.path))
    try credential("other", "saved").write(to: URL(fileURLWithPath: other.path))
    try fresh.write(to: live)
    try expect(CodexAccountSwitcher.activeLabel(in: home) == "a", "refreshed live account was not recognized")
    try expect(CodexAccountSwitcher.rotationCandidate(excluding: [], in: home)?.label == "c-other", "rotation chose a current alias")
    try expect(CodexAccountSwitcher.activate(a, in: home), "same-account selection failed")
    try expect(Data(contentsOf: live) == fresh, "same-account selection rolled tokens back")
    try expect(CodexAccountSwitcher.activate(other, in: home), "switch failed")
    try expect(Data(contentsOf: URL(fileURLWithPath: a.path)) == fresh, "outgoing fresh tokens not preserved")
    try expect(Data(contentsOf: URL(fileURLWithPath: alias.path)) == fresh, "alias retained old tokens")
    try expect(CodexAccountSwitcher.rotationCandidate(excluding: ["a"], in: home) == nil, "rotation retried an exhausted alias")
    try expect(CodexAccountSwitcher.activate(a, in: home), "switch back failed")
    try expect(Data(contentsOf: live) == fresh, "switch back restored old tokens")

    let unsaved = try credential("unsaved", "fresh")
    try unsaved.write(to: live)
    try expect(CodexAccountSwitcher.activate(other, in: home), "unsaved outgoing backup failed")
    let copies = CodexAccountSwitcher.accounts(in: home).filter {
        (try? Data(contentsOf: URL(fileURLWithPath: $0.path))) == unsaved
    }
    try expect(copies.count == 1, "unsaved outgoing login was not preserved exactly once")
}

private func testBackupFailure(in home: String) throws {
    let fm = FileManager.default
    let store = home + "/agentisland-accounts"
    try fm.createDirectory(atPath: store, withIntermediateDirectories: true)
    let incoming = CodexAccountSwitcher.Account(label: "incoming", path: store + "/incoming.json", email: nil)
    try credential("incoming", "saved").write(to: URL(fileURLWithPath: incoming.path))
    let outgoing = try credential("unsaved", "fresh")
    let live = URL(fileURLWithPath: home + "/auth.json")
    try outgoing.write(to: live)
    try fm.setAttributes([.posixPermissions: 0o500], ofItemAtPath: store)
    defer { try? fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: store) }
    try expect(!CodexAccountSwitcher.activate(incoming, in: home), "backup failure did not stop activation")
    try expect(Data(contentsOf: live) == outgoing, "backup failure replaced the current login")
}

@main
private enum CodexAccountSwitcherTestRunner {
    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("AgentIslandAccounts-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try testIdentity()
        try testSwitching(in: root.appendingPathComponent("switching").path)
        try testBackupFailure(in: root.appendingPathComponent("failure").path)
        print("CodexAccountSwitcherTests GREEN")
    }
}
