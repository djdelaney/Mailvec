// MailvecTrayApp.swift
import AppKit
import ServiceManagement
import SwiftUI

/// AppDelegate is where all launch-time work happens. `applicationDidFinish-
/// Launching` is the only hook macOS guarantees to fire once at app start
/// regardless of whether the user has clicked the menu-bar icon yet — and
/// the popover content's `.onAppear` doesn't fire until first open, while
/// `.task` on the MenuBarExtra label doesn't fire reliably for the icon
/// view. Putting prewarm here means status polling and folder/system
/// prefetch all run from boot.
///
/// There used to be a global ⌘⇧M hotkey here (via the HotKey SPM package)
/// that opened the search popover. The Carbon hotkey registration worked
/// fine, but SwiftUI's `MenuBarExtra` doesn't expose a public API to open
/// its popover programmatically — the KVC reflection workaround
/// (NSStatusBarWindow → statusItem → performClick) wasn't stable across
/// SwiftUI versions, so the hotkey would set TrayModel state without
/// surfacing any UI. Removed cleanly rather than ship a non-working
/// feature; reinstate when SwiftUI grows an isMenuPresented binding.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        Task { @MainActor in
            // Request notification authorization upfront so the OS prompt
            // appears once at first launch rather than at the awkward
            // moment a sync first fails. macOS dedupes the prompt across
            // app installs.
            TrayNotifications.requestAuthorizationIfNeeded()

            // Reconcile launch-at-login state with macOS. The Preferences
            // UI binds the toggle to @AppStorage("launchAtLogin") (default
            // true), but SMAppService.mainApp.register() only runs inside
            // `.onChange` — which never fires for a user who installs the
            // app and leaves the toggle alone. Without this reconciliation,
            // fresh installs show "Launch at login" as ON in Preferences
            // while the system has no record of the app as a login item,
            // and the tray fails to auto-start after reboot. Self-heals on
            // every launch in case macOS revokes the entry (e.g. user
            // disables it in System Settings → General → Login Items).
            self.reconcileLaunchAtLogin()

            // Pre-warm: start polling and load folder + system info so
            // the dashboard has data the moment the user clicks the icon,
            // the search popover's folder filter is populated, and the
            // webmail provider (Fastmail / Gmail / …) is known before any
            // "Open in …" button renders. All three calls are idempotent
            // (poller-nil guard + availableFolders-empty guard +
            // imapHost-nil guard).
            TrayLog.info("prewarm", "starting poller + folder fetch + system fetch")
            TrayModel.shared.start()
            async let folders: () = TrayModel.shared.loadFolders()
            async let system: () = TrayModel.shared.loadSystemInfo()
            _ = await (folders, system)
        }
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
                // Already in sync, or in a transient state
                // (.requiresApproval) that the user must resolve in
                // System Settings.
                break
            }
        } catch {
            TrayLog.error("launch-at-login", "reconcile failed: \(error.localizedDescription)")
        }
    }

}

@main
struct MailvecTrayApp: App {
    // Bind the SwiftUI scene to the same TrayModel singleton the
    // AppDelegate prewarms. @StateObject(wrappedValue:) hands the existing
    // instance to SwiftUI rather than constructing a new one.
    @StateObject private var model: TrayModel
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    init() {
        _model = StateObject(wrappedValue: TrayModel.shared)
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
            .onAppear { TrayLog.info("popover opened") }
        } label: {
            // effectiveSeverity, not `health?.severity ?? .ok`: a dead server
            // must show the error badge (stale green is a lie), and the first
            // poll shows the syncing pulse instead of a false all-clear.
            MenuBarIcon(severity: model.effectiveSeverity)
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
}
