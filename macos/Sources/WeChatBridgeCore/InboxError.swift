import Foundation

public enum InboxError: Error, LocalizedError, Equatable {
    /// The sandbox refused the group container. In practice this means the
    /// entitlement is missing or does not match the signature, which is a build
    /// problem rather than something the user can fix.
    case containerUnavailable(identifier: String)
    case unsupportedAttachment(types: [String])
    case emptyShare
    case manifestUnreadable(reason: String)

    public var errorDescription: String? {
        switch self {
        case .containerUnavailable(let identifier):
            return L10n.format("无法访问共享容器 %@。", identifier)
        case .unsupportedAttachment(let types):
            let list = types.isEmpty ? L10n.text("未知类型") : types.joined(separator: ", ")
            return L10n.format("这项内容不是 WeChatBridge 能保存的文件（%@）。", list)
        case .emptyShare:
            return L10n.text("这次分享没有包含任何文件。")
        case .manifestUnreadable(let reason):
            return L10n.format("批次清单无法读取：%@", reason)
        }
    }
}
