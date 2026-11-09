import XCTest
@testable import TinyClips

final class StudioInspectorPanelTests: XCTestCase {
    func testARecordingWithACameraHasEveryPanelInRailOrder() {
        XCTAssertEqual(
            StudioInspectorPanel.available(hasCamera: true),
            [.scene, .background, .screen, .camera, .zoom, .annotate, .cut, .speed, .captions, .audio, .project]
        )
        XCTAssertEqual(Set(StudioInspectorPanel.available(hasCamera: true)), Set(StudioInspectorPanel.allCases))
    }

    func testARecordingWithoutACameraHasNoSceneOrCameraPanel() {
        XCTAssertEqual(
            StudioInspectorPanel.available(hasCamera: false),
            [.background, .screen, .zoom, .annotate, .cut, .speed, .captions, .audio, .project]
        )
    }

    func testTheRailGroupsLookThenTimelineEditsThenTheRest() {
        XCTAssertEqual(
            StudioInspectorPanel.groups(hasCamera: true),
            [[.scene, .background, .screen, .camera], [.zoom, .annotate, .cut, .speed, .captions], [.audio, .project]]
        )
        XCTAssertEqual(
            StudioInspectorPanel.groups(hasCamera: false),
            [[.background, .screen], [.zoom, .annotate, .cut, .speed, .captions], [.audio, .project]]
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

    func testAButtonThatAddsWhatAPanelEditsHasThatPanelsSymbol() {
        XCTAssertEqual(StudioSymbol.addZoom, StudioInspectorPanel.zoom.symbolName)
        XCTAssertEqual(StudioSymbol.addCut, StudioInspectorPanel.cut.symbolName)
        XCTAssertEqual(StudioSymbol.addSpeed, StudioInspectorPanel.speed.symbolName)
        XCTAssertEqual(StudioSymbol.showScene, StudioInspectorPanel.scene.symbolName)
        XCTAssertNotEqual(StudioSymbol.trimStart, StudioSymbol.trimEnd)
        XCTAssertFalse(StudioSymbol.all.contains { $0.isEmpty })
    }

    func testEveryPanelHasATitleASymbolAndASummaryOfItsOwn() {
        let panels = StudioInspectorPanel.allCases
        XCTAssertEqual(Set(panels.map(\.title)).count, panels.count)
        XCTAssertEqual(Set(panels.map(\.symbolName)).count, panels.count)
        XCTAssertEqual(Set(panels.map(\.summary)).count, panels.count)
        XCTAssertFalse(panels.contains { $0.title.isEmpty || $0.symbolName.isEmpty || $0.summary.isEmpty })
    }

    // MARK: - Lanes

    private func laneProject(camera: Bool = true, sceneStarts: [Double] = [0]) -> StudioProject {
        StudioProject(
            id: "00000000-0000-0000-0000-000000000001",
            sources: StudioSources(
                screen: StudioScreenSource(width: 1920, height: 1080, duration: 60),
                camera: camera ? StudioCameraSource(width: 1280, height: 720, duration: 60) : nil
            ),
            scenes: sceneStarts.map { StudioScene(start: $0) }
        )
    }

    func testANewProjectShowsNoLanes() {
        XCTAssertEqual(StudioTimelineLane.shown(in: laneProject()), [])
        XCTAssertEqual(StudioTimelineLane.shown(in: laneProject(camera: false)), [])
    }

    func testTheSceneLaneNeedsACameraAndMoreThanOneScene() {
        XCTAssertTrue(StudioTimelineLane.scene.isShown(in: laneProject(sceneStarts: [0, 10])))
        XCTAssertFalse(StudioTimelineLane.scene.isShown(in: laneProject(sceneStarts: [0])))
        XCTAssertFalse(StudioTimelineLane.scene.isShown(in: laneProject(camera: false, sceneStarts: [0, 10])))

        // Two scenes at one time are one scene, as the format reads them.
        XCTAssertFalse(StudioTimelineLane.scene.isShown(in: laneProject(sceneStarts: [3, 3])))
    }

    func testALaneShowsWithSomethingOnItInTheOrderOfTheTimeline() {
        var project = laneProject(sceneStarts: [0, 10])
        project.zooms = [StudioZoom(start: 1, end: 2)]
        project.edits.cuts = [StudioTimeRange(start: 3, end: 4)]
        project.edits.speed = [StudioSpeedRange(start: 5, end: 6, rate: 2)]
        project.overlays.annotations = [StudioAnnotation(end: 1)]
        XCTAssertEqual(StudioTimelineLane.shown(in: project), [.scene, .zoom, .cut, .speed, .annotate])

        var cutOnly = laneProject()
        cutOnly.edits.cuts = [StudioTimeRange(start: 3, end: 4)]
        XCTAssertEqual(StudioTimelineLane.shown(in: cutOnly), [.cut])
    }
}
