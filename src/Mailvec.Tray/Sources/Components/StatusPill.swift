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
        .background(.white.opacity(0.10), in: Capsule())
        .overlay(Capsule().stroke(tint.opacity(0.33)))
        .foregroundStyle(tint)
    }
    private var tint: Color {
        switch severity { case .ok: .green; case .syncing: Brand.accent; case .warn: .orange; case .error: .red }
    }
    private var label: String {
        switch severity { case .ok: "All clear"; case .syncing: "Syncing"; case .warn: "Warning"; case .error: "Attention" }
    }
}
