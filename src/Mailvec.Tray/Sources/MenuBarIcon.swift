// MenuBarIcon.swift
import AppKit
import SwiftUI

struct MenuBarIcon: View {
    let severity: TrayHealth.Severity

    var body: some View {
        ZStack(alignment: .topTrailing) {
            iconImage
                .symbolEffect(.pulse, isActive: severity == .syncing)
                // Force the MenuBarExtra label to rebuild whenever severity
                // changes. Without this, the rendered status-item image can
                // get cached across transitions.
                .id(severity)

            if let badge = badgeColor {
                Circle()
                    .fill(badge)
                    // 9pt (up from 5pt) so the dot is genuinely visible on a
                    // Retina menu bar. Offset bumped to match.
                    .frame(width: 9, height: 9)
                    .offset(x: 5, y: -4)
                    .symbolEffect(.pulse, isActive: severity == .syncing)
            }
        }
        .accessibilityLabel(accessibilityLabel)
    }

    /// macOS's MenuBarExtra label silently strips SwiftUI `.foregroundStyle()`
    /// from `Image(systemName:)` and from template `Image("asset")`
    /// — NSStatusItem treats the rendered label as a template and applies its
    /// own monochrome auto-tint. The only reliable way I've found to ship a
    /// coloured icon is to hand it a fully-baked, non-template NSImage
    /// constructed with a palette `SymbolConfiguration`. For healthy states
    /// we keep the plain SwiftUI Image so the system's auto-tint runs and
    /// the icon adapts to light/dark menu bars.
    @ViewBuilder
    private var iconImage: some View {
        if let coloured = colouredNSImage {
            Image(nsImage: coloured)
        } else if NSImage(named: "mailvec.mv") != nil {
            Image("mailvec.mv").renderingMode(.template)
        } else {
            Image(systemName: "tray.full.fill")
        }
    }

    /// Builds a non-template NSImage with the severity's palette colour
    /// baked in. Returns nil for healthy states so the caller falls through
    /// to the template path. Uses NSImage palette configuration rather than
    /// SwiftUI's `.foregroundStyle()` so the colour survives NSStatusItem's
    /// template stripping.
    private var colouredNSImage: NSImage? {
        guard let tint = nsTintColor else { return nil }
        let symbolName = NSImage(named: "mailvec.mv") != nil ? "mailvec.mv" : "tray.full.fill"
        let base = NSImage(systemSymbolName: symbolName, accessibilityDescription: nil)
            ?? NSImage(named: symbolName)
        guard let image = base else { return nil }
        let config = NSImage.SymbolConfiguration(paletteColors: [tint])
        // withSymbolConfiguration returns the configured image; isTemplate
        // is implicitly false on the result, which is what we want — a
        // template image would be re-tinted by NSStatusItem.
        let configured = image.withSymbolConfiguration(config) ?? image
        configured.isTemplate = false
        return configured
    }

    private var nsTintColor: NSColor? {
        switch severity {
        case .ok, .syncing: return nil
        case .warn:         return .systemYellow
        case .error:        return .systemRed
        }
    }

    /// Corner-dot colour. nil means "no badge".
    private var badgeColor: Color? {
        switch severity {
        case .ok:      return nil
        case .syncing: return Brand.accent
        case .warn:    return .yellow
        case .error:   return .red
        }
    }

    private var accessibilityLabel: String {
        switch severity {
        case .ok:      return "Mailvec — healthy"
        case .syncing: return "Mailvec — syncing"
        case .warn:    return "Mailvec — attention needed"
        case .error:   return "Mailvec — error"
        }
    }
}
