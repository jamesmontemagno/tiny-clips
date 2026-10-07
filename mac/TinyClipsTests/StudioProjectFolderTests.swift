import XCTest
@testable import TinyClips

final class StudioProjectFolderTests: XCTestCase {
    private var rootURL: URL!
    private var storeURL: URL!
    private var outsideURL: URL!
    private var store: StudioProjectStore!
    private let fileManager = FileManager.default

    override func setUpWithError() throws {
        rootURL = fileManager.temporaryDirectory
            .appendingPathComponent("TinyClipsStudioFolderTests-\(UUID().uuidString)", isDirectory: true)
        storeURL = rootURL.appendingPathComponent("Projects", isDirectory: true)
        outsideURL = rootURL.appendingPathComponent("Outside", isDirectory: true)
        try fileManager.createDirectory(at: storeURL, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: outsideURL, withIntermediateDirectories: true)
        store = StudioProjectStore(rootURL: storeURL)
    }

    override func tearDownWithError() throws {
        if let rootURL {
            try? fileManager.removeItem(at: rootURL)
        }
        rootURL = nil
        store = nil
    }

    // MARK: - Saving a Folder

    func testAFolderHasTheRecordingsAndAProjectFileNamedAfterIt() throws {
        let id = try makeProject(camera: true, events: true, poster: true)
        let folder = outsideURL.appendingPathComponent("My Demo")

        try store.exportProjectFolder(id: id, to: folder)

        XCTAssertEqual(
            try names(in: folder),
            ["My Demo.tinyclips", "camera.mp4", "events.json", "poster.jpg", "screen.mp4"]
        )
        XCTAssertEqual(try Data(contentsOf: folder.appendingPathComponent("screen.mp4")), Data("screen".utf8))
        XCTAssertEqual(try Data(contentsOf: folder.appendingPathComponent("camera.mp4")), Data("camera".utf8))
    }

    func testAProjectWithoutACameraHasNoCameraFileInItsFolder() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Screen Only")

        try store.exportProjectFolder(id: id, to: folder)

        XCTAssertEqual(try names(in: folder), ["Screen Only.tinyclips", "screen.mp4"])
    }

    func testTheProjectFileLeavesOutWhereVideosWereExportedAndTheStoreKeepsIt() throws {
        let id = try makeProject(camera: true)
        _ = try store.recordExport(id: id, path: "/Users/someone/Movies/demo.mp4")
        let folder = outsideURL.appendingPathComponent("Copy")

        try store.exportProjectFolder(id: id, to: folder)

        let text = try String(contentsOf: folder.appendingPathComponent("Copy.tinyclips"), encoding: .utf8)
        XCTAssertFalse(text.contains("someone"))
        let written = try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(text.utf8))
        XCTAssertTrue(written.exports.isEmpty)
        XCTAssertEqual(try store.load(id: id).exports.map(\.path), ["/Users/someone/Movies/demo.mp4"])
    }

    func testABackgroundImageGoesWithTheProject() throws {
        let id = try makeProject(camera: false)
        var project = try store.load(id: id)
        project.canvas.background.style = .image
        project.canvas.background.image = "backdrop.png"
        try store.save(project)
        try Data("image".utf8).write(to: try store.paths(forID: id).projectDirectory.appendingPathComponent("backdrop.png"))
        let folder = outsideURL.appendingPathComponent("Pictured")

        try store.exportProjectFolder(id: id, to: folder)
        let imported = try store.importProjectFolder(projectFile: folder.appendingPathComponent("Pictured.tinyclips"))

        XCTAssertTrue(try names(in: folder).contains("backdrop.png"))
        XCTAssertEqual(
            try Data(contentsOf: try store.paths(forID: imported.id).projectDirectory.appendingPathComponent("backdrop.png")),
            Data("image".utf8)
        )
    }

    func testSavingOverSomethingThatIsNotASavedProjectIsRefusedAndLeavesItAlone() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Taken")
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data("mine".utf8).write(to: folder.appendingPathComponent("keep.txt"))
        let file = outsideURL.appendingPathComponent("A File")
        try Data("mine".utf8).write(to: file)

        for replacing in [false, true] {
            XCTAssertThrowsError(try store.exportProjectFolder(id: id, to: folder, replacingSavedProject: replacing)) { error in
                XCTAssertEqual(error as? StudioProjectFolderError, .destinationExists)
            }
            XCTAssertThrowsError(try store.exportProjectFolder(id: id, to: file, replacingSavedProject: replacing)) { error in
                XCTAssertEqual(error as? StudioProjectFolderError, .destinationExists)
            }
        }
        XCTAssertEqual(try names(in: folder), ["keep.txt"])
        XCTAssertEqual(try Data(contentsOf: file), Data("mine".utf8))
    }

    func testASavedProjectIsReplacedOnlyWhenAskedTo() throws {
        let first = try makeProject(camera: true, events: true)
        let second = try makeProject(camera: false)
        var edited = try store.load(id: second)
        edited.name = "Second"
        try store.save(edited)
        let folder = outsideURL.appendingPathComponent("Shared Name")
        try store.exportProjectFolder(id: first, to: folder)

        XCTAssertThrowsError(try store.exportProjectFolder(id: second, to: folder)) { error in
            XCTAssertEqual(error as? StudioProjectFolderError, .destinationExists)
        }
        XCTAssertTrue(try names(in: folder).contains("camera.mp4"))

        try store.exportProjectFolder(id: second, to: folder, replacingSavedProject: true)

        // What the first project had and the second does not is gone with the old folder.
        XCTAssertEqual(try names(in: folder), ["Shared Name.tinyclips", "screen.mp4"])
        let written = try StudioJSON.makeDecoder().decode(
            StudioProject.self,
            from: Data(contentsOf: folder.appendingPathComponent("Shared Name.tinyclips"))
        )
        XCTAssertEqual(written.name, "Second")
        XCTAssertEqual(try names(in: outsideURL), ["Shared Name"])
    }

    func testAFolderIsASavedProjectWhenItHasExactlyOneProjectFile() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Saved")
        try store.exportProjectFolder(id: id, to: folder)

        XCTAssertTrue(store.isSavedProjectFolder(folder))
        XCTAssertFalse(store.isSavedProjectFolder(outsideURL))
        XCTAssertFalse(store.isSavedProjectFolder(folder.appendingPathComponent("Saved.tinyclips")))
        XCTAssertFalse(store.isSavedProjectFolder(outsideURL.appendingPathComponent("Nowhere")))
    }

    func testAProjectWhoseRecordingIsGoneIsNotSavedAndLeavesNoFolder() throws {
        let id = try makeProject(camera: true)
        try fileManager.removeItem(at: try XCTUnwrap(try store.paths(forID: id).cameraURL))
        let folder = outsideURL.appendingPathComponent("Half")

        XCTAssertThrowsError(try store.exportProjectFolder(id: id, to: folder)) { error in
            XCTAssertEqual(error as? StudioProjectFolderError, .missingFile("camera.mp4"))
        }
        XCTAssertFalse(fileManager.fileExists(atPath: folder.path))
    }

    func testAProjectAroundAVideoKeptElsewhereIsNotSavedAsAFolder() throws {
        let video = outsideURL.appendingPathComponent("finished.mp4")
        try Data("video".utf8).write(to: video)
        let flat = try store.getOrCreateFlatProject(
            request: StudioFlatProjectRequest(videoURL: video, width: 1920, height: 1080, duration: 5, appVersion: "1.0")
        )

        XCTAssertThrowsError(try store.exportProjectFolder(id: flat.id, to: outsideURL.appendingPathComponent("Flat"))) { error in
            XCTAssertEqual(error as? StudioProjectFolderError, .externalSource)
        }
    }

    // MARK: - Opening a Folder

    func testAFolderOpensAsANewProjectWithTheSameEditsAndNoExports() throws {
        let id = try makeProject(camera: true, events: true)
        var project = try store.load(id: id)
        project.name = "Round Trip"
        project.zooms = [StudioZoom(start: 1, end: 3, scale: 2)]
        project.edits.cuts = [StudioTimeRange(start: 4, end: 5)]
        project.keepSources = true
        project.extra["fromALaterVersion"] = .string("kept")
        try store.save(project)
        _ = try store.recordExport(id: id, path: "/Users/someone/Movies/demo.mp4")
        let folder = outsideURL.appendingPathComponent("Round Trip")
        try store.exportProjectFolder(id: id, to: folder)

        let imported = try store.importProjectFolder(projectFile: folder.appendingPathComponent("Round Trip.tinyclips"))

        XCTAssertNotEqual(imported.id, id)
        XCTAssertEqual(imported.name, "Round Trip")
        XCTAssertEqual(imported.zooms, project.zooms)
        XCTAssertEqual(imported.edits.cuts, project.edits.cuts)
        XCTAssertEqual(imported.extra["fromALaterVersion"], .string("kept"))
        XCTAssertTrue(imported.exports.isEmpty)

        let onDisk = try store.load(id: imported.id)
        XCTAssertEqual(onDisk.id, imported.id)
        XCTAssertEqual(onDisk.zooms, project.zooms)
        let paths = try store.paths(for: onDisk)
        XCTAssertEqual(try Data(contentsOf: paths.screenURL), Data("screen".utf8))
        XCTAssertEqual(try Data(contentsOf: try XCTUnwrap(paths.cameraURL)), Data("camera".utf8))
        XCTAssertEqual(try Data(contentsOf: paths.eventsURL), Data("{}".utf8))

        let summary = try XCTUnwrap(try store.listSummaries().first { $0.id == imported.id })
        XCTAssertTrue(summary.isDraft)
        XCTAssertFalse(summary.isRemovableByCleanup)
        // The project it was saved from is as it was.
        XCTAssertEqual(try store.load(id: id).exports.count, 1)
    }

    func testOpeningTheSameFileTwiceMakesTwoProjectsAndLeavesTheFolderAlone() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Twice")
        try store.exportProjectFolder(id: id, to: folder)
        let before = try names(in: folder)
        let file = folder.appendingPathComponent("Twice.tinyclips")
        let textBefore = try Data(contentsOf: file)

        let first = try store.importProjectFolder(projectFile: file)
        let second = try store.importProjectFolder(projectFile: file)

        XCTAssertEqual(Set([id, first.id, second.id]).count, 3)
        XCTAssertEqual(try store.listSummaries().count, 3)
        XCTAssertEqual(try names(in: folder), before)
        XCTAssertEqual(try Data(contentsOf: file), textBefore)
    }

    func testAProjectFileWithoutItsScreenRecordingIsRefusedAndAddsNothingToTheStore() throws {
        let id = try makeProject(camera: true)
        let folder = outsideURL.appendingPathComponent("Alone")
        try store.exportProjectFolder(id: id, to: folder)
        try fileManager.removeItem(at: folder.appendingPathComponent("screen.mp4"))

        XCTAssertThrowsError(try store.importProjectFolder(projectFile: folder.appendingPathComponent("Alone.tinyclips"))) { error in
            XCTAssertEqual(error as? StudioProjectFolderError, .missingFile("screen.mp4"))
        }
        XCTAssertEqual(try names(in: storeURL), [id])
    }

    func testAProjectFileWithoutTheCameraRecordingItNamesIsRefused() throws {
        let id = try makeProject(camera: true)
        let folder = outsideURL.appendingPathComponent("No Camera")
        try store.exportProjectFolder(id: id, to: folder)
        try fileManager.removeItem(at: folder.appendingPathComponent("camera.mp4"))

        XCTAssertThrowsError(try store.importProjectFolder(projectFile: folder.appendingPathComponent("No Camera.tinyclips"))) { error in
            XCTAssertEqual(error as? StudioProjectFolderError, .missingFile("camera.mp4"))
        }
        XCTAssertEqual(try names(in: storeURL), [id])
    }

    func testAProjectFileCannotNameAFileOutsideItsFolder() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Reaching")
        try store.exportProjectFolder(id: id, to: folder)
        try Data("secret".utf8).write(to: outsideURL.appendingPathComponent("secret.mp4"))
        let file = folder.appendingPathComponent("Reaching.tinyclips")
        let text = try String(contentsOf: file, encoding: .utf8)
        XCTAssertTrue(text.contains("\"screen.mp4\""))
        try text.replacingOccurrences(of: "\"screen.mp4\"", with: "\"../secret.mp4\"")
            .write(to: file, atomically: true, encoding: .utf8)

        XCTAssertThrowsError(try store.importProjectFolder(projectFile: file)) { error in
            XCTAssertEqual(
                error as? StudioProjectError,
                .invalidProject("sources.screen.file must be a plain file name")
            )
        }
        XCTAssertEqual(try names(in: storeURL), [id])
    }

    func testAProjectFileCannotNameAnEventsFileOutsideItsFolder() throws {
        let id = try makeProject(camera: false, events: true)
        let folder = outsideURL.appendingPathComponent("Events")
        try store.exportProjectFolder(id: id, to: folder)
        let file = folder.appendingPathComponent("Events.tinyclips")
        let text = try String(contentsOf: file, encoding: .utf8)
        XCTAssertTrue(text.contains("\"events.json\""))
        try text.replacingOccurrences(of: "\"events.json\"", with: "\"../events.json\"")
            .write(to: file, atomically: true, encoding: .utf8)

        XCTAssertThrowsError(try store.importProjectFolder(projectFile: file)) { error in
            XCTAssertEqual(error as? StudioProjectError, .invalidProject("sources.events must be a plain file name"))
        }
        XCTAssertEqual(try names(in: storeURL), [id])
    }

    func testAProjectFileFromALaterVersionIsRefused() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Later")
        try store.exportProjectFolder(id: id, to: folder)
        let file = folder.appendingPathComponent("Later.tinyclips")
        let text = try String(contentsOf: file, encoding: .utf8)
        XCTAssertTrue(text.contains("\"schemaVersion\" : 1"))
        try text.replacingOccurrences(of: "\"schemaVersion\" : 1", with: "\"schemaVersion\" : 2")
            .write(to: file, atomically: true, encoding: .utf8)

        XCTAssertThrowsError(try store.importProjectFolder(projectFile: file)) { error in
            XCTAssertEqual(error as? StudioProjectError, .unsupportedVersion(2))
        }
        XCTAssertEqual(try names(in: storeURL), [id])
    }

    // MARK: - Finding the Project File

    func testAProjectFileIsItselfAndAFolderIsTheOneProjectFileInIt() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Found")
        try store.exportProjectFolder(id: id, to: folder)
        let file = folder.appendingPathComponent("Found.tinyclips")

        XCTAssertEqual(try store.projectFile(at: file).lastPathComponent, "Found.tinyclips")
        XCTAssertEqual(try store.projectFile(at: folder).lastPathComponent, "Found.tinyclips")
    }

    func testWhatIsNotAProjectFileOrAFolderWithExactlyOneIsRefused() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Crowded")
        try store.exportProjectFolder(id: id, to: folder)
        let screen = folder.appendingPathComponent("screen.mp4")

        for url in [screen, outsideURL!, outsideURL.appendingPathComponent("nothing.tinyclips")] {
            XCTAssertThrowsError(try store.projectFile(at: url)) { error in
                XCTAssertEqual(error as? StudioProjectFolderError, .notAProjectFile)
            }
        }

        try Data("{}".utf8).write(to: folder.appendingPathComponent("Second.tinyclips"))
        XCTAssertThrowsError(try store.projectFile(at: folder)) { error in
            XCTAssertEqual(error as? StudioProjectFolderError, .notAProjectFile)
        }
    }

    func testAFolderNameIsTheProjectsNameWithoutWhatAPathCannotHold() {
        XCTAssertEqual(StudioProjectStore.folderName(for: "Demo recording"), "Demo recording")
        XCTAssertEqual(StudioProjectStore.folderName(for: "  a/b:c\\d  "), "a-b-c-d")
        XCTAssertEqual(StudioProjectStore.folderName(for: "two\nlines"), "two-lines")
        XCTAssertEqual(StudioProjectStore.folderName(for: "..hidden"), "hidden")
        XCTAssertEqual(StudioProjectStore.folderName(for: ""), "Tiny Clips Project")
        XCTAssertEqual(StudioProjectStore.folderName(for: " . "), "Tiny Clips Project")
    }

    // MARK: - Open Recent

    func testRecentProjectsAreTheLastOpenedFirstWithoutTheCurrentOneOrThoseWithoutARecording() {
        func summary(_ id: String, opened: TimeInterval, sourceExists: Bool = true) -> StudioProjectSummary {
            StudioProjectSummary(
                id: id,
                name: id,
                createdAt: Date(timeIntervalSince1970: 0),
                lastOpenedAt: Date(timeIntervalSince1970: opened),
                isDraft: true,
                isFlat: false,
                keepSources: false,
                sizeOnDisk: 0,
                sourceExists: sourceExists
            )
        }
        let summaries = [
            summary("old", opened: 10),
            summary("current", opened: 99),
            summary("gone", opened: 50, sourceExists: false),
            summary("b-tied", opened: 30),
            summary("new", opened: 40),
            summary("a-tied", opened: 30),
        ]

        XCTAssertEqual(
            StudioProjectSummary.recent(from: summaries, excluding: "current", limit: 10).map(\.id),
            ["new", "a-tied", "b-tied", "old"]
        )
        XCTAssertEqual(
            StudioProjectSummary.recent(from: summaries, excluding: "current", limit: 2).map(\.id),
            ["new", "a-tied"]
        )
        XCTAssertEqual(
            StudioProjectSummary.recent(from: summaries, excluding: nil, limit: 1).map(\.id),
            ["current"]
        )
        XCTAssertTrue(StudioProjectSummary.recent(from: summaries, excluding: nil, limit: 0).isEmpty)
        XCTAssertTrue(StudioProjectSummary.recent(from: [], excluding: "current", limit: 5).isEmpty)
    }

    // MARK: - Helpers

    private func makeProject(camera: Bool, events: Bool = false, poster: Bool = false) throws -> String {
        let paths = try store.beginRecording()
        try Data("screen".utf8).write(to: paths.screenURL)
        if camera {
            try Data("camera".utf8).write(to: try XCTUnwrap(paths.cameraURL))
        }
        if events {
            try Data("{}".utf8).write(to: paths.eventsURL)
        }
        if poster {
            try Data("poster".utf8).write(to: paths.posterURL)
        }
        _ = try store.completeRecording(
            id: paths.id,
            request: StudioProjectCreationRequest(
                name: "Test",
                screenWidth: 1920,
                screenHeight: 1080,
                screenDuration: 10,
                camera: camera ? StudioCameraCreationInfo(width: 640, height: 480, duration: 10) : nil,
                appVersion: "1.0"
            )
        )
        return paths.id
    }

    private func names(in directory: URL) throws -> [String] {
        try fileManager.contentsOfDirectory(atPath: directory.path).sorted()
    }
}
