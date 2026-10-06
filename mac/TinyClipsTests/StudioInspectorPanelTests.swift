import XCTest
@testable import TinyClips

final class StudioInspectorPanelTests: XCTestCase {
    func testARecordingWithACameraHasEveryPanelInRailOrder() {
        XCTAssertEqual(
            StudioInspectorPanel.available(hasCamera: true),
            [.scene, .background, .screen, .camera, .zoom, .cut, .speed, .audio, .project]
        )
        XCTAssertEqual(Set(StudioInspectorPanel.available(hasCamera: true)), Set(StudioInspectorPanel.allCases))
    }

    func testARecordingWithoutACameraHasNoSceneOrCameraPanel() {
        XCTAssertEqual(
            StudioInspectorPanel.available(hasCamera: false),
            [.background, .screen, .zoom, .cut, .speed, .audio, .project]
        )
    }

    func testTheRailGroupsLookThenTimelineEditsThenTheRest() {
        XCTAssertEqual(
            StudioInspectorPanel.groups(hasCamera: true),
            [[.scene, .background, .screen, .camera], [.zoom, .cut, .speed], [.audio, .project]]
        )
        XCTAssertEqual(
            StudioInspectorPanel.groups(hasCamera: false),
            [[.background, .screen], [.zoom, .cut, .speed], [.audio, .project]]
        )
    }

    func testAProjectOpensOnAPanelItHas() {
        XCTAssertEqual(StudioInspectorPanel.initial(hasCamera: true), .scene)
        XCTAssertEqual(StudioInspectorPanel.initial(hasCamera: false), .background)
    }

    func testAPanelTheRecordingLacksResolvesToItsNeighbor() {
        XCTAssertEqual(StudioInspectorPanel.resolved(.scene, hasCamera: false), .background)
        XCTAssertEqual(StudioInspectorPanel.resolved(.camera, hasCamera: false), .screen)
        for hasCamera in [true, false] {
            let available = StudioInspectorPanel.available(hasCamera: hasCamera)
            for panel in StudioInspectorPanel.allCases {
                let resolved = StudioInspectorPanel.resolved(panel, hasCamera: hasCamera)
                XCTAssertTrue(available.contains(resolved))
                if available.contains(panel) {
                    XCTAssertEqual(resolved, panel)
                }
            }
        }
    }

    func testEveryPanelHasATitleASymbolAndASummaryOfItsOwn() {
        let panels = StudioInspectorPanel.allCases
        XCTAssertEqual(Set(panels.map(\.title)).count, panels.count)
        XCTAssertEqual(Set(panels.map(\.symbolName)).count, panels.count)
        XCTAssertEqual(Set(panels.map(\.summary)).count, panels.count)
        XCTAssertFalse(panels.contains { $0.title.isEmpty || $0.symbolName.isEmpty || $0.summary.isEmpty })
    }
}
