// Components/StatusPill.swift
import SwiftUI

struct StatusPill: View {
    /// Server-reported severity, plus the two client-side states the server
    /// can't report: never-yet-connected and connection-lost. These used to
    /// collapse into `severity: h?.severity ?? .ok`, which rendered a green
    /// "All clear" over a dead (or never-installed) server.
    enum PillState: Equatable {
        case severity(TrayHealth.Severity)
        case connecting
        case unreachable
    }

    let state: PillState

    init(severity: TrayHealth.Severity) { state = .severity(severity) }
    init(state: PillState) { self.state = state }

    var body: some View {
        HStack(spacing: 6) {
            Circle().fill(tint).frame(width: 6, height: 6)
                .symbolEffect(.pulse, isActive: pulses)
            Text(label).font(.system(size: 11, weight: .semibold))
        }
        .padding(.horizontal, 9).padding(.vertical, 3)
        .background(tint.opacity(0.18), in: Capsule())
        .overlay(Capsule().stroke(tint.opacity(0.55)))
        .foregroundStyle(tint)
    }

    private var pulses: Bool {
        state == .severity(.syncing) || state == .connecting
    }
    // Fixed bright tints — see Brand.statusOk note. System `.green`/`.orange`/
    // `.red` dim against the dark band, which is why this pill was hard to read.
    private var tint: Color {
        switch state {
        case .severity(.ok): Brand.statusOk
        case .severity(.syncing): Brand.accent
        case .severity(.warn): Brand.statusWarn
        case .severity(.error), .unreachable: Brand.statusError
        case .connecting: Brand.accent
        }
    }
    private var label: String {
        switch state {
        case .severity(.ok): "All clear"
        case .severity(.syncing): "Syncing"
        case .severity(.warn): "Warning"
        case .severity(.error): "Attention"
        case .connecting: "Connecting…"
        case .unreachable: "Unreachable"
        }
    }
}
