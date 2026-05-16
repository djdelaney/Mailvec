// FastmailLink.swift
//
// Builds a Fastmail webmail deep-link from a stored RFC Message-ID. The
// server-side WebmailLinkBuilder returns null when Fastmail__AccountId
// isn't configured in the launchd plist's environment — for a tray app we
// want the link to work whether or not the user has set their account ID,
// so we mirror the same URL format here and treat the account ID as
// optional. Fastmail's web UI redirects to the user's default account
// when ?u= is omitted, which is fine for the common single-account case.
import AppKit
import Foundation

enum FastmailLink {
    /// Construct an `app.fastmail.com/mail/search:msgid:...` URL for the
    /// given RFC Message-ID. Returns nil only when the message-id is empty
    /// (the only thing we can't recover from).
    static func url(forMessageId messageId: String) -> URL? {
        guard !messageId.isEmpty else { return nil }
        let encoded = messageId.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? messageId
        // Allow override via @AppStorage so a future Preferences toggle can
        // point at app.fastmail.com vs a Fastmail Workspace tenant.
        let base = (UserDefaults.standard.string(forKey: "fastmailWebUrl")?.trimmingCharacters(in: .whitespaces))
            .flatMap { $0.isEmpty ? nil : $0 } ?? "https://app.fastmail.com"
        let accountId = (UserDefaults.standard.string(forKey: "fastmailAccountId") ?? "")
            .trimmingCharacters(in: .whitespaces)
        var components = "\(base.trimmingTrailingSlash())/mail/search:msgid:\(encoded)"
        if !accountId.isEmpty {
            let encId = accountId.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? accountId
            components += "?u=\(encId)"
        }
        return URL(string: components)
    }

    /// Convenience wrapper for SearchHit / EmailDetail that has the
    /// messageId already in scope.
    static func open(messageId: String) {
        guard let url = url(forMessageId: messageId) else { return }
        NSWorkspace.shared.open(url)
    }
}

private extension String {
    func trimmingTrailingSlash() -> String {
        hasSuffix("/") ? String(dropLast()) : self
    }
}
