import SwiftUI

/// The app icon drawn as vectors, playing the onboarding intro once:
/// three pieces fly into the dot, the dot swells, one piece rides the arch,
/// and the far end flashes two rings. It then rests as the plain icon.
/// With Reduce Motion on it shows the plain icon straight away.
struct BridgeGlyphAnimation: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.scenePhase) private var scenePhase
    // Nil until the scene is active, so a system alert on launch can't swallow the animation.
    @State private var start: Date?
    @State private var finished = false
    /// Seconds after the scene is active before the glyph starts drawing.
    var delay: TimeInterval = 0.45
    var onBegin: () -> Void = {}
    var onFinish: () -> Void = {}

    var body: some View {
        TimelineView(.animation(paused: start == nil || finished || reduceMotion)) { context in
            let t: TimeInterval = finished || reduceMotion ? BridgeGlyph.duration : start.map { context.date.timeIntervalSince($0) } ?? 0
            Canvas { canvas, size in
                BridgeGlyph.draw(in: &canvas, size: size, time: t)
            }
        }
        .aspectRatio(1, contentMode: .fit)
        .accessibilityHidden(true)
        // Plays once per presentation; paging back to it shows the finished icon.
        .onAppear { if scenePhase == .active, start == nil { begin() } }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, start == nil { begin() }
        }
        .task(id: start) {
            guard let start else { return }
            // A cancelled wait (the view went away) must not count as having played.
            do { try await Task.sleep(for: .seconds(BridgeGlyph.duration + max(0, start.timeIntervalSinceNow))) } catch { return }
            finished = true
            onFinish()
        }
    }

    // The default delay waits out the full-screen cover's slide-in.
    private func begin() {
        start = Date().addingTimeInterval(delay)
        finished = false
        onBegin()
    }
}

private enum BridgeGlyph {
    static let duration: TimeInterval = 2.7
    static let tile = Color(red: 0.027, green: 0.757, blue: 0.376)

    // Glyph geometry, in the same local units as the app icon's SVG.
    static let dotCenter = CGPoint(x: 66, y: 178)
    static let dotRadius: CGFloat = 40
    static let strokeWidth: CGFloat = 28
    static let segments: [(CGPoint, CGPoint, CGPoint, CGPoint)] = [
        (CGPoint(x: 80, y: 142), CGPoint(x: 92, y: 92), CGPoint(x: 134, y: 70), CGPoint(x: 172, y: 78)),
        (CGPoint(x: 172, y: 78), CGPoint(x: 206, y: 86), CGPoint(x: 222, y: 120), CGPoint(x: 220, y: 186)),
    ]
    static let incomingStarts = [CGPoint(x: -25, y: 95), CGPoint(x: -35, y: 180), CGPoint(x: 0, y: 265)]

    /// The arch flattened into a polyline with running lengths, for point-at-fraction lookups.
    static let samples: [(point: CGPoint, length: CGFloat)] = {
        var result: [(CGPoint, CGFloat)] = []
        var total: CGFloat = 0
        var previous: CGPoint?
        for (p0, p1, p2, p3) in segments {
            for i in 0...48 {
                let s = CGFloat(i) / 48
                let u = 1 - s
                let x = u * u * u * p0.x + 3 * u * u * s * p1.x + 3 * u * s * s * p2.x + s * s * s * p3.x
                let y = u * u * u * p0.y + 3 * u * u * s * p1.y + 3 * u * s * s * p2.y + s * s * s * p3.y
                let point = CGPoint(x: x, y: y)
                if let previous { total += hypot(point.x - previous.x, point.y - previous.y) }
                result.append((point, total))
                previous = point
            }
        }
        return result
    }()

    static func pointOnArch(_ fraction: CGFloat) -> CGPoint {
        let target = clamp(fraction) * samples[samples.count - 1].length
        guard let index = samples.firstIndex(where: { $0.length >= target }), index > 0 else { return samples[0].point }
        let a = samples[index - 1], b = samples[index]
        let span = b.length - a.length
        let k = span > 0 ? (target - a.length) / span : 0
        return CGPoint(x: a.point.x + (b.point.x - a.point.x) * k, y: a.point.y + (b.point.y - a.point.y) * k)
    }

    static func draw(in canvas: inout GraphicsContext, size: CGSize, time t: TimeInterval) {
        let side = min(size.width, size.height)
        let k = side / 1024
        // Same placement as the icon: translate(512 500) scale(3) translate(-130 -144).
        func map(_ p: CGPoint) -> CGPoint { CGPoint(x: ((p.x - 130) * 3 + 512) * k, y: ((p.y - 144) * 3 + 500) * k) }
        let unit = 3 * k
        let t = CGFloat(t)

        canvas.fill(Path(roundedRect: CGRect(x: 0, y: 0, width: side, height: side), cornerRadius: 230 * k, style: .continuous), with: .color(tile))

        // 1. Three pieces fly into the dot.
        for (i, origin) in incomingStarts.enumerated() {
            let u = clamp((t - CGFloat(i) * 0.15) / 0.7)
            guard u > 0, u < 1 else { continue }
            let e = u * u * u
            let p = CGPoint(x: origin.x + (dotCenter.x - origin.x) * e, y: origin.y + (dotCenter.y - origin.y) * e)
            canvas.fill(circle(map(p), 12 * unit), with: .color(.white))
        }

        // 4. Rings at the far end once the piece arrives (drawn under the glyph).
        let end = map(pointOnArch(1))
        for (delay, grow, width, alpha) in [(1.9, 34.0, 5.0, 0.8), (2.05, 24.0, 3.0, 0.6)] as [(CGFloat, CGFloat, CGFloat, CGFloat)] {
            let a = clamp((t - delay) / 0.6)
            guard a > 0, a < 1 else { continue }
            let r = (16 + grow * easeOut(a)) * unit
            canvas.stroke(circle(end, r), with: .color(.white.opacity(alpha * (1 - a))), lineWidth: width * unit)
        }

        // 2. The dot swells as it takes them in.
        let pulse = clamp((t - 0.7) / 0.4)
        let swell = 1 + 0.14 * sin(.pi * pulse)
        canvas.fill(circle(map(dotCenter), dotRadius * swell * unit), with: .color(.white))

        var arch = Path()
        arch.move(to: map(segments[0].0))
        for (_, c1, c2, p) in segments { arch.addCurve(to: map(p), control1: map(c1), control2: map(c2)) }
        canvas.stroke(arch, with: .color(.white), style: StrokeStyle(lineWidth: strokeWidth * unit, lineCap: .round))

        // 3. One piece rides the arch to the far end.
        let v = clamp((t - 1.05) / 0.9)
        if v > 0, v < 1 {
            canvas.fill(circle(map(pointOnArch(easeInOut(v))), 9 * unit), with: .color(tile))
        }
    }

    static func circle(_ c: CGPoint, _ r: CGFloat) -> Path {
        Path(ellipseIn: CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r))
    }
    static func clamp(_ x: CGFloat) -> CGFloat { min(max(x, 0), 1) }
    static func easeOut(_ x: CGFloat) -> CGFloat { 1 - pow(1 - x, 3) }
    static func easeInOut(_ x: CGFloat) -> CGFloat { x < 0.5 ? 4 * x * x * x : 1 - pow(-2 * x + 2, 3) / 2 }
}

#Preview {
    BridgeGlyphAnimation().frame(width: 200, height: 200)
}
