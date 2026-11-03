// Where a Chinese pixel line may break (windows/notes/pixel-zh-20261102/SPEC.md,
// "Wrapping"): between any two Han characters, but never before a closing mark
// or after an opening one, never inside a Latin word, a number with its unit
// ("2 小时", "31.1M") or a command ("codex login"). A line without Han
// characters is left to Core Text's own rules, so English lines wrap as before.
import Foundation

enum PixelBreaks {
    /// Marks that never start a line.
    private static let closing: Set<Unicode.Scalar> = [
        "，", "。", "、", "：", "；", "！", "？", "）", "」", "』", "】", "》", "〉", "”", "’", "…", "—", "～",
        ",", ".", ":", ";", "!", "?", ")", "]", "}",
    ]
    /// Marks that never end a line.
    private static let opening: Set<Unicode.Scalar> = ["（", "「", "『", "【", "《", "〈", "“", "‘", "(", "[", "{"]
    /// Units that follow a number after a space ("5 小时"): the number and its unit stay together.
    private static let units = ["小时", "分钟", "毫秒", "秒", "分", "天", "周", "个", "次", "条", "张", "项", "页", "月"]
    /// Command words: a command's words stay together ("codex login", "claude /login").
    private static let commandWords: Set<String> = ["codex", "claude", "kimi", "grok", "login"]

    /// A character the Chinese rules govern: an ideograph, or wide punctuation.
    static func isWide(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x2E80...0x9FFF, 0xF900...0xFAFF, 0x20000...0x2FA1F: return true
        case 0x3000...0x303F, 0xFF00...0xFFEF: return true
        case 0x2014, 0x2026, 0x201C, 0x201D, 0x2018, 0x2019: return true
        default: return false
        }
    }

    static func hasWide(_ text: String) -> Bool { text.unicodeScalars.contains(where: isWide) }

    /// The UTF-16 offsets (in `text`) where a line may end, the end of the text included.
    static func opportunities(_ text: String) -> [Int] {
        var scalars: [Unicode.Scalar] = []
        var offsets: [Int] = []
        var offset = 0
        for scalar in text.unicodeScalars {
            scalars.append(scalar)
            offsets.append(offset)
            offset += scalar.value > 0xFFFF ? 2 : 1
        }
        offsets.append(offset)
        var result: [Int] = []
        guard scalars.count > 1 else { return [offset] }
        for index in 1..<scalars.count where allowed(before: index, scalars) { result.append(offsets[index]) }
        result.append(offset)
        return result
    }

    /// Whether a line may end between scalars[index - 1] and scalars[index].
    private static func allowed(before index: Int, _ scalars: [Unicode.Scalar]) -> Bool {
        let previous = scalars[index - 1], next = scalars[index]
        if previous == "\n" { return true }
        if next == " " || next == "\n" { return false }
        if closing.contains(next) || opening.contains(previous) { return false }
        // The pair of an ellipsis or a dash stays whole.
        if previous == next && (previous == "…" || previous == "—") { return false }
        if previous == " " { return !glued(after: index, scalars) }
        let wide = isWide(previous) || isWide(next)
        if !wide { return false }
        // Inside a unit ("小|时"), or between a digit and its unit ("5|小时").
        if insideUnit(at: index, scalars) { return false }
        if isDigit(previous), unit(at: index, scalars) != nil { return false }
        return true
    }

    /// A break after the space before scalars[index] would split a number from its unit, or a command.
    private static func glued(after index: Int, _ scalars: [Unicode.Scalar]) -> Bool {
        let before = word(endingAt: index - 2, scalars)
        if let last = before.unicodeScalars.last, isDigit(last), unit(at: index, scalars) != nil { return true }
        let after = word(startingAt: index, scalars)
        if commandWords.contains(before.lowercased()) && (after.hasPrefix("/") || commandWords.contains(after.lowercased())) { return true }
        return false
    }

    private static func word(endingAt end: Int, _ scalars: [Unicode.Scalar]) -> String {
        guard end >= 0 else { return "" }
        var start = end
        while start > 0, !isSeparator(scalars[start - 1]) { start -= 1 }
        return String(String.UnicodeScalarView(scalars[start...end]))
    }

    private static func word(startingAt start: Int, _ scalars: [Unicode.Scalar]) -> String {
        var end = start
        while end < scalars.count, !isSeparator(scalars[end]) { end += 1 }
        return String(String.UnicodeScalarView(scalars[start..<end]))
    }

    private static func isDigit(_ scalar: Unicode.Scalar) -> Bool { ("0"..."9").contains(scalar) }

    private static func isSeparator(_ scalar: Unicode.Scalar) -> Bool { scalar == " " || scalar == "\n" || isWide(scalar) }

    /// The unit that starts at scalars[index], if one does.
    private static func unit(at index: Int, _ scalars: [Unicode.Scalar]) -> String? {
        for candidate in units {
            let candidateScalars = Array(candidate.unicodeScalars)
            guard index + candidateScalars.count <= scalars.count else { continue }
            if Array(scalars[index..<(index + candidateScalars.count)]) == candidateScalars { return candidate }
        }
        return nil
    }

    /// scalars[index - 1] and scalars[index] are the two characters of one unit.
    private static func insideUnit(at index: Int, _ scalars: [Unicode.Scalar]) -> Bool {
        guard index >= 1 else { return false }
        for candidate in units where candidate.unicodeScalars.count > 1 {
            let candidateScalars = Array(candidate.unicodeScalars)
            for split in 1..<candidateScalars.count {
                let start = index - split
                guard start >= 0, start + candidateScalars.count <= scalars.count else { continue }
                if Array(scalars[start..<(start + candidateScalars.count)]) == candidateScalars { return true }
            }
        }
        return false
    }

    /// The offsets just after each " · " separator: a line of "label value · label value" breaks there
    /// before it breaks inside a phrase, so a value never parts from its label (Windows' PhraseFlow).
    static func phraseEnds(_ text: String) -> [Int] {
        let ns = text as NSString
        var result: [Int] = []
        var from = 0
        while from < ns.length {
            let found = ns.range(of: " · ", options: [], range: NSRange(location: from, length: ns.length - from))
            if found.location == NSNotFound { break }
            result.append(found.location + found.length)
            from = found.location + found.length
        }
        return result
    }

    /// Where the line that starts at `start` ends, given the room Core Text left (`suggested`, a count).
    /// A phrase boundary comes first, then an allowed break (Chinese lines only); with no better place the
    /// suggestion stands: a word longer than a line breaks inside. A last line of fewer than two Han
    /// characters (a character and its mark) is avoided by carrying one more character down.
    static func lineEnd(start: Int, suggested: Int, length: Int, opportunities: [Int]?, phrases: [Int] = [], text: NSString? = nil) -> Int {
        let limit = start + suggested
        if limit >= length { return length }
        var end = limit
        if let phrase = phrases.last(where: { $0 > start && $0 <= limit }) { end = phrase }
        else if let allowed = opportunities?.last(where: { $0 > start && $0 <= limit }) { end = allowed }
        if let text, let opportunities, end < length, length - end <= 3 {
            let rest = text.substring(from: end).unicodeScalars
            let han = rest.filter { isWide($0) && !closing.contains($0) && !opening.contains($0) }.count
            if han < 2, rest.contains(where: isWide), let earlier = opportunities.last(where: { $0 > start && $0 < end }) { end = earlier }
        }
        return end
    }

    /// Lines with the break offsets that must and must not be offered, for the self-test.
    /// Whole-line cases: the text, the room Core Text would leave (a count), and where the line must end.
    static let lines: [(text: String, room: Int, end: Int, name: String)] = [
        ("Kimi Code 30% · Kimi 应用 14%", 25, 16, "a value stays with its label"),
        ("上下文生效。", 4, 3, "no last line of one character and its mark"),
        ("等你批准后继续运行", 4, 4, "a long enough last line is left alone"),
    ]

    static let samples: [(text: String, allowed: [Int], forbidden: [Int], name: String)] = [
        ("额度，已用", [1], [2], "none before a closing mark"),
        ("（额度）", [], [1, 3], "none after an opening mark"),
        ("剩余 5 小时额度", [3, 7], [4, 5, 6], "a number keeps its unit"),
        ("运行 codex login 登录", [3], [9], "a command stays whole"),
        ("Kimi 应用", [5], [1, 2, 3], "none inside a Latin word"),
        ("等你批准……再说", [6], [4, 5], "an ellipsis stays whole"),
    ]
}
