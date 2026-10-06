import XCTest
@testable import TinyClips

final class StudioProjectTests: XCTestCase {
    private var directoryURL: URL!
    private let fixedDate = Date(timeIntervalSince1970: 1_800)
    private let validID = "3f0013cf-ba10-4453-af91-792b7882dae6"
    private let otherID = "11111111-2222-3333-4444-555555555555"

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
          "id": "\(validID)",
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
          "id": "\(validID)",
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
          "id": "\(validID)",
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

    func testTheSoundTrackListIsReadAndWritten_AndLeftOutWhenTheProjectDoesNotSay() throws {
        let project = try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "\(validID)",
          "sources": {
            "screen": {
              "width": 100,
              "height": 50,
              "duration": 9,
              "audioTracks": ["system", null, "microphone", "somethingNew"],
              "screenUnknown": "kept"
            }
          }
        }
        """)

        // A null entry is dropped, as in every list. A word that is not known is kept as it is.
        XCTAssertEqual(project.sources.screen.audioTracks, ["system", "microphone", "somethingNew"])
        XCTAssertNil(project.sources.screen.extra["audioTracks"])
        XCTAssertEqual(project.sources.screen.extra["screenUnknown"], .string("kept"))

        let screen = try screenObject(of: project)
        XCTAssertEqual(screen["audioTracks"] as? [String], ["system", "microphone", "somethingNew"])
        XCTAssertEqual(screen["screenUnknown"] as? String, "kept")

        // An empty list says the file has no sound, and is written as that.
        var silent = project
        silent.sources.screen.audioTracks = []
        let silentScreen = try screenObject(of: silent)
        XCTAssertEqual(silentScreen["audioTracks"] as? [String], [])

        // A project that does not say stays one that does not say.
        for json in [#""audioTracks": null,"#, ""] {
            let unknown = try decodeProject("""
            {
              "schemaVersion": 1,
              "id": "\(validID)",
              "sources": { "screen": { \(json) "width": 100, "height": 50, "duration": 9 } }
            }
            """)
            XCTAssertNil(unknown.sources.screen.audioTracks)
            XCTAssertNil(try screenObject(of: unknown)["audioTracks"])
        }
    }

    func testARecordingsSoundTracksGoIntoItsProject() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })

        var request = creationRequest()
        request.screenAudioTracks = ["system", "microphone"]
        let listed = try store.completeRecording(id: store.beginRecording().id, request: request)
        XCTAssertEqual(listed.sources.screen.audioTracks, ["system", "microphone"])
        XCTAssertEqual(try store.load(id: listed.id).sources.screen.audioTracks, ["system", "microphone"])

        // A recorder that cannot say leaves it out.
        let unknown = try store.completeRecording(id: store.beginRecording().id, request: creationRequest())
        XCTAssertNil(unknown.sources.screen.audioTracks)
        XCTAssertEqual(unknown.audio, StudioAudio())
    }

    func testANewProjectHasASceneForEachCornerTheCameraWasMovedTo() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        var request = creationRequest(camera: StudioCameraCreationInfo(width: 640, height: 480, duration: 10, startOffset: 0.25))
        request.bubbleAnchor = .topRight
        request.cameraCorners = [
            StudioCameraCornerEvent(t: 0, corner: .topRight),
            StudioCameraCornerEvent(t: 3, corner: .bottomLeft),
            StudioCameraCornerEvent(t: 7.5, corner: .topLeft),
        ]

        let project = try store.completeRecording(id: store.beginRecording().id, request: request)
        XCTAssertEqual(project.scenes.map(\.start), [0, 3, 7.5])
        XCTAssertEqual(project.scenes.map(\.bubble.anchor), [.topRight, .bottomLeft, .topLeft])
        XCTAssertEqual(project.scenes.map(\.layout), [.bubble, .bubble, .bubble])
        XCTAssertEqual(project.scenes.map(\.transition.kind), [.cut, .morph, .morph])
        XCTAssertEqual(project.edits.trimStart, 0.25)
        XCTAssertEqual(try store.load(id: project.id).scenes, project.scenes)

        // The recording's length is what decides which changes come too late.
        var shorter = request
        shorter.screenDuration = 7.6
        XCTAssertEqual(try store.completeRecording(id: store.beginRecording().id, request: shorter).scenes.map(\.start), [0, 3])

        // Layouts chosen while recording become scenes as well.
        var withLayouts = request
        withLayouts.layoutMarkers = [StudioLayoutMarker(t: 5, layout: .camera)]
        let laidOut = try store.completeRecording(id: store.beginRecording().id, request: withLayouts)
        XCTAssertEqual(laidOut.scenes.map(\.start), [0, 3, 5])
        XCTAssertEqual(laidOut.scenes[2].layout, .camera)
        XCTAssertEqual(laidOut.scenes[2].bubble.anchor, .topLeft)
    }

    func testANewProjectWithoutACameraHasItsOneScreenSceneWhateverWasChanged() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        var request = creationRequest(camera: nil)
        request.cameraCorners = [StudioCameraCornerEvent(t: 0, corner: .topRight), StudioCameraCornerEvent(t: 4, corner: .topLeft)]
        request.layoutMarkers = [StudioLayoutMarker(t: 6, layout: .camera)]

        let project = try store.completeRecording(id: store.beginRecording().id, request: request)
        XCTAssertEqual(project.scenes.map(\.start), [0])
        XCTAssertEqual(project.scenes[0].layout, .screen)

        // A request that says nothing about corners gives the one scene, as before.
        let plain = try store.completeRecording(id: store.beginRecording().id, request: creationRequest())
        XCTAssertEqual(plain.scenes.map(\.start), [0])
        XCTAssertEqual(plain.scenes[0].bubble.anchor, .topLeft)
        XCTAssertEqual(plain.scenes[0].layout, .bubble)
    }

    func testNullRequiredPropertiesAreInvalid() {
        XCTAssertThrowsError(try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "\(validID)",
          "sources": {
            "screen": {
              "width": null,
              "height": 50,
              "duration": 9
            }
          }
        }
        """))

        XCTAssertThrowsError(try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "\(validID)",
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
        """))
    }

    func testRequiredPropertiesAndUnsupportedSchemaVersion() {
        XCTAssertThrowsError(try decodeProject("""
        { "schemaVersion": 1, "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } } }
        """))

        XCTAssertThrowsError(try decodeProject("""
        { "schemaVersion": 2, "id": "\(validID)", "sources": { "screen": { "width": 1, "height": 1, "duration": 1 } } }
        """)) { error in
            XCTAssertEqual(error as? StudioProjectError, .unsupportedVersion(2))
        }
    }

    func testReaderRejectsInvalidDimensionsDurationsAndSourceFileNames() {
        let invalidSnippets = [
            "\"width\": 0, \"height\": 50, \"duration\": 9",
            "\"width\": 100, \"height\": 0, \"duration\": 9",
            "\"width\": 100, \"height\": 50, \"duration\": -1",
            "\"width\": 100, \"height\": 50, \"duration\": 9, \"file\": \"..\"",
            "\"width\": 100, \"height\": 50, \"duration\": 9, \"file\": \"nested/screen.mp4\"",
            "\"width\": 100, \"height\": 50, \"duration\": 9, \"file\": \"nested\\\\screen.mp4\""
        ]

        for snippet in invalidSnippets {
            XCTAssertThrowsError(try decodeProject("""
            { "schemaVersion": 1, "id": "\(validID)", "sources": { "screen": { \(snippet) } } }
            """), snippet)
        }

        XCTAssertThrowsError(try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "\(validID)",
          "sources": {
            "screen": { "width": 100, "height": 50, "duration": 9 },
            "camera": { "width": 100, "height": 50, "duration": 9, "file": "../camera.mp4" }
          }
        }
        """))

        XCTAssertThrowsError(try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "\(validID)",
          "sources": {
            "screen": { "width": 100, "height": 50, "duration": 9 },
            "events": "nested/events.json"
          }
        }
        """))
    }

    func testNullArrayElementsAreDropped() throws {
        let project = try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "\(validID)",
          "sources": { "screen": { "width": 100, "height": 50, "duration": 9 } },
          "scenes": [ null, { "layout": "screen" } ],
          "zooms": [ null, { "start": 1, "end": 2 } ],
          "edits": {
            "cuts": [ null, { "start": 1, "end": 2 } ],
            "speed": [ null, { "start": 3, "end": 4, "rate": 2 } ]
          },
          "exports": [ null, { "path": "/tmp/out.mp4" } ]
        }
        """)

        XCTAssertEqual(project.scenes.count, 1)
        XCTAssertEqual(project.zooms.count, 1)
        XCTAssertEqual(project.edits.cuts.count, 1)
        XCTAssertEqual(project.edits.speed.count, 1)
        XCTAssertEqual(project.exports.count, 1)

        let events = try StudioJSON.makeDecoder().decode(StudioEvents.self, from: Data("""
        {
          "schemaVersion": 1,
          "clicks": [ null, { "t": 1, "x": 0.2, "y": 0.3 } ],
          "cursor": [ null, { "t": 2, "x": 0.4, "y": 0.5 } ],
          "cameraCorners": [ null, { "t": 3, "corner": "topLeft" } ],
          "markers": [ null, { "future": true } ]
        }
        """.utf8))
        XCTAssertEqual(events.clicks.count, 1)
        XCTAssertEqual(events.cursor.count, 1)
        XCTAssertEqual(events.cameraCorners.count, 1)
        XCTAssertEqual(events.markers.count, 1)
    }

    func testTimestampParsingAndWholeSecondFormatting() throws {
        let project = try decodeProject("""
        {
          "schemaVersion": 1,
          "id": "\(validID)",
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

    func testStoreRejectsPathTraversalIDsBeforeBuildingURLs() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let invalidID = "../\(validID)"

        XCTAssertThrowsError(try store.completeRecording(id: invalidID, request: creationRequest()))
        XCTAssertThrowsError(try store.paths(forID: invalidID))
        XCTAssertFalse(store.exists(id: invalidID))
        XCTAssertThrowsError(try store.load(id: invalidID))
        XCTAssertThrowsError(try store.save(makeProject(id: invalidID)))
        XCTAssertThrowsError(try store.paths(for: makeProject(id: invalidID)))
        XCTAssertThrowsError(try store.delete(id: invalidID))
        XCTAssertThrowsError(try store.markOpened(id: invalidID))
        XCTAssertThrowsError(try store.recordExport(id: invalidID, path: "/tmp/out.mp4"))
        XCTAssertThrowsError(try store.loadEvents(id: invalidID))
        XCTAssertThrowsError(try store.saveEvents(StudioEvents(), id: invalidID))
        XCTAssertFalse(FileManager.default.fileExists(atPath: directoryURL.deletingLastPathComponent().appendingPathComponent(validID).path))
    }

    func testBeginRecordingCompleteRecordingAndPaths() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let started = try store.beginRecording()

        XCTAssertTrue(store.exists(id: started.id))
        XCTAssertFalse(FileManager.default.fileExists(atPath: started.projectJSONURL.path))
        XCTAssertEqual(started.screenURL.lastPathComponent, "screen.mp4")
        XCTAssertEqual(started.cameraURL?.lastPathComponent, "camera.mp4")
        XCTAssertEqual(started.eventsURL.lastPathComponent, "events.json")
        XCTAssertEqual(started.posterURL.lastPathComponent, "poster.jpg")

        let project = try store.completeRecording(id: started.id, request: creationRequest())
        XCTAssertEqual(project.id, started.id)
        XCTAssertEqual(project.edits.trimStart, 0.25)

        let normalPaths = try store.paths(for: project)
        XCTAssertEqual(normalPaths.cameraURL?.lastPathComponent, "camera.mp4")
        XCTAssertEqual(normalPaths.screenURL.lastPathComponent, "screen.mp4")

        let flatVideo = directoryURL.appendingPathComponent("external.mp4")
        XCTAssertTrue(FileManager.default.createFile(atPath: flatVideo.path, contents: Data()))
        let flat = try store.getOrCreateFlatProject(request: flatRequest(videoURL: flatVideo))
        let flatPaths = try store.paths(for: flat)
        XCTAssertEqual(flatPaths.screenURL.path, flatVideo.standardizedFileURL.path)
        XCTAssertNil(flatPaths.cameraURL)
        XCTAssertNotNil((try store.paths(forID: flat.id)).cameraURL)
    }

    func testFolderNameWinsOverProjectJSONIDAndNonIDFoldersAreIgnored() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let hostileID = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        try writeProject(makeProject(id: hostileID), folderID: validID)
        try writeProject(makeProject(id: otherID), folderID: "not-a-project-id")

        let loaded = try store.load(id: validID)
        XCTAssertEqual(loaded.id, validID)
        XCTAssertEqual(try store.listSummaries().map(\.id), [validID])

        var project = loaded
        // A video that is where it was exported to. Without it the project would be the only
        // copy of the recording, and cleanup would leave it alone.
        let exportURL = directoryURL.appendingPathComponent("out.mp4")
        XCTAssertTrue(FileManager.default.createFile(atPath: exportURL.path, contents: Data([7, 8, 9])))
        project.exports = [StudioExport(path: exportURL.path)]
        project.lastOpenedAt = fixedDate.addingTimeInterval(-60 * 24 * 60 * 60)
        _ = try store.save(project)
        XCTAssertEqual(try store.cleanup(), [validID])
        XCTAssertFalse(FileManager.default.fileExists(atPath: directoryURL.appendingPathComponent(validID).path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: directoryURL.appendingPathComponent("not-a-project-id").path))
    }

    func testStoreSaveListDeleteAndMarkOpenedDoesNotTouchModifiedAt() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let paths = try store.beginRecording()
        var project = try store.completeRecording(id: paths.id, request: creationRequest())
        project.name = "Edited"
        let saved = try store.save(project)

        let openedDate = fixedDate.addingTimeInterval(60)
        let openedStore = StudioProjectStore(rootURL: directoryURL, now: { openedDate })
        let opened = try openedStore.markOpened(id: paths.id)

        XCTAssertEqual(opened.modifiedAt, saved.modifiedAt)
        XCTAssertEqual(opened.lastOpenedAt, openedDate)
        let summaries = try openedStore.listSummaries()
        XCTAssertEqual(summaries.map(\.id), [paths.id])
        XCTAssertEqual(summaries[0].name, "Edited")
        XCTAssertTrue(summaries[0].isDraft)

        try openedStore.delete(id: paths.id)
        XCTAssertTrue(try openedStore.listSummaries().isEmpty)
    }

    func testExportLinksReplaceStealUpdateRemoveAndPersistAcrossStores() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let first = try store.completeRecording(id: validID, request: creationRequest(camera: nil))
        _ = try store.completeRecording(id: otherID, request: creationRequest(camera: nil))

        _ = try store.recordExport(id: first.id, path: "C:\\Clips\\Output.MP4")
        _ = try store.recordExport(id: first.id, path: "c:\\clips\\output.mp4")
        XCTAssertEqual(try store.load(id: first.id).exports.count, 1)
        XCTAssertEqual(try store.findProjectID(exportedPath: "C:\\CLIPS\\OUTPUT.mp4"), first.id)

        _ = try store.recordExport(id: otherID, path: "C:\\CLIPS\\OUTPUT.mp4")
        XCTAssertEqual(try store.findProjectID(exportedPath: "c:\\clips\\output.mp4"), otherID)
        XCTAssertTrue(try store.load(id: first.id).exports.isEmpty)

        XCTAssertTrue(try store.updateExportPath(from: "C:\\clips\\output.mp4", to: "D:\\Moved.mp4"))
        XCTAssertNil(try store.findProjectID(exportedPath: "C:\\Clips\\Output.mp4"))
        XCTAssertEqual(try store.findProjectID(exportedPath: "d:\\moved.mp4"), otherID)

        let secondStore = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        XCTAssertEqual(try secondStore.findProjectID(exportedPath: "D:\\MOVED.mp4"), otherID)
        XCTAssertTrue(try secondStore.removeExportPath("d:\\moved.mp4"))
        XCTAssertNil(try secondStore.findProjectID(exportedPath: "D:\\Moved.mp4"))
    }

    func testExportedPathIndexListsEveryLinkUnderItsComparisonKey() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        XCTAssertTrue(try store.exportedPathIndex().isEmpty)

        _ = try store.completeRecording(id: validID, request: creationRequest(camera: nil))
        _ = try store.completeRecording(id: otherID, request: creationRequest(camera: nil))
        XCTAssertTrue(try store.exportedPathIndex().isEmpty)

        let firstPath = directoryURL.appendingPathComponent("Clip One.MP4").path
        let secondPath = directoryURL.appendingPathComponent("Clip Two.mp4").path
        let thirdPath = directoryURL.appendingPathComponent("Clip Three.mp4").path
        _ = try store.recordExport(id: validID, path: firstPath)
        _ = try store.recordExport(id: validID, path: secondPath)
        _ = try store.recordExport(id: otherID, path: thirdPath)

        let index = try store.exportedPathIndex()
        XCTAssertEqual(index.count, 3)
        let firstPathInOtherCase = directoryURL.appendingPathComponent("CLIP ONE.mp4").path
        XCTAssertEqual(index[StudioProjectStore.exportKey(forPath: firstPathInOtherCase)], validID)
        XCTAssertEqual(index[StudioProjectStore.exportKey(forPath: secondPath)], validID)
        XCTAssertEqual(index[StudioProjectStore.exportKey(forPath: thirdPath)], otherID)
        XCTAssertNil(index[StudioProjectStore.exportKey(forPath: directoryURL.appendingPathComponent("Other.mp4").path)])
    }

    func testCleanupOptionsFromSettingsValuesTurnRulesOffAtZero() {
        let defaults = StudioCleanupOptions(retentionDays: 30, sizeCapGigabytes: 10)
        XCTAssertEqual(defaults, StudioCleanupOptions())
        XCTAssertEqual(defaults.sizeCapBytes, 10_737_418_240)

        let off = StudioCleanupOptions(retentionDays: 0, sizeCapGigabytes: 0)
        XCTAssertEqual(off.retentionDays, 0)
        XCTAssertEqual(off.sizeCapBytes, 0)

        let negative = StudioCleanupOptions(retentionDays: -5, sizeCapGigabytes: -1)
        XCTAssertEqual(negative, off)
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

    func testEventsDefaultsMissingFileSaveAndUnsupportedVersion() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let project = try store.completeRecording(id: validID, request: creationRequest(camera: nil))
        XCTAssertEqual(try store.loadEvents(id: project.id), StudioEvents())

        let events = StudioEvents(clicks: [StudioClickEvent(t: 1, x: 0.2, y: 0.3, button: .right)])
        try store.saveEvents(events, id: project.id)
        XCTAssertEqual(try store.loadEvents(id: project.id), events)

        XCTAssertThrowsError(try StudioJSON.makeDecoder().decode(StudioEvents.self, from: Data("""
        { "schemaVersion": 2 }
        """.utf8))) { error in
            XCTAssertEqual(error as? StudioProjectError, .unsupportedVersion(2))
        }
    }

    func testCleanupPlannerHonorsAgeBoundaryEligibilityAndSizeAfterAgeRule() {
        let now = Date(timeIntervalSince1970: 100 * 24 * 60 * 60)
        let exactlyThirtyDaysOld = summary(id: "exact", lastOpenedAt: now.addingTimeInterval(-30 * 24 * 60 * 60), isDraft: false)
        let older = summary(id: "older", lastOpenedAt: now.addingTimeInterval(-(30 * 24 * 60 * 60 + 1)), isDraft: false)
        let draft = summary(id: "draft", lastOpenedAt: now.addingTimeInterval(-60 * 24 * 60 * 60), isDraft: true)
        let pinned = summary(id: "pinned", lastOpenedAt: now.addingTimeInterval(-60 * 24 * 60 * 60), isDraft: false, keepSources: true)

        XCTAssertEqual(StudioCleanupPolicy.plan(summaries: [exactlyThirtyDaysOld, older, draft, pinned], currentDate: now), ["older"])

        let a = summary(id: "a", lastOpenedAt: now.addingTimeInterval(-40 * 24 * 60 * 60), isDraft: false, sizeOnDisk: 5)
        let b = summary(id: "b", lastOpenedAt: now.addingTimeInterval(-2), isDraft: false, sizeOnDisk: 4)
        let c = summary(id: "c", lastOpenedAt: now.addingTimeInterval(-1), isDraft: false, sizeOnDisk: 3)
        XCTAssertEqual(
            StudioCleanupPolicy.plan(
                summaries: [a, b, c],
                currentDate: now,
                options: StudioCleanupOptions(retentionDays: 30, sizeCapBytes: 10)
            ),
            ["a"]
        )
    }

    func testCleanupInUseProjectsAreExcludedFromEveryRuleButStillCountTowardSize() {
        let now = Date(timeIntervalSince1970: 1_000)
        let old = summary(id: "old", lastOpenedAt: now.addingTimeInterval(-10_000), isDraft: false, sizeOnDisk: 60)
        let flat = summary(id: "flat", lastOpenedAt: now, isDraft: true, isFlat: true, sizeOnDisk: 60, sourceExists: false)
        let sizeOld = summary(id: "size-a", lastOpenedAt: now.addingTimeInterval(-20), isDraft: false, sizeOnDisk: 40)
        let sizeNew = summary(id: "size-b", lastOpenedAt: now.addingTimeInterval(-10), isDraft: false, sizeOnDisk: 40)

        XCTAssertEqual(
            StudioCleanupPolicy.plan(
                summaries: [old],
                currentDate: now,
                options: StudioCleanupOptions(retentionDays: 1, sizeCapBytes: 0),
                inUseProjectIDs: ["old"]
            ),
            []
        )
        XCTAssertEqual(
            StudioCleanupPolicy.plan(summaries: [flat], currentDate: now, inUseProjectIDs: ["flat"]),
            []
        )
        XCTAssertEqual(
            StudioCleanupPolicy.plan(
                summaries: [old, sizeOld, sizeNew],
                currentDate: now,
                options: StudioCleanupOptions(retentionDays: 0, sizeCapBytes: 90),
                inUseProjectIDs: ["old"]
            ),
            ["size-a", "size-b"]
        )
    }

    func testCleanupDeletesMissingFlatAndUnfinishedRecordingsOnlyWhenOldAndNotInUse() throws {
        let oldID = validID
        let youngID = otherID
        let inUseID = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        let folderDates = [
            oldID: fixedDate.addingTimeInterval(-(25 * 60 * 60)),
            youngID: fixedDate.addingTimeInterval(-(23 * 60 * 60)),
            inUseID: fixedDate.addingTimeInterval(-(25 * 60 * 60))
        ]
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate }, folderDateProvider: { url in
            folderDates[url.lastPathComponent]
        })
        try FileManager.default.createDirectory(at: directoryURL.appendingPathComponent(oldID), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: directoryURL.appendingPathComponent(youngID), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: directoryURL.appendingPathComponent(inUseID), withIntermediateDirectories: true)

        let flat = makeProject(id: "bbbbbbbb-cccc-dddd-eeee-ffffffffffff")
        var flatProject = flat
        flatProject.sources.screen.external = true
        flatProject.sources.screen.file = directoryURL.appendingPathComponent("missing.mp4").path
        try writeProject(flatProject, folderID: flatProject.id)

        let deleted = try store.cleanup(inUseProjectIDs: [inUseID])
        XCTAssertEqual(Set(deleted), [oldID, flatProject.id])
        XCTAssertFalse(FileManager.default.fileExists(atPath: directoryURL.appendingPathComponent(oldID).path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: directoryURL.appendingPathComponent(youngID).path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: directoryURL.appendingPathComponent(inUseID).path))
    }

    func testCleanupLeavesAProjectAnEditorOpenedAfterTheListOfThoseInUseWasMade() throws {
        // Two folders that the cleanup would delete. Which rule selects a project makes no
        // difference here: every project that is to go is looked at once more before it does.
        let openedID = validID
        let untouchedID = otherID
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate }, folderDateProvider: { _ in
            self.fixedDate.addingTimeInterval(-(25 * 60 * 60))
        })
        let openedURL = directoryURL.appendingPathComponent(openedID)
        let untouchedURL = directoryURL.appendingPathComponent(untouchedID)
        try FileManager.default.createDirectory(at: openedURL, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: untouchedURL, withIntermediateDirectories: true)

        // The list names nothing: it was made before the editor opened the first of the two.
        store.beginUse(id: openedID)
        XCTAssertEqual(try store.cleanup(inUseProjectIDs: []), [untouchedID])
        XCTAssertTrue(FileManager.default.fileExists(atPath: openedURL.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: untouchedURL.path))

        // Once the editor has closed, the project is the cleanup's to remove again.
        store.endUse(id: openedID)
        XCTAssertEqual(try store.cleanup(inUseProjectIDs: []), [openedID])
        XCTAssertFalse(FileManager.default.fileExists(atPath: openedURL.path))
    }

    func testCleanupSizeOrderingTiesAndDisabledRules() {
        let now = Date(timeIntervalSince1970: 1_000)
        let newest = summary(id: "newest", lastOpenedAt: now, isDraft: false, sizeOnDisk: 60)
        let oldestB = summary(id: "b", lastOpenedAt: now.addingTimeInterval(-20), isDraft: false, sizeOnDisk: 40)
        let oldestA = summary(id: "a", lastOpenedAt: now.addingTimeInterval(-20), isDraft: false, sizeOnDisk: 30)

        XCTAssertEqual(
            StudioCleanupPolicy.plan(
                summaries: [newest, oldestB, oldestA],
                currentDate: now,
                options: StudioCleanupOptions(retentionDays: 0, sizeCapBytes: 70)
            ),
            ["a", "b"]
        )
        XCTAssertTrue(
            StudioCleanupPolicy.plan(
                summaries: [summary(id: "old", lastOpenedAt: now.addingTimeInterval(-10_000), isDraft: false)],
                currentDate: now,
                options: StudioCleanupOptions(retentionDays: 0, sizeCapBytes: 0)
            ).isEmpty
        )
    }

    func testTheStorageLimitCountsOnlyWhatCleanupMayRemove() {
        let now = Date(timeIntervalSince1970: 1_000)
        // Each of the four that cleanup never removes is over the limit by itself.
        let draft = summary(id: "draft", lastOpenedAt: now.addingTimeInterval(-90), isDraft: true, sizeOnDisk: 500)
        let pinned = summary(id: "pinned", lastOpenedAt: now.addingTimeInterval(-80), isDraft: false, keepSources: true, sizeOnDisk: 500)
        let flat = summary(id: "flat", lastOpenedAt: now.addingTimeInterval(-70), isDraft: false, isFlat: true, sizeOnDisk: 500)
        let videoGone = summary(id: "video-gone", lastOpenedAt: now.addingTimeInterval(-60), isDraft: false, sizeOnDisk: 500, exportMissing: true)
        let oldest = summary(id: "oldest", lastOpenedAt: now.addingTimeInterval(-30), isDraft: false, sizeOnDisk: 40)
        let middle = summary(id: "middle", lastOpenedAt: now.addingTimeInterval(-20), isDraft: false, sizeOnDisk: 30)
        let newest = summary(id: "newest", lastOpenedAt: now.addingTimeInterval(-10), isDraft: false, sizeOnDisk: 60)
        let options = StudioCleanupOptions(retentionDays: 0, sizeCapBytes: 100)

        XCTAssertEqual(
            StudioCleanupPolicy.plan(summaries: [draft, pinned, flat, videoGone, oldest, newest], currentDate: now, options: options),
            []
        )
        // 130 of removable projects, and 90 without the oldest. The draft's 500 are not in it.
        XCTAssertEqual(
            StudioCleanupPolicy.plan(summaries: [draft, oldest, middle, newest], currentDate: now, options: options),
            ["oldest"]
        )
    }

    func testAnExportedProjectWhoseVideosAreAllGoneIsKeptAsADraftIs() {
        let now = Date(timeIntervalSince1970: 100 * 24 * 60 * 60)
        let videoGone = summary(
            id: "video-gone",
            lastOpenedAt: now.addingTimeInterval(-90 * 24 * 60 * 60),
            isDraft: false,
            sizeOnDisk: 500,
            exportMissing: true
        )

        XCTAssertFalse(videoGone.isRemovableByCleanup)
        XCTAssertEqual(
            StudioCleanupPolicy.plan(
                summaries: [videoGone],
                currentDate: now,
                options: StudioCleanupOptions(retentionDays: 30, sizeCapBytes: 1)
            ),
            []
        )
    }

    func testASummarySaysWhenEveryExportedVideoIsGoneAndCleanupThenKeepsTheProject() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        _ = try store.completeRecording(id: validID, request: creationRequest(camera: nil))
        let firstURL = directoryURL.appendingPathComponent("First.mp4")
        let secondURL = directoryURL.appendingPathComponent("Second.mp4")
        XCTAssertTrue(FileManager.default.createFile(atPath: firstURL.path, contents: Data([1])))
        XCTAssertTrue(FileManager.default.createFile(atPath: secondURL.path, contents: Data([2])))

        var summary = try XCTUnwrap(store.listSummaries().first)
        XCTAssertTrue(summary.isDraft)
        XCTAssertFalse(summary.exportMissing)
        XCTAssertFalse(summary.isRemovableByCleanup)

        _ = try store.recordExport(id: validID, path: firstURL.path)
        _ = try store.recordExport(id: validID, path: secondURL.path)
        var project = try store.load(id: validID)
        project.lastOpenedAt = fixedDate.addingTimeInterval(-90 * 24 * 60 * 60)
        _ = try store.save(project)
        summary = try XCTUnwrap(store.listSummaries().first)
        XCTAssertFalse(summary.isDraft)
        XCTAssertFalse(summary.exportMissing)
        XCTAssertTrue(summary.isRemovableByCleanup)

        // One of the two videos is enough for the recording to exist outside the project.
        try FileManager.default.removeItem(at: firstURL)
        summary = try XCTUnwrap(store.listSummaries().first)
        XCTAssertFalse(summary.exportMissing)

        try FileManager.default.removeItem(at: secondURL)
        summary = try XCTUnwrap(store.listSummaries().first)
        XCTAssertFalse(summary.isDraft)
        XCTAssertTrue(summary.exportMissing)
        XCTAssertFalse(summary.isRemovableByCleanup)

        // Nothing was written down: the project still names both, and is found by either.
        XCTAssertEqual(try store.load(id: validID).exports.count, 2)
        XCTAssertEqual(try store.findProjectID(exportedPath: firstURL.path), validID)

        // It is the one copy of the recording now, so cleanup keeps it, whatever the rules.
        let rules = StudioCleanupOptions(retentionDays: 30, sizeCapBytes: 1)
        XCTAssertEqual(try store.cleanup(options: rules), [])
        XCTAssertTrue(store.exists(id: validID))

        // Back where it was saved, from the Trash or with the disk it is on.
        XCTAssertTrue(FileManager.default.createFile(atPath: secondURL.path, contents: Data([2])))
        XCTAssertEqual(try store.cleanup(options: rules), [validID])
    }

    func testTheStorageLimitLeavesExportedProjectsAloneWhenOnlyDraftsAreOverIt() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let rules = StudioCleanupOptions(retentionDays: 0, sizeCapBytes: 250_000)

        func recordProject(bytes: Int) throws -> String {
            let paths = try store.beginRecording()
            _ = try store.completeRecording(id: paths.id, request: self.creationRequest(camera: nil))
            try Data(count: bytes).write(to: paths.screenURL)
            return paths.id
        }

        func exportProject(_ id: String, name: String, secondsSinceOpened: TimeInterval) throws {
            let videoURL = self.directoryURL.appendingPathComponent("\(name).mp4")
            XCTAssertTrue(FileManager.default.createFile(atPath: videoURL.path, contents: Data([7, 8, 9])))
            _ = try store.recordExport(id: id, path: videoURL.path)
            var project = try store.load(id: id)
            project.lastOpenedAt = self.fixedDate.addingTimeInterval(-secondsSinceOpened)
            _ = try store.save(project)
        }

        // The draft alone is over the limit, and so is the pinned project.
        let draft = try recordProject(bytes: 400_000)
        let pinned = try recordProject(bytes: 400_000)
        try exportProject(pinned, name: "pinned", secondsSinceOpened: 900)
        try store.setKeepSources(id: pinned, keepSources: true)
        let oldest = try recordProject(bytes: 100_000)
        try exportProject(oldest, name: "oldest", secondsSinceOpened: 300)
        let newer = try recordProject(bytes: 100_000)
        try exportProject(newer, name: "newer", secondsSinceOpened: 200)

        // Two exported projects of 100,000 bytes are under 250,000. Counting everything, as the
        // limit once did, both would go.
        XCTAssertEqual(try store.cleanup(options: rules), [])

        let newest = try recordProject(bytes: 100_000)
        try exportProject(newest, name: "newest", secondsSinceOpened: 100)

        // Three are over it, and the one opened longest ago goes.
        XCTAssertEqual(try store.cleanup(options: rules), [oldest])
        XCTAssertTrue(store.exists(id: draft))
        XCTAssertTrue(store.exists(id: pinned))
        XCTAssertTrue(store.exists(id: newer))
        XCTAssertTrue(store.exists(id: newest))
    }

    func testKeepingAProjectPinsItAndChangesNothingElse() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        _ = try store.completeRecording(id: validID, request: creationRequest(camera: nil))
        let exportURL = directoryURL.appendingPathComponent("out.mp4")
        XCTAssertTrue(FileManager.default.createFile(atPath: exportURL.path, contents: Data([7, 8, 9])))
        _ = try store.recordExport(id: validID, path: exportURL.path)
        var project = try store.load(id: validID)
        project.lastOpenedAt = fixedDate.addingTimeInterval(-90 * 24 * 60 * 60)
        _ = try store.save(project)
        let before = try store.load(id: validID)

        let laterDate = fixedDate.addingTimeInterval(300)
        let laterStore = StudioProjectStore(rootURL: directoryURL, now: { laterDate })
        let pinned = try laterStore.setKeepSources(id: validID, keepSources: true)

        var expected = before
        expected.keepSources = true
        XCTAssertTrue(pinned.keepSources)
        XCTAssertEqual(pinned.modifiedAt, before.modifiedAt)
        XCTAssertEqual(pinned.lastOpenedAt, before.lastOpenedAt)
        XCTAssertEqual(try laterStore.load(id: validID), expected)
        XCTAssertEqual(try laterStore.listSummaries().map(\.keepSources), [true])
        XCTAssertEqual(try laterStore.cleanup(), [])

        XCTAssertFalse(try laterStore.setKeepSources(id: validID, keepSources: false).keepSources)
        XCTAssertEqual(try laterStore.cleanup(), [validID])
        XCTAssertThrowsError(try laterStore.setKeepSources(id: validID, keepSources: true))
        XCTAssertFalse(laterStore.exists(id: validID))
    }

    func testTheScreenRecordingOfAProjectIsFoundInItsOwnFolder() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let paths = try store.beginRecording()
        var project = try store.completeRecording(id: paths.id, request: creationRequest(camera: nil))

        // Not written yet, as after a recording that failed before its first frame.
        XCTAssertNil(store.screenRecordingURL(id: paths.id))

        try Data([1, 2, 3]).write(to: paths.screenURL)
        XCTAssertEqual(store.screenRecordingURL(id: paths.id)?.path, paths.screenURL.path)

        // The project says which file it is.
        let renamedURL = paths.projectDirectory.appendingPathComponent("take-two.mp4")
        try FileManager.default.moveItem(at: paths.screenURL, to: renamedURL)
        project.sources.screen.file = "take-two.mp4"
        _ = try store.save(project)
        XCTAssertEqual(store.screenRecordingURL(id: paths.id)?.path, renamedURL.path)

        // A project that cannot be read is looked for under the name every recording gets.
        try Data("{".utf8).write(to: paths.projectJSONURL)
        XCTAssertThrowsError(try store.load(id: paths.id))
        XCTAssertNil(store.screenRecordingURL(id: paths.id))
        try FileManager.default.moveItem(at: renamedURL, to: paths.screenURL)
        XCTAssertEqual(store.screenRecordingURL(id: paths.id)?.path, paths.screenURL.path)

        try store.delete(id: paths.id)
        XCTAssertNil(store.screenRecordingURL(id: paths.id))
        XCTAssertNil(store.screenRecordingURL(id: "../\(validID)"))

        // A project around a video kept elsewhere has nothing of its own to save.
        let flatVideo = directoryURL.appendingPathComponent("external.mp4")
        XCTAssertTrue(FileManager.default.createFile(atPath: flatVideo.path, contents: Data([1])))
        let flat = try store.getOrCreateFlatProject(request: flatRequest(videoURL: flatVideo))
        XCTAssertNil(store.screenRecordingURL(id: flat.id))
    }

    func testProjectsThatCannotBeReadAreListedApartFromTheRest() throws {
        let damagedID = validID
        let newerID = otherID
        let folderDates = [
            damagedID: Date(timeIntervalSince1970: 100),
            newerID: Date(timeIntervalSince1970: 200)
        ]
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate }, folderDateProvider: { url in
            folderDates[url.lastPathComponent]
        })
        let readable = try store.beginRecording()
        _ = try store.completeRecording(id: readable.id, request: creationRequest(camera: nil))
        let unfinished = try store.beginRecording()

        let damagedURL = directoryURL.appendingPathComponent(damagedID, isDirectory: true)
        let newerURL = directoryURL.appendingPathComponent(newerID, isDirectory: true)
        try FileManager.default.createDirectory(at: damagedURL, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: newerURL, withIntermediateDirectories: true)
        try Data("{".utf8).write(to: damagedURL.appendingPathComponent("project.json"))
        try Data(count: 1_000).write(to: damagedURL.appendingPathComponent("screen.mp4"))
        try Data("{ \"schemaVersion\": 99 }".utf8).write(to: newerURL.appendingPathComponent("project.json"))

        let unreadable = try store.listUnreadableProjects()

        XCTAssertEqual(try store.listSummaries().map(\.id), [readable.id])
        XCTAssertEqual(unreadable.map(\.id), [damagedID, newerID])
        XCTAssertFalse(unreadable.map(\.id).contains(unfinished.id))
        XCTAssertEqual(unreadable.map(\.hasScreenRecording), [true, false])
        XCTAssertEqual(unreadable.map(\.createdAt), [Date(timeIntervalSince1970: 100), Date(timeIntervalSince1970: 200)])
        XCTAssertGreaterThanOrEqual(unreadable[0].sizeOnDisk, 1_000)

        // Cleanup still leaves them alone, whatever the rules.
        XCTAssertEqual(try store.cleanup(options: StudioCleanupOptions(retentionDays: 1, sizeCapBytes: 1)), [])
        XCTAssertEqual(try store.listUnreadableProjects().count, 2)

        // The way out, and the way to be rid of it.
        XCTAssertEqual(store.screenRecordingURL(id: damagedID)?.lastPathComponent, "screen.mp4")
        try store.delete(id: damagedID)
        XCTAssertEqual(try store.listUnreadableProjects().map(\.id), [newerID])
    }

    func testSavingAScreenRecordingCopiesItAndNeverReplacesAFile() throws {
        let store = StudioProjectStore(
            rootURL: directoryURL.appendingPathComponent("Projects", isDirectory: true),
            now: { self.fixedDate }
        )
        let videosURL = directoryURL.appendingPathComponent("Videos", isDirectory: true)
        let paths = try store.beginRecording()
        _ = try store.completeRecording(id: paths.id, request: creationRequest(camera: nil))
        let recording = Data((0..<5_000).map { UInt8($0 % 251) })
        try recording.write(to: paths.screenURL)
        let firstURL = videosURL.appendingPathComponent("Clip.mp4")
        let secondURL = videosURL.appendingPathComponent("Clip 2.mp4")

        // The name is free: the copy gets it, and the project is as it was.
        var asked = 0
        let saved = try StudioScreenRecording.save(store: store, id: paths.id) {
            asked += 1
            return firstURL
        }
        XCTAssertEqual(saved, firstURL)
        XCTAssertEqual(asked, 1)
        XCTAssertEqual(try Data(contentsOf: firstURL), recording)
        XCTAssertEqual(try Data(contentsOf: paths.screenURL), recording)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: videosURL.path), ["Clip.mp4"])
        XCTAssertEqual(try store.listSummaries().map(\.isDraft), [true])
        XCTAssertNil(try store.findProjectID(exportedPath: firstURL.path))

        // The name is taken: what has it keeps it, and the copy gets the next name.
        try Data([42]).write(to: firstURL)
        asked = 0
        let savedAgain = try StudioScreenRecording.save(store: store, id: paths.id) {
            asked += 1
            return asked == 1 ? firstURL : secondURL
        }
        XCTAssertEqual(savedAgain, secondURL)
        XCTAssertEqual(asked, 2)
        XCTAssertEqual(try Data(contentsOf: firstURL), Data([42]))
        XCTAssertEqual(try Data(contentsOf: secondURL), recording)

        // No name is free: nothing is replaced and nothing is left behind.
        asked = 0
        XCTAssertThrowsError(try StudioScreenRecording.save(store: store, id: paths.id, makeOutputURL: {
            asked += 1
            return firstURL
        })) { error in
            XCTAssertEqual(error as? StudioScreenRecording.SaveError, .noFreeName("Clip.mp4"))
        }
        XCTAssertEqual(asked, StudioScreenRecording.attempts)
        XCTAssertEqual(try Data(contentsOf: firstURL), Data([42]))
        XCTAssertEqual(
            try FileManager.default.contentsOfDirectory(atPath: videosURL.path).sorted(),
            ["Clip 2.mp4", "Clip.mp4"]
        )
    }

    func testSavingAScreenRecordingSaysSoWhenThereIsNone() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        _ = try store.completeRecording(id: validID, request: creationRequest(camera: nil))
        let outputURL = directoryURL.appendingPathComponent("Videos", isDirectory: true).appendingPathComponent("Clip.mp4")
        var asked = 0

        XCTAssertThrowsError(try StudioScreenRecording.save(store: store, id: validID, makeOutputURL: {
            asked += 1
            return outputURL
        })) { error in
            XCTAssertEqual(error as? StudioScreenRecording.SaveError, .nothingToSave)
            XCTAssertEqual(error.localizedDescription, "This project has no screen recording to save.")
        }
        XCTAssertEqual(asked, 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath: outputURL.deletingLastPathComponent().path))
    }

    func testSavedLookAppliesStylesAndResetsCrops() throws {
        let store = StudioProjectStore(rootURL: directoryURL, now: { self.fixedDate })
        let look = StudioLook(
            canvas: StudioCanvas(aspect: .square, padding: 0.2, background: StudioBackground(style: .solid, preset: "custom", primary: "#000000", secondary: "#111111", image: "bad/path.png")),
            screen: StudioScreenStyle(cornerRadius: 0.08, shadow: 0.1, crop: StudioRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5)),
            camera: StudioCameraStyle(shape: .rectangle, mirror: false, shadow: 0.2, crop: StudioRect(x: 0.2, y: 0.2, width: 0.5, height: 0.5))
        )
        let project = try store.completeRecording(id: validID, request: creationRequest(look: look))

        XCTAssertEqual(project.canvas.aspect, .square)
        XCTAssertEqual(project.canvas.padding, 0.2)
        XCTAssertNil(project.canvas.background.image)
        XCTAssertEqual(project.screen.cornerRadius, 0.08)
        XCTAssertEqual(project.screen.shadow, 0.1)
        XCTAssertNil(project.screen.crop)
        XCTAssertEqual(project.camera.shape, .rectangle)
        XCTAssertFalse(project.camera.mirror)
        XCTAssertNil(project.camera.crop)
    }

    func testLookSettingsTextRoundTripsWithoutCrops() throws {
        let look = StudioLook(
            canvas: StudioCanvas(aspect: .portrait9x16, padding: 0.1, background: StudioBackground(style: .solid, preset: "ink", primary: "#141719", secondary: nil)),
            screen: StudioScreenStyle(cornerRadius: 0.05, shadow: 0.8, crop: StudioRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5)),
            camera: StudioCameraStyle(shape: .squircle, mirror: false, borderWidth: 0.01, shadow: 0.6, crop: StudioRect(x: 0.2, y: 0.2, width: 0.5, height: 0.5))
        )

        let text = try XCTUnwrap(look.settingsText())
        let restored = try XCTUnwrap(StudioLook(settingsText: text))

        XCTAssertEqual(restored, look.withoutCrops)
        XCTAssertNil(restored.screen.crop)
        XCTAssertNil(restored.camera.crop)
        XCTAssertEqual(restored.canvas.aspect, .portrait9x16)
        XCTAssertEqual(restored.camera.shape, .squircle)
    }

    func testLookSettingsTextFillsMissingPartsAndRejectsOtherText() throws {
        let partial = try XCTUnwrap(StudioLook(settingsText: #"{"canvas":{"padding":0.2}}"#))
        XCTAssertEqual(partial.canvas.padding, 0.2)
        XCTAssertEqual(partial.canvas.background, StudioBackground())
        XCTAssertEqual(partial.screen, StudioScreenStyle())
        XCTAssertEqual(partial.camera, StudioCameraStyle())

        XCTAssertNil(StudioLook(settingsText: ""))
        XCTAssertNil(StudioLook(settingsText: "not json"))
        XCTAssertNil(StudioLook(settingsText: "[1, 2]"))
    }

    func testGenericNullAndMissingGuardForProjectsAndEvents() throws {
        let projectData = try StudioJSON.makeEncoder().encode(fullyPopulatedProject())
        let eventData = try StudioJSON.makeEncoder().encode(StudioEvents())

        try assertNullAndMissingDefaults(ProjectDocument.self, data: projectData, requiredPaths: [
            "id",
            "sources.screen.width",
            "sources.screen.height",
            "sources.screen.duration",
            "sources.camera.width",
            "sources.camera.height",
            "sources.camera.duration"
        ], explicitNullSurvives: [
            "canvas.background.preset",
            "canvas.background.secondary"
        ])
        try assertNullAndMissingDefaults(EventDocument.self, data: eventData, requiredPaths: [], explicitNullSurvives: [])
    }

    // MARK: - Helpers

    private func decodeProject(_ json: String) throws -> StudioProject {
        try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(json.utf8))
    }

    /// `sources.screen` of a project as it is written.
    private func screenObject(of project: StudioProject) throws -> [String: Any] {
        let written = try XCTUnwrap(JSONSerialization.jsonObject(with: StudioJSON.makeEncoder().encode(project)) as? [String: Any])
        return try XCTUnwrap((written["sources"] as? [String: Any])?["screen"] as? [String: Any])
    }

    private func creationRequest(
        camera: StudioCameraCreationInfo? = StudioCameraCreationInfo(width: 640, height: 480, duration: 10, startOffset: 0.25),
        look: StudioLook? = nil
    ) -> StudioProjectCreationRequest {
        StudioProjectCreationRequest(
            name: "Test",
            screenWidth: 1920,
            screenHeight: 1080,
            screenDuration: 10,
            camera: camera,
            bubbleAnchor: .topLeft,
            clickOverlay: StudioClickOverlay(enabled: true, color: "#FF0000"),
            branding: true,
            appVersion: "1.0",
            look: look
        )
    }

    private func flatRequest(videoURL: URL) -> StudioFlatProjectRequest {
        StudioFlatProjectRequest(videoURL: videoURL, width: 1920, height: 1080, duration: 5, appVersion: "1.0")
    }

    private func makeProject(id: String) -> StudioProject {
        StudioProject(
            id: id,
            name: "Project",
            createdAt: fixedDate,
            modifiedAt: fixedDate,
            lastOpenedAt: fixedDate,
            app: StudioAppInfo(platform: "macos", version: "1.0"),
            sources: StudioSources(
                screen: StudioScreenSource(width: 1920, height: 1080, duration: 10),
                camera: StudioCameraSource(width: 640, height: 480, duration: 8)
            )
        )
    }

    private func fullyPopulatedProject() -> StudioProject {
        StudioProject(
            id: validID,
            name: "Full",
            createdAt: fixedDate,
            modifiedAt: fixedDate,
            lastOpenedAt: fixedDate,
            app: StudioAppInfo(platform: "macos", version: "1.0"),
            keepSources: true,
            sources: StudioSources(
                screen: StudioScreenSource(file: "screen.mp4", width: 1920, height: 1080, frameRate: 60, duration: 10, audioTracks: ["system", "microphone"]),
                camera: StudioCameraSource(file: "camera.mp4", width: 640, height: 480, duration: 8, startOffset: 0.5),
                events: "events.json"
            ),
            canvas: StudioCanvas(aspect: .landscape16x9, padding: 0.1, background: StudioBackground(style: .image, preset: "preset", primary: "#112233", secondary: "#445566", image: "background.png")),
            screen: StudioScreenStyle(cornerRadius: 0.05, shadow: 0.2, crop: StudioRect(x: 0.1, y: 0.1, width: 0.8, height: 0.8)),
            camera: StudioCameraStyle(shape: .roundedRectangle, cornerRadius: 0.2, mirror: false, borderWidth: 0.01, borderColor: "#ABCDEF", shadow: 0.4, crop: StudioRect(x: 0.1, y: 0.1, width: 0.7, height: 0.7), cutout: .blur),
            scenes: [StudioScene(start: 1, layout: .sideBySide, bubble: StudioBubble(anchor: .topRight, size: 0.3, offsetX: 0.1, offsetY: 0.2), split: StudioSplit(cameraSide: .leading, cameraFraction: 0.4), transition: StudioTransition(kind: .morph, duration: 0.5))],
            zooms: [StudioZoom(start: 1, end: 2, scale: 1.5, focus: StudioZoomFocus(mode: .cursor, x: 0.2, y: 0.3), easeIn: 0.1, easeOut: 0.2, origin: .auto)],
            edits: StudioEdits(trimStart: 1, trimEnd: 9, cuts: [StudioTimeRange(start: 2, end: 3)], speed: [StudioSpeedRange(start: 4, end: 5, rate: 2)]),
            audio: StudioAudio(muted: true, systemVolume: 0.5, microphoneVolume: 0.6),
            overlays: StudioOverlays(clicks: StudioClickOverlay(enabled: false, color: "#123456", size: 20, strokeWidth: 2, opacity: 0.5, duration: 0.2), branding: true),
            exports: [StudioExport(path: "/tmp/export.mp4", exportedAt: fixedDate)]
        )
    }

    private func fullyPopulatedEvents() -> StudioEvents {
        StudioEvents(
            capture: StudioCaptureInfo(width: 1920, height: 1080, scale: 2, kind: .region),
            clicks: [StudioClickEvent(t: 1, x: 0.2, y: 0.3, button: .right)],
            cursor: [StudioCursorSample(t: 2, x: 0.4, y: 0.5)],
            cameraCorners: [StudioCameraCornerEvent(t: 3, corner: .topLeft)],
            markers: [.object(["future": .bool(true)])]
        )
    }

    private func writeProject(_ project: StudioProject, folderID: String) throws {
        let directory = directoryURL.appendingPathComponent(folderID, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let data = try StudioJSON.makeEncoder().encode(project)
        try data.write(to: directory.appendingPathComponent("project.json"))
    }

    private func summary(
        id: String,
        lastOpenedAt: Date,
        isDraft: Bool,
        isFlat: Bool = false,
        keepSources: Bool = false,
        sizeOnDisk: Int64 = 1,
        sourceExists: Bool = true,
        exportMissing: Bool = false
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
            sourceExists: sourceExists,
            exportMissing: exportMissing
        )
    }
}

// MARK: - Generic Null Guard

private protocol GuardDecodable: Decodable, Equatable {
    static func decode(_ data: Data) throws -> Self
    static func encode(_ value: Self) throws -> Any
}

private struct ProjectDocument: GuardDecodable {
    var project: StudioProject

    init(project: StudioProject) {
        self.project = project
    }

    init(from decoder: Decoder) throws {
        project = try StudioProject(from: decoder)
    }

    static func decode(_ data: Data) throws -> ProjectDocument {
        ProjectDocument(project: try StudioJSON.makeDecoder().decode(StudioProject.self, from: data))
    }

    static func encode(_ value: ProjectDocument) throws -> Any {
        try JSONSerialization.jsonObject(with: StudioJSON.makeEncoder().encode(value.project))
    }
}

private struct EventDocument: GuardDecodable {
    var events: StudioEvents

    init(events: StudioEvents) {
        self.events = events
    }

    init(from decoder: Decoder) throws {
        events = try StudioEvents(from: decoder)
    }

    static func decode(_ data: Data) throws -> EventDocument {
        EventDocument(events: try StudioJSON.makeDecoder().decode(StudioEvents.self, from: data))
    }

    static func encode(_ value: EventDocument) throws -> Any {
        try JSONSerialization.jsonObject(with: StudioJSON.makeEncoder().encode(value.events))
    }
}

private enum JSONPathComponent: Hashable {
    case key(String)
    case index(Int)
}

private extension StudioProjectTests {
    func assertNullAndMissingDefaults<T: GuardDecodable>(
        _ type: T.Type,
        data: Data,
        requiredPaths: Set<String>,
        explicitNullSurvives: Set<String>
    ) throws {
        let root = try JSONSerialization.jsonObject(with: data)
        for path in leafPaths(in: root) {
            let dotted = dottedPath(path)
            var nullRoot = root
            setValue(NSNull(), at: path, in: &nullRoot)
            var missingRoot = root
            removeValue(at: path, in: &missingRoot)
            let nullData = try JSONSerialization.data(withJSONObject: nullRoot)
            let missingData = try JSONSerialization.data(withJSONObject: missingRoot)

            if requiredPaths.contains(dotted) {
                XCTAssertThrowsError(try T.decode(nullData), dotted)
                XCTAssertThrowsError(try T.decode(missingData), dotted)
                continue
            }

            let nullDecoded = try T.decode(nullData)
            let missingDecoded = try T.decode(missingData)
            if explicitNullSurvives.contains(dotted) {
                let encodedNull = try T.encode(nullDecoded)
                XCTAssertTrue(value(at: path, in: encodedNull) is NSNull, dotted)
            } else {
                XCTAssertEqual(nullDecoded, missingDecoded, dotted)
            }
        }
    }

    func leafPaths(in value: Any, prefix: [JSONPathComponent] = []) -> [[JSONPathComponent]] {
        if let dictionary = value as? [String: Any] {
            return dictionary.keys.sorted().flatMap { key in
                leafPaths(in: dictionary[key] as Any, prefix: prefix + [.key(key)])
            }
        }
        if let array = value as? [Any] {
            return array.indices.flatMap { index in
                leafPaths(in: array[index], prefix: prefix + [.index(index)])
            }
        }
        return [prefix]
    }

    func dottedPath(_ path: [JSONPathComponent]) -> String {
        path.compactMap { component in
            switch component {
            case .key(let key):
                return key
            case .index:
                return nil
            }
        }.joined(separator: ".")
    }

    func value(at path: [JSONPathComponent], in value: Any) -> Any? {
        guard let first = path.first else { return value }
        let rest = Array(path.dropFirst())
        switch first {
        case .key(let key):
            guard let dictionary = value as? [String: Any], let next = dictionary[key] else { return nil }
            return self.value(at: rest, in: next)
        case .index(let index):
            guard let array = value as? [Any], array.indices.contains(index) else { return nil }
            return self.value(at: rest, in: array[index])
        }
    }

    func setValue(_ newValue: Any, at path: [JSONPathComponent], in value: inout Any) {
        guard let first = path.first else {
            value = newValue
            return
        }
        let rest = Array(path.dropFirst())
        switch first {
        case .key(let key):
            var dictionary = value as? [String: Any] ?? [:]
            var child = dictionary[key] as Any
            setValue(newValue, at: rest, in: &child)
            dictionary[key] = child
            value = dictionary
        case .index(let index):
            var array = value as? [Any] ?? []
            var child = array[index]
            setValue(newValue, at: rest, in: &child)
            array[index] = child
            value = array
        }
    }

    func removeValue(at path: [JSONPathComponent], in value: inout Any) {
        guard let first = path.first else { return }
        let rest = Array(path.dropFirst())
        switch first {
        case .key(let key):
            var dictionary = value as? [String: Any] ?? [:]
            if rest.isEmpty {
                dictionary.removeValue(forKey: key)
            } else {
                var child = dictionary[key] as Any
                removeValue(at: rest, in: &child)
                dictionary[key] = child
            }
            value = dictionary
        case .index(let index):
            var array = value as? [Any] ?? []
            if rest.isEmpty {
                array.remove(at: index)
            } else {
                var child = array[index]
                removeValue(at: rest, in: &child)
                array[index] = child
            }
            value = array
        }
    }
}
