// WebmailLink.swift
//
// Provider-aware webmail deep-link builder. Detects which webmail provider
// the user is on (from their IMAP host as reported by /tray/system) and
// dispatches URL construction accordingly. When the provider is unknown
// (any IMAP host we don't have a URL scheme for), `url(...)` returns nil
// and callers hide the "Open in …" affordance entirely.
//
// Today only Fastmail has a known URL scheme. Mailvec is primarily a
// Fastmail tool but the project scope says "Fastmail (or any IMAP)", so
// the rest of the app has to gracefully degrade for non-Fastmail users.
// Adding a new provider means: extend the `WebmailProvider` enum, teach
// `detect` to recognise the IMAP host(s), and add a URL builder branch
// in `WebmailLink.url`.
import AppKit
import Foundation

enum WebmailProvider: Equatable {
    case fastmail
    case unknown

    /// Map an IMAP host to a webmail provider. Case-insensitive. The
    /// Fastmail check covers both their current `imap.fastmail.com` and
    /// the legacy `imap.messagingengine.com` host that older accounts
    /// still resolve to.
    static func detect(imapHost: String?) -> WebmailProvider {
        guard let host = imapHost?.lowercased(), !host.isEmpty, host != "—" else {
            return .unknown
        }
        if host.contains("fastmail.com") || host.contains("messagingengine.com") {
            return .fastmail
        }
        return .unknown
    }

    /// User-facing label used in button titles ("Open in Fastmail",
    /// "↩ Open in Fastmail"). nil for unknown providers — callers hide
    /// the affordance entirely rather than showing "Open in (unknown)".
    var displayName: String? {
        switch self {
        case .fastmail: return "Fastmail"
        case .unknown:  return nil
        }
    }
}

enum WebmailLink {
    /// Build a webmail deep-link URL for the given RFC Message-ID, scoped
    /// to the detected provider. Returns nil when the provider is unknown
    /// (caller should hide the button) or when the message-id is empty.
    static func url(provider: WebmailProvider, messageId: String) -> URL? {
        guard !messageId.isEmpty else { return nil }
        switch provider {
        case .fastmail: return fastmailURL(messageId: messageId)
        case .unknown:  return nil
        }
    }

    /// Convenience wrapper: build the URL and hand it to NSWorkspace.
    /// No-op when the provider is unknown.
    static func open(provider: WebmailProvider, messageId: String) {
        guard let url = url(provider: provider, messageId: messageId) else { return }
        NSWorkspace.shared.open(url)
    }

    /// Construct an `app.fastmail.com/mail/search:msgid:...` URL.
    /// The base URL and optional account-id are read from @AppStorage so
    /// the user can point at a Fastmail Workspace tenant or pin their
    /// account-id if they have multiple Fastmail accounts.
    private static func fastmailURL(messageId: String) -> URL? {
        let encoded = messageId.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? messageId
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
}

private extension String {
    func trimmingTrailingSlash() -> String {
        hasSuffix("/") ? String(dropLast()) : self
    }
}
