// Components/ServiceTile.swift
import SwiftUI

struct ServiceTile: View {
    let service: ServiceStatus
    var body: some View {
        VStack(spacing: 2) {
            HStack(spacing: 4) {
                Circle().fill(tint).frame(width: 5, height: 5)
                    .symbolEffect(.pulse, isActive: service.busy)
                // Keep the service id on a single line — wrapping
                // "embedder"/"indexer" mid-word makes the row of tiles
                // look ragged. Allow a small scale-down for the longest
                // labels so all five tiles stay the same height.
                Text(service.id)
                    .font(.system(size: 10.5, design: .monospaced))
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            }
            Text(service.detail)
                .font(.system(size: 9.5))
                .foregroundStyle(service.severity == .error ? .red : .secondary)
                .lineLimit(1).truncationMode(.tail)
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 6).padding(.vertical, 6)
        .background(tileFill, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(tileStroke))
    }
    private var tint: Color {
        switch service.severity {
        case .error: return .red
        case .warn:  return .orange
        default:
            if service.busy { return Brand.accent }
            return service.ok ? .green : .secondary
        }
    }
    private var tileFill: Color {
        switch service.severity {
        case .error: return Color.red.opacity(0.06)
        case .warn:  return Color.orange.opacity(0.06)
        default:     return Brand.cardBg
        }
    }
    private var tileStroke: Color {
        switch service.severity {
        case .error: return Color.red.opacity(0.28)
        case .warn:  return Color.orange.opacity(0.28)
        default:     return Brand.hairline
        }
    }
}
