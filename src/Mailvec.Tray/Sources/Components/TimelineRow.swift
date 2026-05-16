// Components/TimelineRow.swift
import SwiftUI

struct TimelineRow: View {
    let event: TimelineEvent
    var body: some View {
        HStack(spacing: 8) {
            // 12h format with AM/PM marker (e.g. "6:43 PM"). The handoff
            // used `.hour().minute()` which is locale-dependent; locking
            // to 12h matches the user preference and keeps the timeline
            // visually consistent regardless of system region. 64pt is wide
            // enough for "12:59 AM" without truncation.
            Text(formattedTime(event.time))
                .font(.system(size: 10, design: .monospaced))
                .foregroundStyle(.secondary)
                .monospacedDigit()
                .frame(width: 64, alignment: .leading)
            Circle().fill(dotColor).frame(width: 5, height: 5)
                .symbolEffect(.pulse, isActive: event.live)
            Text(event.text)
                .font(.system(size: 11.5))
                .foregroundStyle(event.severity == .error ? .red : .primary)
                .lineLimit(1).truncationMode(.tail)
            Spacer()
            Text(event.agent.uppercased())
                .font(.system(size: 9, design: .monospaced))
                .tracking(0.4)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 8).padding(.vertical, 4)
        .background(event.live ? Brand.accent.opacity(0.06) : .clear,
                    in: RoundedRectangle(cornerRadius: 6))
    }
    private var dotColor: Color {
        switch event.kind {
        case .sync:    .secondary
        case .indexed: .blue
        case .embed:   Brand.accent
        case .error:   .red
        }
    }
}

private let timelineTimeFormatter: DateFormatter = {
    let f = DateFormatter()
    f.dateFormat = "h:mm a"        // 12h with AM/PM
    f.amSymbol = "AM"
    f.pmSymbol = "PM"
    return f
}()

private func formattedTime(_ date: Date) -> String {
    timelineTimeFormatter.string(from: date)
}
