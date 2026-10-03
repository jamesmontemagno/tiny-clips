import XCTest
@testable import TinyClips

final class StudioProjectTests: XCTestCase {
    private var directoryURL: URL!
    private let fixedDate = Date(timeIntervalSince1970: 1_800)

    override func setUpWithError() throws {
        directoryURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("TinyClipsStudioTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directoryURL, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        if let directoryURL = directoryURL {
            try? FileManager.default.removeItem(at: directoryURL)
        }
        directoryURL = nil
    }

    func testJSONRoundTripPreservesUnknownPropertiesAtAllLevels() throws {
        let json = """
        {
          "schemaVersion": 1,
          "id": "project-one",
          "rootUnknown": { "flag": true },
          "sources": {
            "screen": {
              "width": 1920,
              "height": 1080,
              "duration": 5,
              "screenUnknown": "kept"
            },
            "camera": null,
            "events": null
          },
          "scenes": [
            {
              "start": 0,
              "layout": "bubble",
              "sceneUnknown": 42
            }
          ]
        }
        """.data(using: .utf8)!

        let project = try StudioJSON.makeDecoder().decode(StudioProject.self, from: json)
        let encoded = try StudioJSON.makeEncoder().encode(project)
        let roundTripped = try StudioJSON.makeDecoder().decode(StudioProject.self, from: encoded)

        XCTAssertEqual(roundTripped.extra["rootUnknown"], .object(["flag": .bool(true)]))
        XCTAssertEqual(roundTripped.sources.screen.extra["screenUnknown"], .string("kept"))
        XCTAssertEqual(roundTripped.scenes[0].extra["sceneUnknown"], .number(42))
    }

    func testDefaultsAndTolerantEnums() throws {
        let project = try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "defaults",
          "sources": {
            "screen": {
              "width": 100,
              "height": 50,
              "duration": 9
            }
          },
          "canvas": { "aspect": "futureAspect" },
          "camera": { "shape": "futureShape" },
          "scenes": [ { "layout": "futureLayout" } ]
        }
        """)

        XCTAssertEqual(project.createdAt, Date(timeIntervalSince1970: 0))
        XCTAssertEqual(project.canvas.aspect, .auto)
        XCTAssertEqual(project.canvas.padding, 0.06)
        XCTAssertEqual(project.camera.shape, .circle)
        XCTAssertEqual(project.scenes[0].layout, .bubble)
        XCTAssertNil(project.sources.camera)
        XCTAssertNil(project.sources.events)
    }

    func testNullCountsAsMissingExceptForNullableProperties() throws {
        let project = try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "nulls",
          "app": null,
          "canvas": {
            "background": {
              "preset": null,
              "secondary": null,
              "image": null
            }
          },
          "camera": null,
          "scenes": [
            {
              "layout": null,
              "bubble": null,
              "split": null,
              "transition": null
            }
          ],
          "edits": {
            "trimEnd": null,
            "cuts": null,
            "speed": null
          },
          "audio": null,
          "overlays": null,
          "sources": {
            "screen": {
              "width": 100,
              "height": 50,
              "duration": 9,
              "file": null,
              "frameRate": null,
              "external": null
            },
            "camera": null,
            "events": null
          }
        }
        """)

        XCTAssertEqual(project.app, StudioAppInfo())
        XCTAssertEqual(project.camera, StudioCameraStyle())
        XCTAssertEqual(project.scenes[0].layout, .bubble)
        XCTAssertEqual(project.scenes[0].bubble, StudioBubble())
        XCTAssertEqual(project.scenes[0].split, StudioSplit())
        XCTAssertEqual(project.scenes[0].transition, StudioTransition())
        XCTAssertEqual(project.edits.cuts, [])
        XCTAssertEqual(project.edits.speed, [])
        XCTAssertNil(project.edits.trimEnd)
        XCTAssertEqual(project.audio, StudioAudio())
        XCTAssertEqual(project.overlays, StudioOverlays())
        XCTAssertEqual(project.sources.screen.file, "screen.mp4")
        XCTAssertEqual(project.sources.screen.frameRate, 30)
        XCTAssertFalse(project.sources.screen.external)
        XCTAssertNil(project.sources.camera)
        XCTAssertNil(project.sources.events)
        XCTAssertNil(project.canvas.background.preset)
        XCTAssertNil(project.canvas.background.secondary)
        XCTAssertNil(project.canvas.background.image)
    }

    func testNullRequiredPropertiesAreInvalid() {
        XCTAssertThrowsError(try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "bad",
          "sources": {
            "screen": {
              "width": null,
              "height": 50,
              "duration": 9
            }
          }
        }
        """)) { error in
            XCTAssertEqual(error as? StudioProjectError, .invalidProject("Missing required property width"))
        }

        XCTAssertThrowsError(try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "bad-camera",
          "sources": {
            "screen": {
              "width": 100,
              "height": 50,
              "duration": 9
            },
            "camera": {
              "width": null,
              "height": 50,
              "duration": 9
            }
          }
        }
        """)) { error in
            XCTAssertEqual(error as? StudioProjectError, .invalidProject("Missing required property width"))
        }
    }

    func testRequiredPropertiesAndUnsupportedSchemaVersion() {
        XCTAssertThrowsError(try decodeProject("""
        { "schemaVersion": 1, "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } } }
        """)) { error in
            XCTAssertEqual(error as? StudioProjectError, .invalidProject("Missing required property id"))
        }

        XCTAssertThrowsError(try decodeProject("""
        { "schemaVersion": 2, "id": "newer", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } } }
        """)) { error in
            XCTAssertEqual(error as? StudioProjectError, .unsupportedVersion(2))
        }
    }

    func testTimestampParsingAndWholeSecondFormatting() throws {
        let project = try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "dates",
          "createdAt": "2026-10-02T22:41:00.500Z",
          "modifiedAt": "2026-10-02T22:41:01Z",
          "lastOpenedAt": "2026-10-02T22:41:02Z",
          "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } }
        }
        """)

        let encoded = String(data: try StudioJSON.makeEncoder().encode(project), encoding: .utf8)!
        XCTAssertTrue(encoded.contains("\"createdAt\" : \"2026-10-02T22:41:00Z\""))
        XCTAssertTrue(encoded.contains("\"modifiedAt\" : \"2026-10-02T22:41:01Z\""))
    }

    func testStoreCreateLoadSaveListAndDelete() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let urls = try store.createProject(request: creationRequest())
        XCTAssertEqual(urls.screenURL.lastPathComponent, "screen.mp4")
        XCTAssertEqual(urls.cameraURL.lastPathComponent, "camera.mp4")
        XCTAssertEqual(urls.eventsURL.lastPathComponent, "events.json")

        var project = try store.load(id: urls.id)
        XCTAssertEqual(project.id, urls.id)
        XCTAssertEqual(project.edits.trimStart, 0.25)
        project.name = "Edited"
        try store.save(project)

        let summaries = try store.listSummaries()
        XCTAssertEqual(summaries.map(\.id), [urls.id])
        XCTAssertEqual(summaries[0].name, "Edited")
        XCTAssertTrue(summaries[0].isDraft)

        try store.delete(id: urls.id)
        XCTAssertTrue(try store.listSummaries().isEmpty)
    }

    func testExportIndexUpdatesAndRemovesPathsCaseInsensitively() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let urls = try store.createProject(request: creationRequest(camera: nil))

        try store.recordExport(id: urls.id, path: "C:\\Clips\\Output.MP4")
        XCTAssertEqual(try store.findProject(exportedPath: "c:\\clips\\output.mp4")?.id, urls.id)

        try store.updateExportPath(from: "C:\\CLIPS\\OUTPUT.mp4", to: "D:\\Moved.mp4")
        XCTAssertNil(try store.findProject(exportedPath: "C:\\Clips\\Output.mp4"))
        XCTAssertEqual(try store.findProject(exportedPath: "d:\\moved.mp4")?.id, urls.id)

        try store.removeExportPath("D:\\MOVED.mp4")
        XCTAssertNil(try store.findProject(exportedPath: "d:\\moved.mp4"))
    }

    func testFlatProjectsAreUniquePerExternalVideo() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let videoURL = directoryURL.appendingPathComponent("external.mp4")
        XCTAssertTrue(FileManager.default.createFile(atPath: videoURL.path, contents: Data()))

        let first = try store.getOrCreateFlatProject(request: flatRequest(videoURL: videoURL))
        let second = try store.getOrCreateFlatProject(request: flatRequest(videoURL: URL(fileURLWithPath: videoURL.path.uppercased())))

        XCTAssertEqual(first.id, second.id)
        XCTAssertTrue(first.sources.screen.external)
        XCTAssertNil(first.sources.camera)
        XCTAssertNil(first.sources.events)
    }

    func testEventsLoadAndSave() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let urls = try store.createProject(request: creationRequest(camera: nil))
        let events = StudioEvents(clicks: [StudioClickEvent(t: 1, x: 0.2, y: 0.3, button: .right)])

        try store.saveEvents(events, id: urls.id)

        XCTAssertEqual(try store.loadEvents(id: urls.id), events)
    }

    func testCleanupPlannerHonorsAgeBoundaryAndEligibility() {
        let now = Date(timeIntervalSince1970: 100 * 24 * 60 * 60)
        let exactlyThirtyDaysOld = summary(id: "exact", lastOpenedAt: now.addingTimeInterval(-30 * 24 * 60 * 60), isDraft: false)
        let older = summary(id: "older", lastOpenedAt: now.addingTimeInterval(-(30 * 24 * 60 * 60 + 1)), isDraft: false)
        let draft = summary(id: "draft", lastOpenedAt: now.addingTimeInterval(-60 * 24 * 60 * 60), isDraft: true)
        let pinned = summary(id: "pinned", lastOpenedAt: now.addingTimeInterval(-60 * 24 * 60 * 60), isDraft: false, keepSources: true)

        let ids = StudioCleanupPolicy.plan(summaries: [exactlyThirtyDaysOld, older, draft, pinned], currentDate: now)

        XCTAssertEqual(ids, ["older"])
    }

    func testCleanupPlannerSizeOrderingAndDisabledRules() {
        let now = Date(timeIntervalSince1970: 1_000)
        let newest = summary(id: "newest", lastOpenedAt: now, isDraft: false, sizeOnDisk: 60)
        let oldest = summary(id: "oldest", lastOpenedAt: now.addingTimeInterval(-20), isDraft: false, sizeOnDisk: 40)
        let middle = summary(id: "middle", lastOpenedAt: now.addingTimeInterval(-10), isDraft: false, sizeOnDisk: 30)

        XCTAssertEqual(
            StudioCleanupPolicy.plan(
                summaries: [newest, oldest, middle],
                currentDate: now,
                options: StudioCleanupOptions(retentionDays: 0, sizeCapBytes: 70)
            ),
            ["oldest", "middle"]
        )
        XCTAssertTrue(
            StudioCleanupPolicy.plan(
                summaries: [summary(id: "old", lastOpenedAt: now.addingTimeInterval(-10_000), isDraft: false)],
                currentDate: now,
                options: StudioCleanupOptions(retentionDays: 0, sizeCapBytes: 0)
            ).isEmpty
        )
    }

    func testCleanupDeletesFlatProjectWithMissingFile() {
        let now = Date(timeIntervalSince1970: 1_000)
        let flat = summary(id: "flat", lastOpenedAt: now, isDraft: true, isFlat: true, sourceExists: false)

        XCTAssertEqual(StudioCleanupPolicy.plan(summaries: [flat], currentDate: now), ["flat"])
    }

    // MARK: - Helpers

    private func decodeProject(_ json: String) throws -> StudioProject {
        try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(json.utf8))
    }

    private func creationRequest(camera: StudioCameraCreationInfo? = StudioCameraCreationInfo(width: 640, height: 480, duration: 10, startOffset: 0.25)) -> StudioProjectCreationRequest {
        StudioProjectCreationRequest(
            name: "Test",
            screenWidth: 1920,
            screenHeight: 1080,
            screenDuration: 10,
            camera: camera,
            bubbleAnchor: .topLeft,
            clickOverlay: StudioClickOverlay(enabled: true, color: "#FF0000"),
            branding: true,
            appVersion: "1.0"
        )
    }

    private func flatRequest(videoURL: URL) -> StudioFlatProjectRequest {
        StudioFlatProjectRequest(videoURL: videoURL, width: 1920, height: 1080, duration: 5, appVersion: "1.0")
    }

    private func summary(
        id: String,
        lastOpenedAt: Date,
        isDraft: Bool,
        isFlat: Bool = false,
        keepSources: Bool = false,
        sizeOnDisk: Int64 = 1,
        sourceExists: Bool = true
    ) -> StudioProjectSummary {
        StudioProjectSummary(
            id: id,
            name: id,
            createdAt: Date(timeIntervalSince1970: 0),
            lastOpenedAt: lastOpenedAt,
            isDraft: isDraft,
            isFlat: isFlat,
            keepSources: keepSources,
            sizeOnDisk: sizeOnDisk,
            sourceExists: sourceExists
        )
    }
}
