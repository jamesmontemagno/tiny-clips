import Foundation

// MARK: - Render Geometry


struct StudioRenderClickRing: Equatable, Sendable {
    var center: CGPoint
    var radius: Double
    var strokeWidth: Double
    var opacity: Double
}

struct StudioRenderAffineTransform: Equatable, Sendable {
    var a: Double
    var b: Double
    var c: Double
    var d: Double
    var tx: Double
    var ty: Double

    static let identity = StudioRenderAffineTransform(a: 1, b: 0, c: 0, d: 1, tx: 0, ty: 0)

    static func translation(x: Double, y: Double) -> StudioRenderAffineTransform {
        StudioRenderAffineTransform(a: 1, b: 0, c: 0, d: 1, tx: x, ty: y)
    }

    static func scale(x: Double, y: Double) -> StudioRenderAffineTransform {
        StudioRenderAffineTransform(a: x, b: 0, c: 0, d: y, tx: 0, ty: 0)
    }

    func concatenating(_ other: StudioRenderAffineTransform) -> StudioRenderAffineTransform {
        StudioRenderAffineTransform(
            a: a * other.a + b * other.c,
            b: a * other.b + b * other.d,
            c: c * other.a + d * other.c,
            d: c * other.b + d * other.d,
            tx: tx * other.a + ty * other.c + other.tx,
            ty: tx * other.b + ty * other.d + other.ty
        )
    }

    func applying(to point: CGPoint) -> CGPoint {
        CGPoint(
            x: point.x * a + point.y * c + tx,
            y: point.x * b + point.y * d + ty
        )
    }
}

enum StudioRenderGeometry {
    static func ciRect(forTopLeftRect rect: StudioRect, renderSize: CGSize) -> CGRect {
        CGRect(
            x: rect.x,
            y: Double(renderSize.height) - rect.y - rect.height,
            width: rect.width,
            height: rect.height
        )
    }

    static func ciPoint(forTopLeftPoint point: CGPoint, renderSize: CGSize) -> CGPoint {
        CGPoint(x: point.x, y: renderSize.height - point.y)
    }

    static func sourceCropRect(source: StudioRect, naturalSize: CGSize) -> CGRect {
        CGRect(
            x: source.x * naturalSize.width,
            y: (1 - source.y - source.height) * naturalSize.height,
            width: source.width * naturalSize.width,
            height: source.height * naturalSize.height
        )
    }

    static func sourceToDestinationTransform(
        sourceSize: CGSize,
        sourceCrop: StudioRect,
        destinationRect: StudioRect,
        renderSize: CGSize,
        mirrored: Bool
    ) -> StudioRenderAffineTransform {
        let crop = sourceCropRect(source: sourceCrop, naturalSize: sourceSize)
        guard crop.width > 0, crop.height > 0, destinationRect.width > 0, destinationRect.height > 0 else {
            return .identity
        }

        let destination = ciRect(forTopLeftRect: destinationRect, renderSize: renderSize)
        let scaleX = destination.width / crop.width
        let scaleY = destination.height / crop.height

        var transform = StudioRenderAffineTransform.translation(x: -crop.minX, y: -crop.minY)
        if mirrored {
            transform = transform
                .concatenating(.translation(x: -crop.width, y: 0))
                .concatenating(.scale(x: -1, y: 1))
        }
        return transform
            .concatenating(.scale(x: scaleX, y: scaleY))
            .concatenating(.translation(x: destination.minX, y: destination.minY))
    }

    static func squirclePoints(in rect: StudioRect, pointCount: Int = 96) -> [CGPoint] {
        let samples = max(8, pointCount)
        let a = rect.width / 2
        let b = rect.height / 2
        let centerX = rect.x + a
        let centerY = rect.y + b
        guard a > 0, b > 0 else { return [] }

        return (0..<samples).map { index in
            let theta = (Double(index) / Double(samples)) * 2 * Double.pi
            let cosTheta = cos(theta)
            let sinTheta = sin(theta)
            let x = centerX + a * signedPower(cosTheta, 2.0 / 5.0)
            let y = centerY + b * signedPower(sinTheta, 2.0 / 5.0)
            return CGPoint(x: x, y: y)
        }
    }

    static func activeClickRings(
        at sourceTime: Double,
        events: StudioEvents,
        frame: StudioResolvedFrame,
        overlay: StudioClickOverlay,
        screenSourceWidth: Int? = nil
    ) -> [StudioRenderClickRing] {
        guard overlay.enabled, let screen = frame.screen else { return [] }
        return events.clicks.compactMap {
            clickRing(
                for: $0,
                at: sourceTime,
                screen: screen,
                overlay: overlay,
                capture: events.capture,
                screenSourceWidth: screenSourceWidth
            )
        }
    }

    static func clickRing(
        for event: StudioClickEvent,
        at sourceTime: Double,
        screen: StudioResolvedScreen,
        overlay: StudioClickOverlay,
        capture: StudioCaptureInfo = StudioCaptureInfo(),
        screenSourceWidth: Int? = nil
    ) -> StudioRenderClickRing? {
        let duration = max(0, overlay.duration)
        guard duration > 0 else { return nil }
        let elapsed = sourceTime - event.t
        guard elapsed >= 0, elapsed <= duration else { return nil }

        let progress = elapsed / duration
        let source = screen.source
        guard source.width > 0, source.height > 0 else { return nil }

        let normalizedX = (event.x - source.x) / source.width
        let normalizedY = (event.y - source.y) / source.height
        guard normalizedX >= 0, normalizedX <= 1, normalizedY >= 0, normalizedY <= 1 else {
            return nil
        }

        let center = CGPoint(
            x: screen.rect.x + normalizedX * screen.rect.width,
            y: screen.rect.y + normalizedY * screen.rect.height
        )
        let captureWidth = capture.width > 0 ? Double(capture.width) : Double(screenSourceWidth ?? 0)
        let sourceWidth = screenSourceWidth.map { max(1, Double($0)) } ?? max(1, captureWidth)
        let sourcePixelsPerCapturePoint = captureWidth > 0 ? sourceWidth / captureWidth : 1
        let visibleSourcePixels = max(1, sourceWidth * source.width)
        let sourcePixelsPerCanvasPixel = visibleSourcePixels / max(screen.rect.width, 1)
        let pointScale = max(0.000_001, capture.scale) * sourcePixelsPerCapturePoint / sourcePixelsPerCanvasPixel
        let baseSize = max(0, overlay.size) * pointScale
        let radius = (baseSize / 2.0) + (baseSize * 0.58 * progress)
        let opacity = max(0, (1 - progress) * StudioCanvasMath.clamped(overlay.opacity, 0, 1))
        return StudioRenderClickRing(
            center: center,
            radius: radius,
            strokeWidth: max(0, overlay.strokeWidth) * pointScale,
            opacity: opacity
        )
    }

    static func gradientEndpoints(renderSize: CGSize) -> (start: CGPoint, end: CGPoint) {
        (
            start: CGPoint(x: 0, y: renderSize.height),
            end: CGPoint(x: renderSize.width, y: 0)
        )
    }


    // MARK: - Private

    private static func signedPower(_ value: Double, _ exponent: Double) -> Double {
        let magnitude = pow(abs(value), exponent)
        return value < 0 ? -magnitude : magnitude
    }
}
