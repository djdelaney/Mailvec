// Components/Sparkline.swift
import SwiftUI

struct Sparkline: View {
    let data: [Int]
    let accent: Color

    var body: some View {
        GeometryReader { g in
            let max = Double(data.max() ?? 1)
            let stepX = g.size.width / Double(data.count - 1)
            let pts = data.enumerated().map { i, v in
                CGPoint(x: Double(i) * stepX,
                        y: g.size.height - (Double(v) / max) * (g.size.height - 4) - 2)
            }
            ZStack {
                Path { p in
                    p.move(to: CGPoint(x: 0, y: g.size.height))
                    pts.forEach { p.addLine(to: $0) }
                    p.addLine(to: CGPoint(x: g.size.width, y: g.size.height))
                    p.closeSubpath()
                }
                .fill(LinearGradient(colors: [accent.opacity(0.35), accent.opacity(0)],
                                     startPoint: .top, endPoint: .bottom))
                Path { p in
                    p.move(to: pts.first ?? .zero)
                    pts.dropFirst().forEach { p.addLine(to: $0) }
                }
                .stroke(accent, style: StrokeStyle(lineWidth: 1.5,
                                                   lineCap: .round, lineJoin: .round))
                if let last = pts.last {
                    Circle().fill(accent).frame(width: 5, height: 5)
                        .position(last)
                }
            }
        }
    }
}
