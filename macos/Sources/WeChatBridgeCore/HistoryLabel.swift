import Foundation

/// How a batch is named in the two places history is listed: the status menu's
/// 最近记录 submenu and the settings window's 记录 pane.
///
/// Pure string building, kept out of the app target so it can be tested: an
/// `NSMenu` item has no auto-truncation and no second line, so everything the
/// menu says about a batch has to be decided here and decided exactly.
public enum HistoryLabel {
    /// How much of a filename an `NSMenu` row can carry before the menu grows
    /// wider than the screen it drops out of.
    public static let menuNameLimit = 28

    /// 「聊天记录.zip」 or 「聊天记录.zip 等 3 个」.
    ///
    /// `limit` truncates the filename in the middle, which is what keeps the
    /// extension visible — two chat exports differ by their tail, not their
    /// head.
    public static func name(for batch: ReadyBatch, limit: Int? = nil) -> String {
        guard let first = batch.items.first else { return "" }
        let name = limit.map { middleTruncated(first.displayName, limit: $0) } ?? first.displayName
        guard batch.items.count > 1 else { return name }
        return L10n.format("%@ 等 %d 个", name, batch.items.count)
    }

    /// The main line of one 记录 row.
    ///
    /// A group name is the strongest context; a scene and then the filename are
    /// fallbacks. Keeping the hierarchy in the title leaves the supporting line
    /// free for what was sent and how big it was.
    public static func paneTitle(for batch: ReadyBatch) -> String {
        clean(batch.chatName) ?? clean(batch.sceneName) ?? name(for: batch)
    }

    /// The supporting line: scene or files, then how much disk they use.
    ///
    /// The destination is shown beside the outcome instead of here; repeating
    /// it in both places crowds the row without adding information.
    public static func paneSubtitle(for batch: ReadyBatch) -> String {
        var parts: [String] = []
        let chatName = clean(batch.chatName)
        let sceneName = clean(batch.sceneName)
        let title = paneTitle(for: batch)

        if let sceneName, title != sceneName {
            parts.append(sceneName)
            parts.append(fileCount(for: batch))
        } else if sceneName != nil {
            parts.append(fileCount(for: batch))
        } else if chatName != nil {
            if batch.items.count > 1 {
                parts.append(fileCount(for: batch))
            } else if let first = batch.items.first {
                parts.append(first.displayName)
            }
        } else if batch.items.count > 1 {
            parts.append(fileCount(for: batch))
        }

        parts.append(ByteFormat.string(batch.byteCount))
        return parts.filter { !$0.isEmpty }.joined(separator: " · ")
    }

    /// The app that received the latest attempt, without the verb. The record
    /// row already has a status beside it, so 「Codex · 已送达」 reads better
    /// than another complete sentence.
    public static func destinationName(for batch: ReadyBatch) -> String {
        clean(batch.targetName) ?? batch.action.targetDisplayName
    }

    /// Whether any of the facts a user can see on a record match every
    /// whitespace-separated search term. Querying title, subtitle and the
    /// underlying metadata together keeps the field honest about what it says
    /// it can find.
    public static func matches(_ batch: ReadyBatch, query: String) -> Bool {
        let terms = normalizeSearch(query)
            .split(whereSeparator: \.isWhitespace)
            .map(String.init)
        guard !terms.isEmpty else { return true }

        let searchable: [String?] = [
            paneTitle(for: batch),
            paneSubtitle(for: batch),
            batch.chatName,
            batch.sceneName,
            batch.targetName,
            batch.action.entryTitle,
            batch.action.targetDisplayName,
        ] + batch.items.map { Optional($0.displayName) }
        let haystack = normalizeSearch(searchable.compactMap { $0 }.joined(separator: "\n"))
        return terms.allSatisfy(haystack.contains)
    }

    /// The heading above one day's records.
    public static func sectionTitle(
        for date: Date,
        now: Date = Date(),
        locale: Locale = L10n.locale(),
        timeZone: TimeZone = .current
    ) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = locale
        calendar.timeZone = timeZone

        let day = calendar.startOfDay(for: date)
        let today = calendar.startOfDay(for: now)
        if day == today { return L10n.text("今天") }
        if let yesterday = calendar.date(byAdding: .day, value: -1, to: today),
           day == yesterday {
            return L10n.text("昨天")
        }

        var style = Date.FormatStyle(
            date: .omitted,
            time: .omitted,
            locale: locale,
            calendar: calendar,
            timeZone: timeZone
        )
        .month(.abbreviated)
        .day()
        if !calendar.isDate(date, equalTo: now, toGranularity: .year) {
            style = style.year()
        }
        return date.formatted(style)
    }

    /// 「14:32」 for today, 「9月4日 14:32」 for any other day, optionally with
    /// 今天 spelled out — the menu has the clock in the corner right above it,
    /// the settings pane does not.
    public static func timestamp(
        _ date: Date,
        now: Date = Date(),
        namesToday: Bool,
        locale: Locale = L10n.locale(),
        timeZone: TimeZone = .current
    ) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = locale
        calendar.timeZone = timeZone

        let time = date.formatted(
            Date.FormatStyle(
                date: .omitted,
                time: .shortened,
                locale: locale,
                calendar: calendar,
                timeZone: timeZone
            )
        )
        guard !calendar.isDate(date, inSameDayAs: now) else {
            return namesToday ? L10n.format("今天 %@", time) : time
        }
        // The abbreviated month, not the numeric one: zh-Hans renders "Md" as
        // "9/4", which reads as a fraction next to a clock time.
        let day = date.formatted(
            Date.FormatStyle(
                date: .omitted,
                time: .omitted,
                locale: locale,
                calendar: calendar,
                timeZone: timeZone
            )
            .month(.abbreviated)
            .day()
        )
        return "\(day) \(time)"
    }

    /// The clock alone, for a row already sitting under a day heading.
    public static func clockTime(
        _ date: Date,
        locale: Locale = L10n.locale(),
        timeZone: TimeZone = .current
    ) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = locale
        calendar.timeZone = timeZone
        return date.formatted(
            Date.FormatStyle(
                date: .omitted,
                time: .shortened,
                locale: locale,
                calendar: calendar,
                timeZone: timeZone
            )
        )
    }

    /// Where a batch went, in the words that were on screen when it was sent.
    ///
    /// 「发送到自定义」 is the name of an entry, not of a destination: on its own
    /// it would leave every custom forward in 记录 saying nothing about where
    /// the files actually went. So a batch that recorded a target names it.
    public static func destination(for batch: ReadyBatch) -> String {
        guard let name = batch.targetName, !name.isEmpty else { return batch.action.entryTitle }
        return L10n.format("发给 %@", name)
    }

    /// One 最近记录 row: 「聊天记录.zip 等 3 个 · 发给 Claude · 14:32」.
    public static func menuTitle(
        for batch: ReadyBatch,
        now: Date = Date(),
        locale: Locale = L10n.locale(),
        timeZone: TimeZone = .current
    ) -> String {
        [
            name(for: batch, limit: menuNameLimit),
            destination(for: batch),
            timestamp(batch.createdAt, now: now, namesToday: false, locale: locale, timeZone: timeZone),
        ]
        .filter { !$0.isEmpty }
        .joined(separator: " · ")
    }

    /// Counted in characters rather than bytes: this is a display width problem,
    /// and one Chinese character is one glyph however many bytes it takes.
    static func middleTruncated(_ text: String, limit: Int) -> String {
        guard limit > 1, text.count > limit else { return text }
        let keep = limit - 1
        let tail = keep / 2
        return "\(text.prefix(keep - tail))…\(text.suffix(tail))"
    }

    private static func fileCount(for batch: ReadyBatch) -> String {
        batch.items.count == 1
            ? L10n.text("1 个文件")
            : L10n.format("%d 个文件", batch.items.count)
    }

    private static func clean(_ value: String?) -> String? {
        guard let value else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    private static func normalizeSearch(_ value: String) -> String {
        value
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: L10n.locale())
            .lowercased()
    }
}
