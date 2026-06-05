// Components/StatusPill.swift
import SwiftUI

struct StatusPill: View {
    let severity: TrayHealth.Severity
    var body: some View {
        HStack(spacing: 6) {
            Circle().fill(tint).frame(width: 6, height: 6)
                .symbolEffect(.pulse, isActive: severity == .syncing)
            Text(label).font(.system(size: 11, weight: .semibold))
        }
        .padding(.horizontal, 9).padding(.vertical, 3)
        .background(tint.opacity(0.18), in: Capsule())
        .overlay(Capsule().stroke(tint.opacity(0.55)))
        .foregroundStyle(tint)
    }
    // Fixed bright tints — see Brand.statusOk note. System `.green`/`.orange`/
    // `.red` dim against the dark band, which is why this pill was hard to read.
    private var tint: Color {
        switch severity { case .ok: Brand.statusOk; case .syncing: Brand.accent; case .warn: Brand.statusWarn; case .error: Brand.statusError }
    }
    private var label: String {
        switch severity { case .ok: "All clear"; case .syncing: "Syncing"; case .warn: "Warning"; case .error: "Attention" }
    }
}
