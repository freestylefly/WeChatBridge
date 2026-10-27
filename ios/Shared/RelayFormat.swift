import Foundation

/// Dates as the relay and the share receipt write them.
enum RelayFormat {
    /// `10月20日 09:00–09:07`; both ends dated when the range crosses a day.
    static func range(_ start: Date?, _ end: Date?, calendar: Calendar = .current) -> String? {
        guard let start else { return nil }
        let day = DateFormatter()
        day.locale = Locale(identifier: "zh_CN")
        day.dateFormat = "M月d日 HH:mm"
        let time = DateFormatter()
        time.dateFormat = "HH:mm"
        guard let end, end != start else { return day.string(from: start) }
        let tail = calendar.isDate(start, inSameDayAs: end) ? time.string(from: end) : day.string(from: end)
        return "\(day.string(from: start))–\(tail)"
    }

    /// `10月24日 00:19`.
    static func day(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "M月d日 HH:mm"
        return formatter.string(from: date)
    }
}
