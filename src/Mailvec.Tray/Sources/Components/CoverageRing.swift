// Components/CoverageRing.swift
import SwiftUI

struct CoverageRing: View {
    let progress: Double
    let severity: TrayHealth.Severity
    var body: some View {
        ZStack {
            Circle().stroke(Color.white.opacity(0.08), lineWidth: 6)
            Circle()
                .trim(from: 0, to: progress)
                .stroke(tint, style: StrokeStyle(lineWidth: 6, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .shadow(color: severity == .syncing ? tint.opacity(0.8) : .clear,
                        radius: 6)
                .animation(.easeInOut(duration: 0.8), value: progress)
            VStack(spacing: 2) {
                Text(progress, format: .percent.precision(.fractionLength(0)))
                    .font(.system(size: 16, weight: .bold)).monospacedDigit()
                    .kerning(-0.4)
                Text("EMBEDDED")
                    .font(.system(size: 9, weight: .semibold)).tracking(0.5)
                    .foregroundStyle(.white.opacity(0.55))
            }
            .foregroundStyle(Brand.bandText)
        }
    }
    private var tint: Color { severity == .error ? .red : Brand.accent }
}
