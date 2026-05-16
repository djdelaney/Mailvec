// MailvecTrayApp.swift
import AppKit
import HotKey
import ServiceManagement
import SwiftUI

/// Minimal AppDelegate that just enforces accessory mode at launch. We no
/// longer flip activation policy to .regular to open Preferences — that
/// pattern reliably opened the window but caused NSStatusItem to get stuck
/// after several open/close cycles, leaving the menu bar icon visible but
/// unresponsive. Preferences is now a regular WindowGroup opened via
/// `\.openWindow`, which works without any activation-policy churn.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        // Request notification authorization upfront so the OS prompt
        // appears once at first launch rather than at the awkward moment a
        // sync first fails. macOS dedupes the prompt across app installs.
        Task { @MainActor in TrayNotifications.requestAuthorizationIfNeeded() }
        // Reconcile launch-at-login state with macOS. The Preferences UI
        // binds the toggle to @AppStorage("launchAtLogin") (default true),
        // but SMAppService.mainApp.register() only runs inside `.onChange`
        // — which never fires for a user who installs the app and leaves
        // the toggle alone. Without this reconciliation, fresh installs
        // show "Launch at login" as ON in Preferences while the system
        // has no record of the app as a login item, and the tray fails
        // to auto-start after reboot. Self-heals on every launch in case
        // macOS revokes the entry (e.g. user disables it in System
        // Settings → General → Login Items).
        Task { @MainActor in reconcileLaunchAtLogin() }
    }

    private func reconcileLaunchAtLogin() {
        let stored = UserDefaults.standard.object(forKey: "launchAtLogin") as? Bool ?? true
        let status = SMAppService.mainApp.status
        do {
            switch (stored, status) {
            case (true, .notRegistered), (true, .notFound):
                try SMAppService.mainApp.register()
                TrayLog.info("launch-at-login", "registered (was \(status))")
            case (false, .enabled):
                Task { try? await SMAppService.mainApp.unregister() }
                TrayLog.info("launch-at-login", "unregistered (stored=false)")
            default:
                // Already in sync, or in a transient state (.requiresApproval)
                // that the user must resolve in System Settings.
                break
            }
        } catch {
            TrayLog.error("launch-at-login", "reconcile failed: \(error.localizedDescription)")
        }
    }
}

@main
struct MailvecTrayApp: App {
    @StateObject private var model = TrayModel()
    @State private var hotkey: HotKey?
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    init() {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
        TrayLog.info("app launch", "Mailvec.Tray v\(version)")
    }

    var body: some Scene {
        MenuBarExtra {
            Group {
                switch model.pane {
                case .dashboard: DashboardView()
                case .search:    SearchView()
                }
            }
            .environmentObject(model)
            // 460pt (up from the handoff's 380pt) gives the 5 service tiles
            // enough room to fit "embedder"/"indexer" on a single line, and
            // the timeline rows enough room for "6:43 PM" without truncation.
            .frame(width: 460)
            // The handoff design is light-mode only: cream popover, white
            // cards, dark text. We have to inject the colorScheme into the
            // environment — `.preferredColorScheme(.light)` on a MenuBarExtra
            // doesn't propagate into the popover window the way it does for
            // a regular Scene, so Color.primary / .secondary stay tied to
            // the system appearance and end up unreadable in Dark Mode.
            .environment(\.colorScheme, .light)
            .onAppear {
                TrayLog.info("popover opened")
            }
        } label: {
            MenuBarIcon(severity: model.health?.severity ?? .ok)
                // Pre-warm at app launch. The menu-bar label is rendered
                // as soon as the app finishes launching — well before the
                // user clicks the icon — so .task here fires on cold
                // start. Doing the prewarm from the popover content's
                // .onAppear (the prior location) was too late: the user
                // saw a loading spinner on first open while /tray/status
                // and /tray/folders made their first round-trip.
                //
                // All three calls are idempotent (start()'s poller guard,
                // loadFolders()'s availableFolders.isEmpty guard,
                // registerHotkey()'s hotkey==nil guard), so this also
                // safely re-runs if SwiftUI ever re-attaches the label.
                .task {
                    TrayLog.info("prewarm", "starting poller + folder fetch")
                    model.start()
                    registerHotkey()
                    await model.loadFolders()
                }
        }
        .menuBarExtraStyle(.window)

        // Regular WindowGroup, not a `Settings` scene. SwiftUI's `Settings`
        // requires the app's activation policy to be .regular to surface
        // properly — for LSUIElement (menu-bar accessory) apps that means
        // bouncing between .accessory and .regular every time, which
        // eventually corrupts NSStatusItem state and leaves the menu-bar
        // icon visible-but-unresponsive. A WindowGroup with `\.openWindow`
        // opens reliably from accessory mode and stays in accessory mode.
        WindowGroup("Mailvec Preferences", id: "preferences") {
            PreferencesScene()
                .environmentObject(model)
                .frame(minWidth: 680, idealWidth: 680, minHeight: 720, idealHeight: 720)
        }
        .windowResizability(.contentSize)
        .commandsRemoved()
    }

    /// Registers ⌘⇧M as a global hotkey. The HotKey package
    /// (https://github.com/soffes/HotKey) wraps Carbon's RegisterEventHotKey
    /// — the modern SwiftUI KeyboardShortcut API only fires while a window
    /// is key, which isn't useful for a menu-bar accessory.
    private func registerHotkey() {
        guard hotkey == nil else { return }
        let hk = HotKey(key: .m, modifiers: [.command, .shift])
        hk.keyDownHandler = {
            TrayLog.debug("hotkey fired", "⌘⇧M")
            DispatchQueue.main.async {
                model.pane = .search
                model.pendingSearchFocus = true
            }
        }
        hotkey = hk
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
        TrayLog.info("hotkey registered", "⌘⇧M · tray v\(version)")
    }
}
