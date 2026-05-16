// MenuBarIcon.swift
import AppKit
import SwiftUI

struct MenuBarIcon: View {
    let severity: TrayHealth.Severity

    var body: some View {
        ZStack(alignment: .topTrailing) {
            iconImage
                .symbolEffect(.pulse, isActive: severity == .syncing)

            if severity != .ok {
                Circle()
                    .fill(severity == .error ? Color.red : Brand.accent)
                    .frame(width: 5, height: 5)
                    .offset(x: 3, y: -2)
                    .symbolEffect(.pulse, isActive: severity == .syncing)
            }
        }
        .accessibilityLabel(severity == .error
                            ? "Mailvec — attention needed"
                            : severity == .syncing
                            ? "Mailvec — syncing"
                            : "Mailvec — healthy")
    }

    /// Use the custom `mailvec.mv` SF Symbol if it's been imported into the
    /// asset catalog, otherwise fall back to a system symbol so the menu bar
    /// always shows *something*. `NSImage(named:)` returns nil for missing
    /// assets, which is the cheapest existence check.
    @ViewBuilder
    private var iconImage: some View {
        if NSImage(named: "mailvec.mv") != nil {
            Image("mailvec.mv").renderingMode(.template)
        } else {
            Image(systemName: "tray.full.fill")
        }
    }
}
