// TrayNotifications.swift
//
// Thin wrapper around UNUserNotificationCenter for the three proactive
// alerts the General prefs tab advertises:
//
//   • Ollama unreachable  — fires when /tray/status flips from reachable
//                           to unreachable.
//   • Sync failure        — fires when mbsync severity flips from ok to
//                           warn/error.
//   • Embed complete      — fires when coverage transitions from <100% to
//                           100% (one-shot).
//
// Each notification respects the corresponding @AppStorage toggle so the
// user can disable individual streams from Preferences → General.
//
// Authorization is requested lazily on the first call to `send(...)` —
// macOS only shows the OS-level permission prompt once per app install,
// and silently no-ops subsequent calls if the user declined.
import Foundation
import UserNotifications

@MainActor
enum TrayNotifications {
    private static var authorizationRequested = false

    /// Call once at app launch to register authorization upfront. Safe to
    /// call repeatedly — UNUserNotificationCenter dedupes internally.
    static func requestAuthorizationIfNeeded() {
        guard !authorizationRequested else { return }
        authorizationRequested = true
        UNUserNotificationCenter.current()
            .requestAuthorization(options: [.alert, .sound]) { granted, error in
                if let e = error {
                    TrayLog.warn("notification auth failed", error: e)
                    return
                }
                TrayLog.info("notification auth", "granted=\(granted)")
            }
    }

    enum Kind: String {
        case ollamaDown, syncFailure, embedComplete
        var defaultsKey: String {
            switch self {
            case .ollamaDown:    return "notifyOllamaDown"
            case .syncFailure:   return "notifySyncFailures"
            case .embedComplete: return "notifyEmbedDone"
            }
        }
        /// Default true — matches the @AppStorage defaults in
        /// PreferencesModel so the first launch fires notifications even
        /// before the user opens Preferences.
        var defaultEnabled: Bool { true }
    }

    static func send(kind: Kind, title: String, body: String) {
        // Honour the user's per-channel toggle. UserDefaults returns false
        // for absent keys, so we read via object(forKey:) to distinguish
        // "explicitly off" from "never set" (the latter falls back to the
        // kind's default).
        let enabled: Bool
        if let value = UserDefaults.standard.object(forKey: kind.defaultsKey) as? Bool {
            enabled = value
        } else {
            enabled = kind.defaultEnabled
        }
        guard enabled else {
            TrayLog.debug("notification suppressed", "\(kind.rawValue) — disabled in prefs")
            return
        }

        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        // identifier = kind so we don't stack duplicate banners; macOS
        // replaces an existing notification with the same id.
        let request = UNNotificationRequest(
            identifier: "mailvec.\(kind.rawValue)",
            content: content,
            trigger: nil)
        UNUserNotificationCenter.current().add(request) { error in
            if let e = error {
                TrayLog.warn("notification add failed", error: e)
            } else {
                TrayLog.info("notification fired", "\(kind.rawValue)")
            }
        }
    }
}
