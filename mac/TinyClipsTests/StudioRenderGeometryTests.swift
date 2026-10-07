import CoreMedia
import XCTest
@testable import TinyClips

final class StudioRenderGeometryTests: XCTestCase {
    func testConvertsTopLeftRectToCoreImageRect() {
        let rect = StudioRect(x: 10, y: 20, width: 300, height: 120)
        let converted = StudioRenderGeometry.ciRect(forTopLeftRect: rect, renderSize: CGSize(width: 800, height: 600))

        XCTAssertEqual(converted.origin.x, 10, accuracy: 1e-6)
        XCTAssertEqual(converted.origin.y, 460, accuracy: 1e-6)
        XCTAssertEqual(converted.width, 300, accuracy: 1e-6)
        XCTAssertEqual(converted.height, 120, accuracy: 1e-6)
    }

    func testBuildsSourceCropRectInBottomLeftSpace() {
        let crop = StudioRect(x: 0.25, y: 0.1, width: 0.5, height: 0.4)
        let rect = StudioRenderGeometry.sourceCropRect(source: crop, naturalSize: CGSize(width: 1920, height: 1080))

        XCTAssertEqual(rect.origin.x, 480, accuracy: 1e-6)
        XCTAssertEqual(rect.origin.y, 540, accuracy: 1e-6)
        XCTAssertEqual(rect.width, 960, accuracy: 1e-6)
        XCTAssertEqual(rect.height, 432, accuracy: 1e-6)
    }

    func testSourceToDestinationTransformMapsCropCornersAndMirrorsWhenRequested() {
        let sourceSize = CGSize(width: 1000, height: 500)
        let crop = StudioRect(x: 0.2, y: 0.1, width: 0.5, height: 0.4)
        let destination = StudioRect(x: 100, y: 50, width: 300, height: 200)
        let renderSize = CGSize(width: 800, height: 600)

        let transform = StudioRenderGeometry.sourceToDestinationTransform(
            sourceSize: sourceSize,
            sourceCrop: crop,
            destinationRect: destination,
            renderSize: renderSize,
            mirrored: false
        )
        let mirrored = StudioRenderGeometry.sourceToDestinationTransform(
            sourceSize: sourceSize,
            sourceCrop: crop,
            destinationRect: destination,
            renderSize: renderSize,
            mirrored: true
        )

        let cropRect = StudioRenderGeometry.sourceCropRect(source: crop, naturalSize: sourceSize)
        XCTAssertEqual(transform.applying(to: cropRect.origin).x, 100, accuracy: 1e-6)
        XCTAssertEqual(transform.applying(to: cropRect.origin).y, 350, accuracy: 1e-6)
        XCTAssertEqual(transform.applying(to: CGPoint(x: cropRect.maxX, y: cropRect.maxY)).x, 400, accuracy: 1e-6)
        XCTAssertEqual(transform.applying(to: CGPoint(x: cropRect.maxX, y: cropRect.maxY)).y, 550, accuracy: 1e-6)

        XCTAssertEqual(mirrored.applying(to: cropRect.origin).x, 400, accuracy: 1e-6)
        XCTAssertEqual(mirrored.applying(to: CGPoint(x: cropRect.maxX, y: cropRect.minY)).x, 100, accuracy: 1e-6)
    }

    func testSquirclePointsStayOnSuperellipse() {
        let rect = StudioRect(x: 10, y: 20, width: 120, height: 80)
        let points = StudioRenderGeometry.squirclePoints(in: rect, pointCount: 32)

        XCTAssertEqual(points.count, 32)
        let a = rect.width / 2
        let b = rect.height / 2
        let center = CGPoint(x: rect.x + a, y: rect.y + b)
        for point in points {
            let value = pow(abs((point.x - center.x) / a), 5) + pow(abs((point.y - center.y) / b), 5)
            XCTAssertEqual(value, 1, accuracy: 1e-6)
        }
    }

    func testClickRingUsesMousePulseTimingAndSourceCrop() {
        let screen = StudioResolvedScreen(
            rect: StudioRect(x: 100, y: 50, width: 800, height: 400),
            source: StudioRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5),
            cornerRadius: 10,
            shadow: StudioResolvedShadow(blur: 0, offsetY: 0, opacity: 0)
        )
        let overlay = StudioClickOverlay(size: 40, strokeWidth: 4, opacity: 0.8, duration: 0.5)
        let capture = StudioCaptureInfo(width: 2000, height: 1000, scale: 2)
        let event = StudioClickEvent(t: 1, x: 0.5, y: 0.5)

        let ring = StudioRenderGeometry.clickRing(
            for: event,
            at: 1.25,
            screen: screen,
            overlay: overlay,
            capture: capture
        )

        XCTAssertEqual(ring?.center.x ?? -1, 500, accuracy: 1e-6)
        XCTAssertEqual(ring?.center.y ?? -1, 250, accuracy: 1e-6)
        XCTAssertEqual(ring?.radius ?? -1, 50.56, accuracy: 1e-6)
        XCTAssertEqual(ring?.strokeWidth ?? -1, 6.4, accuracy: 1e-6)
        XCTAssertEqual(ring?.opacity ?? -1, 0.4, accuracy: 1e-6)

        XCTAssertNil(StudioRenderGeometry.clickRing(for: event, at: 0.9, screen: screen, overlay: overlay, capture: capture))
        XCTAssertNil(StudioRenderGeometry.clickRing(for: StudioClickEvent(t: 1, x: 0.1, y: 0.5), at: 1.1, screen: screen, overlay: overlay, capture: capture))
    }

    func testActiveClickRingsAndGradientHelpers() {
        let frame = StudioResolvedFrame(
            sceneIndex: 0,
            layout: .screen,
            screen: StudioResolvedScreen(
                rect: StudioRect(x: 0, y: 0, width: 100, height: 100),
                source: StudioRect(x: 0, y: 0, width: 1, height: 1),
                cornerRadius: 0,
                shadow: StudioResolvedShadow(blur: 0, offsetY: 0, opacity: 0)
            ),
            camera: nil
        )
        let events = StudioEvents(
            capture: StudioCaptureInfo(width: 100, height: 100, scale: 1),
            clicks: [
                StudioClickEvent(t: 0.8, x: 0.5, y: 0.5),
                StudioClickEvent(t: 0.1, x: 0.5, y: 0.5)
            ]
        )

        XCTAssertEqual(StudioRenderGeometry.activeClickRings(at: 1, events: events, frame: frame, overlay: StudioClickOverlay()).count, 1)
        let gradient = StudioRenderGeometry.gradientEndpoints(renderSize: CGSize(width: 200, height: 100))
        XCTAssertEqual(gradient.start, CGPoint(x: 0, y: 100))
        XCTAssertEqual(gradient.end, CGPoint(x: 200, y: 0))
    }

    // MARK: - Dividing a Piece at the Edges of a Track

    private func time(_ seconds: Double, _ timescale: CMTimeScale = 600) -> CMTime {
        CMTime(seconds: seconds, preferredTimescale: timescale)
    }

    private func range(_ start: Double, _ end: Double) -> CMTimeRange {
        CMTimeRange(start: time(start), end: time(end))
    }

    func testARangeIsDividedAtEveryEdgeInsideItInOrder() {
        let parts = StudioCompositionBuilder.divided(range(2, 8), at: [time(6.5, 48_000), time(3), time(7)])

        XCTAssertEqual(parts.map(\.start.seconds), [2, 3, 6.5, 7])
        XCTAssertEqual(parts.map(\.end.seconds), [3, 6.5, 7, 8])
        // The parts are the range again, with nothing between them.
        XCTAssertEqual(parts.first?.start, time(2))
        XCTAssertEqual(parts.last?.end, time(8))
        for (part, next) in zip(parts, parts.dropFirst()) {
            XCTAssertEqual(part.end, next.start)
        }
    }

    func testAnEdgeAtAnEndOfTheRangeOrOutsideItDividesNothing() {
        let whole = range(2, 8)

        XCTAssertEqual(StudioCompositionBuilder.divided(whole, at: []), [whole])
        XCTAssertEqual(StudioCompositionBuilder.divided(whole, at: [time(2), time(8), time(1), time(9)]), [whole])
        XCTAssertEqual(StudioCompositionBuilder.divided(whole, at: [.invalid, .positiveInfinity, .indefinite]), [whole])
    }

    func testAnEdgeNamedTwiceDividesOnce() {
        let parts = StudioCompositionBuilder.divided(range(2, 8), at: [time(5), time(5, 48_000), time(5)])

        XCTAssertEqual(parts.map(\.start.seconds), [2, 5])
        XCTAssertEqual(parts.map(\.end.seconds), [5, 8])
    }
}
