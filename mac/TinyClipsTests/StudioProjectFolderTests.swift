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

    func testLibraryFolderFollowsSavedDestinationAndFallsBackToDraft() throws {
        let id = try makeProject(camera: false)
        let paths = try store.paths(forID: id)
        func libraryFolder() throws -> String { try store.withLibraryFolder(id: id) { $0.path } }
        XCTAssertEqual(try libraryFolder(), paths.projectDirectory.path)
        let folder = outsideURL.appendingPathComponent("Demo")
        try store.exportProjectFolder(id: id, to: folder)
        try store.rememberDestination(id: id, file: folder.appendingPathComponent("Demo.tinyclips"))
        XCTAssertEqual(try libraryFolder(), folder.path)
        try fileManager.removeItem(at: folder)
        XCTAssertEqual(try libraryFolder(), paths.projectDirectory.path)
        try Data("{".utf8).write(to: paths.projectJSONURL)
        XCTAssertEqual(try libraryFolder(), paths.projectDirectory.path)
    }

    func testLibraryDeleteRefusesInUseProjectAndPreservesSavedCopyAndExport() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Saved")
        let manifest = folder.appendingPathComponent("Saved.tinyclips")
        let export = outsideURL.appendingPathComponent("Export.mp4")
        try Data("export".utf8).write(to: export)
        _ = try store.recordExport(id: id, path: export.path)
        try store.exportProjectFolder(id: id, to: folder)
        try store.rememberDestination(id: id, file: manifest)
        store.beginUse(id: id)
        XCTAssertThrowsError(try store.deleteLibraryProject(id: id)) {
            XCTAssertEqual($0 as? StudioLibraryActionError, .inUse)
        }
        XCTAssertTrue(store.exists(id: id))
        store.endUse(id: id)
        // Even an unreadable Library copy can be removed.
        try Data("{".utf8).write(to: store.paths(forID: id).projectJSONURL)
        try store.deleteLibraryProject(id: id)
        XCTAssertFalse(store.exists(id: id))
        XCTAssertTrue(fileManager.fileExists(atPath: manifest.path))
        XCTAssertEqual(try Data(contentsOf: export), Data("export".utf8))
        XCTAssertThrowsError(try store.deleteLibraryProject(id: id))
    }

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

    func testAProjectAroundAVideoKeptElsewhereSavesWithACopyOfTheVideo() throws {
        let video = outsideURL.appendingPathComponent("finished.MOV")
        try Data("video".utf8).write(to: video)
        let flat = try store.getOrCreateFlatProject(
            request: StudioFlatProjectRequest(videoURL: video, width: 1920, height: 1080, duration: 5, appVersion: "1.0")
        )
        let folder = outsideURL.appendingPathComponent("Flat")

        try store.exportProjectFolder(id: flat.id, to: folder)

        XCTAssertEqual(try names(in: folder), ["Flat.tinyclips", "screen.mov"])
        XCTAssertEqual(try Data(contentsOf: folder.appendingPathComponent("screen.mov")), Data("video".utf8))
        XCTAssertEqual(try Data(contentsOf: video), Data("video".utf8))
        XCTAssertTrue(try store.load(id: flat.id).sources.screen.external)

        let projectFile = folder.appendingPathComponent("Flat.tinyclips")
        let imported = try store.importProjectFolder(projectFile: projectFile)
        XCTAssertFalse(imported.sources.screen.external)
        XCTAssertEqual(try Data(contentsOf: try store.paths(for: imported).screenURL), Data("video".utf8))

        var edited = try store.load(id: flat.id)
        edited.zooms = [StudioZoom(start: 1, end: 2, scale: 2)]
        try store.save(edited)
        try store.updateSavedProject(id: flat.id, projectFile: projectFile)
        let updated = try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(contentsOf: projectFile))
        XCTAssertEqual(updated.zooms, edited.zooms)
        XCTAssertEqual(try names(in: folder), ["Flat.tinyclips", "screen.mov"])
    }

    func testSavingAnImportedSymlinkCopiesTheVideoNotTheLink() throws {
        let video = outsideURL.appendingPathComponent("source.mov")
        let link = outsideURL.appendingPathComponent("alias.mov")
        try Data("video".utf8).write(to: video)
        try fileManager.createSymbolicLink(atPath: link.path, withDestinationPath: video.lastPathComponent)
        let project = try store.getOrCreateFlatProject(
            request: StudioFlatProjectRequest(videoURL: link, width: 1920, height: 1080, duration: 5, appVersion: "1.0")
        )
        let folder = outsideURL.appendingPathComponent("Symlink Copy")
        try store.exportProjectFolder(id: project.id, to: folder)
        let copied = folder.appendingPathComponent("screen.mov")
        XCTAssertEqual(try copied.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink, false)
        try fileManager.removeItem(at: link)
        try fileManager.removeItem(at: video)
        XCTAssertEqual(try Data(contentsOf: copied), Data("video".utf8))
    }

    @MainActor
    func testAgentProjectCopyUsesOnlyANewFolderInTheVideoDirectory() throws {
        let id = try makeProject(camera: false)
        let projectDirectory = try store.paths(forID: id).projectDirectory
        let folder = try AutomationCommandRouter.studioProjectCopyDestination(
            name: "Agent Copy", videoFolder: outsideURL, projectDirectory: projectDirectory
        )
        try store.exportProjectFolder(id: id, to: folder)
        let projectFile = folder.appendingPathComponent("Agent Copy.tinyclips")
        let reopened = try store.importProjectFolder(projectFile: projectFile)
        XCTAssertEqual(try Data(contentsOf: try store.paths(for: reopened).screenURL), Data("screen".utf8))
        XCTAssertThrowsError(try store.exportProjectFolder(id: id, to: folder)) { error in
            XCTAssertEqual(error as? StudioProjectFolderError, .destinationExists)
        }
        XCTAssertTrue(fileManager.fileExists(atPath: projectFile.path))

        for name in ["", ".", "..", "../other", "a/b", "a\\b", "bad\0name"] {
            XCTAssertThrowsError(try AutomationCommandRouter.studioProjectCopyDestination(
                name: name, videoFolder: outsideURL, projectDirectory: projectDirectory
            )) { error in
                XCTAssertEqual((error as? AutomationError)?.code, .invalidParams)
            }
        }
        XCTAssertThrowsError(try AutomationCommandRouter.studioProjectCopyDestination(
            name: "inside", videoFolder: projectDirectory, projectDirectory: projectDirectory
        )) { error in
            XCTAssertEqual((error as? AutomationError)?.code, .invalidParams)
        }
    }

    func testSavingDoesNotReplaceABrokenDestinationSymlink() throws {
        let id = try makeProject(camera: false)
        let link = outsideURL.appendingPathComponent("Taken")
        try fileManager.createSymbolicLink(atPath: link.path, withDestinationPath: "missing")
        XCTAssertThrowsError(try store.exportProjectFolder(id: id, to: link)) { error in
            XCTAssertEqual(error as? StudioProjectFolderError, .destinationExists)
        }
        XCTAssertEqual(try fileManager.destinationOfSymbolicLink(atPath: link.path), "missing")
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
        try text.replacingOccurrences(of: "\"schemaVersion\" : 1", with: "\"schemaVersion\" : 3")
            .write(to: file, atomically: true, encoding: .utf8)

        XCTAssertThrowsError(try store.importProjectFolder(projectFile: file)) { error in
            XCTAssertEqual(error as? StudioProjectError, .unsupportedVersion(3))
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

    // MARK: - Recent Captures

    private func menuSummary(
        _ id: String,
        created: TimeInterval = 0,
        opened: TimeInterval = 0,
        isDraft: Bool = true,
        isFlat: Bool = false,
        sourceExists: Bool = true,
        exportMissing: Bool = false
    ) -> StudioProjectSummary {
        StudioProjectSummary(
            id: id,
            name: id,
            createdAt: Date(timeIntervalSince1970: created),
            lastOpenedAt: Date(timeIntervalSince1970: opened),
            isDraft: isDraft,
            isFlat: isFlat,
            keepSources: false,
            sizeOnDisk: 0,
            sourceExists: sourceExists,
            exportMissing: exportMissing
        )
    }

    func testTheMenuListsProjectsThatHoldTheOnlyCopyOfARecordingThatCanBeOpened() {
        let summaries = [
            menuSummary("draft"),
            menuSummary("exported", isDraft: false),
            menuSummary("lost-its-export", isDraft: false, exportMissing: true),
            menuSummary("no-recording", sourceExists: false),
            menuSummary("flat", isFlat: true),
        ]

        XCTAssertEqual(StudioProjectSummary.menuDrafts(from: summaries).map(\.id), ["draft", "lost-its-export"])
    }

    func testSettingsListsFlatDraftsAndMissingExportsWithoutDuplicatingTheRecentMenu() {
        let summaries = [
            menuSummary("flat-draft", isFlat: true),
            menuSummary("flat-lost-export", isDraft: false, isFlat: true, exportMissing: true),
            menuSummary("flat-offline", isFlat: true, sourceExists: false),
            menuSummary("flat-exported", isDraft: false, isFlat: true),
        ]

        XCTAssertEqual(
            summaries.filter(\.isDraftOrLostItsExport).map(\.id),
            ["flat-draft", "flat-lost-export", "flat-offline"]
        )
        XCTAssertTrue(StudioProjectSummary.menuDrafts(from: summaries).isEmpty)
    }

    func testLibraryListsAllProjectsByActivityAndSearchesTheirNames() {
        var untitled = menuSummary("untitled", created: 5)
        untitled.name = " "
        let summaries = [
            menuSummary("exported", created: 10, isDraft: false),
            menuSummary("flat", created: 20, isFlat: true),
            menuSummary("missing", created: 20, sourceExists: false),
            untitled
        ]
        XCTAssertEqual(
            StudioProjectSummary.libraryProjects(from: summaries, matching: "").map(\.id),
            ["flat", "missing", "exported", "untitled"]
        )
        XCTAssertEqual(StudioProjectSummary.libraryProjects(from: summaries, matching: " FLAT ").map(\.id), ["flat"])
        XCTAssertEqual(StudioProjectSummary.libraryProjects(from: summaries, matching: "untitled").map(\.id), ["untitled"])
        XCTAssertTrue(StudioProjectSummary.libraryProjects(from: summaries, matching: "absent").isEmpty)
    }

    func testAProjectWasLastUsedWhenItWasOpenedOrFailingThatRecorded() {
        XCTAssertEqual(menuSummary("a", created: 100, opened: 0).lastUsedAt, Date(timeIntervalSince1970: 100))
        XCTAssertEqual(menuSummary("b", created: 100, opened: 250).lastUsedAt, Date(timeIntervalSince1970: 250))
    }

    func testRecentCapturesAndProjectsAreMixedByDateNewestFirst() {
        func capture(_ name: String, at time: TimeInterval) -> RecentCaptureItem {
            RecentCaptureItem(path: "/tmp/\(name)", type: .video, capturedAt: Date(timeIntervalSince1970: time))
        }
        let captures = [capture("c3", at: 300), capture("c2", at: 200), capture("c1", at: 100)]
        let drafts = [menuSummary("d-new", created: 250), menuSummary("d-tied", created: 200), menuSummary("d-old", created: 50)]

        let merged = RecentMenuEntry.merged(captures: captures, drafts: drafts, limit: 5)

        XCTAssertEqual(
            merged.map(\.id),
            ["capture:/tmp/c3", "studio:d-new", "capture:/tmp/c2", "studio:d-tied", "capture:/tmp/c1"]
        )
        XCTAssertEqual(RecentMenuEntry.merged(captures: captures, drafts: drafts, limit: 2).map(\.id), ["capture:/tmp/c3", "studio:d-new"])
        XCTAssertEqual(RecentMenuEntry.merged(captures: [], drafts: drafts, limit: 5).map(\.id), ["studio:d-new", "studio:d-tied", "studio:d-old"])
        XCTAssertEqual(RecentMenuEntry.merged(captures: captures, drafts: [], limit: 5).count, 3)
        XCTAssertTrue(RecentMenuEntry.merged(captures: captures, drafts: drafts, limit: 0).isEmpty)
    }

    func testRecentCapturePresentationKeepsFullNameKindAndDate() {
        let capture = RecentCaptureItem(
            path: "/tmp/TinyClips Long Capture.png",
            type: .screenshot,
            capturedAt: Date(timeIntervalSince1970: 1_700_000_000)
        )
        let presentation = RecentCapturePresentation(.capture(capture))

        XCTAssertEqual(presentation.id, "capture:/tmp/TinyClips Long Capture.png")
        XCTAssertEqual(presentation.title, "TinyClips Long Capture.png")
        XCTAssertEqual(presentation.kind, "Screenshot")
        XCTAssertEqual(presentation.symbol, "photo")
        XCTAssertTrue(presentation.accessibilityText.contains(presentation.title))
        XCTAssertTrue(presentation.accessibilityText.contains(presentation.date))
    }

    func testRecentCapturesSupportEveryConfiguredDisplayLimit() {
        let captures = (0..<40).map {
            RecentCaptureItem(
                path: "/tmp/capture-\($0).png",
                type: .screenshot,
                capturedAt: Date(timeIntervalSince1970: TimeInterval($0))
            )
        }

        for limit in [5, 10, 30] {
            let displayed = RecentMenuEntry.merged(captures: captures, drafts: [], limit: limit)
            XCTAssertEqual(displayed.count, limit)
            XCTAssertEqual(displayed.first?.date, captures.last?.capturedAt)
        }
    }

    func testCaseVariantsOfTheDestinationRecordCannotReplaceOrExposeAnAsset() throws {
        for name in ["saved-project.json", "Saved-Project.json", "SAVED-PROJECT.JSON"] {
            let id = try makeProject(camera: false)
            var project = try store.load(id: id)
            project.canvas.background.image = name
            try store.save(project)
            let asset = try store.paths(forID: id).projectDirectory.appendingPathComponent(name)
            let original = Data("project asset".utf8)
            try original.write(to: asset)
            let file = outsideURL.appendingPathComponent("Saved.tinyclips")

            XCTAssertThrowsError(try store.rememberDestination(id: id, file: file, bookmark: Data("private bookmark".utf8)))
            XCTAssertThrowsError(try store.savedDestination(id: id))
            XCTAssertEqual(try Data(contentsOf: asset), original)
        }
    }

    func testImportedDestinationSurvivesRestartWithoutTravellingWithTheProject() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Original")
        try store.exportProjectFolder(id: id, to: folder)
        let file = folder.appendingPathComponent("Original.tinyclips")
        let imported = try store.importProjectFolder(projectFile: file)
        let reopenedStore = StudioProjectStore(rootURL: storeURL)
        XCTAssertEqual(try reopenedStore.savedDestination(id: imported.id)?.file, file)
        let copy = outsideURL.appendingPathComponent("Copy")
        try reopenedStore.exportProjectFolder(id: imported.id, to: copy)
        XCTAssertFalse(fileManager.fileExists(atPath: copy.appendingPathComponent("saved-project.json").path))
        XCTAssertFalse(try String(contentsOf: copy.appendingPathComponent("Copy.tinyclips"), encoding: .utf8).contains(outsideURL.path))
    }

    func testOnlyTheFirstSuccessfulDestinationIsAFirstSave() throws {
        let id = try makeProject(camera: false)
        let file = outsideURL.appendingPathComponent("Demo.tinyclips")
        XCTAssertThrowsError(try store.rememberDestination(id: id, file: outsideURL))
        XCTAssertTrue(try store.rememberDestination(id: id, file: file))
        let reopened = StudioProjectStore(rootURL: storeURL)
        XCTAssertFalse(try reopened.rememberDestination(id: id, file: file))
        XCTAssertFalse(try reopened.rememberDestination(id: id, file: outsideURL.appendingPathComponent("Copy.tinyclips")))
    }

    func testLinkedSavePreservesExportedVideosAndOtherContent() throws {
        let id = try makeProject(camera: true)
        let folder = outsideURL.appendingPathComponent("Saved")
        try store.exportProjectFolder(id: id, to: folder)
        let file = folder.appendingPathComponent("Custom Name.tinyclips")
        try fileManager.moveItem(at: folder.appendingPathComponent("Saved.tinyclips"), to: file)
        let imported = try store.importProjectFolder(projectFile: file)
        try Data("export".utf8).write(to: folder.appendingPathComponent("Demo.mp4"))
        try Data("notes".utf8).write(to: folder.appendingPathComponent("notes.txt"))
        let other = folder.appendingPathComponent("Other")
        try fileManager.createDirectory(at: other, withIntermediateDirectories: false)
        try Data("keep".utf8).write(to: other.appendingPathComponent("keep.txt"))
        var edited = imported
        edited.name = "Updated"
        try store.save(edited)

        try store.updateSavedProject(id: imported.id, projectFile: file)

        let saved = try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(contentsOf: file))
        XCTAssertEqual(saved.name, "Updated")
        XCTAssertEqual(try Data(contentsOf: folder.appendingPathComponent("Demo.mp4")), Data("export".utf8))
        XCTAssertEqual(try Data(contentsOf: folder.appendingPathComponent("notes.txt")), Data("notes".utf8))
        XCTAssertEqual(try Data(contentsOf: other.appendingPathComponent("keep.txt")), Data("keep".utf8))
    }

    func testLinkedSaveDoesNotOverwriteANewAssetCollision() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Saved")
        try store.exportProjectFolder(id: id, to: folder)
        let file = folder.appendingPathComponent("Saved.tinyclips")
        let original = try Data(contentsOf: file)
        let background = folder.appendingPathComponent("background.png")
        try Data("unrelated".utf8).write(to: background)
        try Data("new asset".utf8).write(to: try store.paths(forID: id).projectDirectory.appendingPathComponent("background.png"))
        var edited = try store.load(id: id)
        edited.canvas.background.image = "background.png"
        try store.save(edited)

        XCTAssertThrowsError(try store.updateSavedProject(id: id, projectFile: file))
        XCTAssertEqual(try Data(contentsOf: file), original)
        XCTAssertEqual(try Data(contentsOf: background), Data("unrelated".utf8))
    }

    func testLinkedSavePreservesANewAssetThatAppearsAfterPreflightAndRollsBack() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Saved")
        try store.exportProjectFolder(id: id, to: folder)
        let file = folder.appendingPathComponent("Saved.tinyclips")
        let original = try Data(contentsOf: file)
        let background = folder.appendingPathComponent("background.png")
        try Data("new asset".utf8).write(to: try store.paths(forID: id).projectDirectory.appendingPathComponent("background.png"))
        var edited = try store.load(id: id)
        edited.canvas.background.image = "background.png"
        try store.save(edited)

        XCTAssertThrowsError(try store.updateSavedProject(id: id, projectFile: file, beforeCommit: {
            try Data("late unrelated file".utf8).write(to: background)
        }))

        XCTAssertEqual(try Data(contentsOf: background), Data("late unrelated file".utf8))
        XCTAssertEqual(try Data(contentsOf: file), original)
        XCTAssertEqual(try Data(contentsOf: folder.appendingPathComponent("screen.mp4")), Data("screen".utf8))
        XCTAssertFalse(try names(in: folder).contains { $0.hasPrefix(".tinyclips-update-") })
    }

    func testInvalidSavedDestinationIsReported() throws {
        let id = try makeProject(camera: false)
        let record = try store.paths(forID: id).projectDirectory.appendingPathComponent("saved-project.json")
        let invalid = StudioProjectStore.SavedDestination(file: outsideURL.appendingPathComponent("video.mp4"))
        try JSONEncoder().encode(invalid).write(to: record)
        XCTAssertThrowsError(try store.savedDestination(id: id))
    }

    func testSaveAsRefusesToReplaceAFolderContainingAnExport() throws {
        let id = try makeProject(camera: false)
        let folder = outsideURL.appendingPathComponent("Saved")
        try store.exportProjectFolder(id: id, to: folder)
        let exported = folder.appendingPathComponent("Demo.mp4")
        try Data("export".utf8).write(to: exported)
        XCTAssertThrowsError(try store.exportProjectFolder(id: id, to: folder, replacingSavedProject: true))
        XCTAssertEqual(try Data(contentsOf: exported), Data("export".utf8))
    }

    func testExportDestinationReplacesOnlyTheApprovedFile() throws {
        let output = outsideURL.appendingPathComponent("Chosen.mp4")
        let staged = outsideURL.appendingPathComponent("staged.mp4")
        try Data("old".utf8).write(to: output)
        let target = try StudioExportDestination(url: output)
        try Data("rendered".utf8).write(to: staged)
        try target.commit(staged: staged)
        XCTAssertEqual(try Data(contentsOf: output), Data("rendered".utf8))
        XCTAssertFalse(fileManager.fileExists(atPath: staged.path))
    }

    func testExportDestinationDoesNotReplaceAFileThatAppearsWhileRendering() throws {
        let output = outsideURL.appendingPathComponent("Chosen.mp4")
        let staged = outsideURL.appendingPathComponent("staged.mp4")
        let target = try StudioExportDestination(url: output)
        try Data("another file".utf8).write(to: output)
        try Data("rendered".utf8).write(to: staged)
        XCTAssertThrowsError(try target.commit(staged: staged))
        XCTAssertEqual(try Data(contentsOf: output), Data("another file".utf8))
    }

    func testMatchingSizeAndTimestampDoNotAuthorizeAReplacementOrInPlaceEdit() throws {
        for inPlace in [false, true] {
            let output = outsideURL.appendingPathComponent(inPlace ? "InPlace.mp4" : "Replaced.mp4")
            let staged = outsideURL.appendingPathComponent(inPlace ? "InPlace-staged.mp4" : "Replaced-staged.mp4")
            let modified = Date(timeIntervalSince1970: 1_700_000_000)
            try Data("first!".utf8).write(to: output)
            try fileManager.setAttributes([.modificationDate: modified], ofItemAtPath: output.path)
            let target = try StudioExportDestination(url: output)
            if inPlace {
                let handle = try FileHandle(forWritingTo: output)
                try handle.write(contentsOf: Data("other!".utf8))
                try handle.close()
            } else {
                try fileManager.moveItem(at: output, to: outsideURL.appendingPathComponent("Previous.mp4"))
                try Data("other!".utf8).write(to: output)
            }
            try fileManager.setAttributes([.modificationDate: modified], ofItemAtPath: output.path)
            let changed = try StudioExportDestination(url: output)
            XCTAssertEqual(target.size, changed.size)
            XCTAssertEqual(target.modified, changed.modified)
            try Data("rendered".utf8).write(to: staged)

            XCTAssertThrowsError(try target.commit(staged: staged))

            XCTAssertEqual(try Data(contentsOf: output), Data("other!".utf8))
            XCTAssertEqual(try Data(contentsOf: staged), Data("rendered".utf8))
        }
    }

    // MARK: - Timeline imports

    @MainActor
    func testEditorBatchInsertIsAtomicUndoableAndReopensAfterRemovingOriginals() async throws {
        let original = outsideURL.appendingPathComponent("Original.mp4")
        let first = outsideURL.appendingPathComponent("First.mp4")
        let second = outsideURL.appendingPathComponent("Second.mp4")
        try copyMovieFixture("native.mp4", to: original)
        try copyMovieFixture("flat.mp4", to: first)
        try copyMovieFixture("native.mp4", to: second)
        let project = try store.getOrCreateFlatProject(request: StudioFlatProjectRequest(
            videoURL: original, width: 64, height: 48, duration: 1, appVersion: "test"
        ))
        let vm = StudioViewModel(projectID: project.id, store: store)
        await vm.load()
        XCTAssertEqual(vm.state, .ready)
        let before = try store.load(id: project.id)
        let paths = try store.paths(for: before)
        let files = try names(in: paths.projectDirectory)
        await vm.addClip(.dropped([first, outsideURL.appendingPathComponent("Missing.mp4")]), at: 0.5)?.value
        XCTAssertNotNil(vm.assemblyError)
        XCTAssertEqual(try store.load(id: project.id), before)
        XCTAssertEqual(try names(in: paths.projectDirectory), files)
        XCTAssertFalse(vm.canUndo)

        await vm.addClip(.dropped([first, second]), at: 0.5)?.value
        XCTAssertNil(vm.assemblyError)
        let saved = try store.load(id: project.id)
        let lane = try XCTUnwrap(saved.timeline)
        XCTAssertEqual(lane.clips.count, 4)
        XCTAssertEqual(Array(lane.clips[1...2].map(\.name)), ["First", "Second"])
        XCTAssertEqual(vm.selectedClipID, lane.clips[1].id)
        XCTAssertEqual(vm.projectPlayhead, 0.5, accuracy: 0.001)
        XCTAssertEqual(vm.timelineMap?.duration ?? 0, 3, accuracy: 0.001)
        vm.undo()
        XCTAssertFalse(vm.canUndo)
        XCTAssertNil(vm.manifest?.timeline)
        vm.redo()
        vm.saveBeforeQuitting()
        for file in [original, first, second] { try fileManager.removeItem(at: file) }
        vm.tearDown()

        let reopened = StudioViewModel(projectID: project.id, store: store)
        await reopened.load()
        XCTAssertEqual(reopened.state, .ready)
        XCTAssertEqual(reopened.manifest?.timeline, lane)
        reopened.scrubProject(to: 1.5)
        XCTAssertEqual(reopened.selectedClipID, lane.clips[2].id)
        XCTAssertEqual(reopened.playhead, 0, accuracy: 0.001)
        reopened.stepFrame(by: -1)
        XCTAssertEqual(reopened.selectedClipID, lane.clips[1].id)
        reopened.tearDown()
    }

    func testTimelineImportOwnsBothFlatVideosAcrossUndoRedoAndLinkedSave() async throws {
        let original = outsideURL.appendingPathComponent("Original.mp4")
        let second = outsideURL.appendingPathComponent("Second.mp4")
        try copyMovieFixture("flat.mp4", to: original)
        try fileManager.copyItem(at: original, to: second)
        var destination = try store.getOrCreateFlatProject(request: StudioFlatProjectRequest(
            videoURL: original, width: 32, height: 64, duration: 1, appVersion: "test"
        ))
        let diskState = StudioEditableState(project: destination)
        destination.edits.trimStart = 0.25 // Not autosaved yet.
        destination.overlays.branding = true
        let paths = try store.paths(for: destination)
        let oldManifest = try Data(contentsOf: paths.projectJSONURL)
        let authorized = try store.videoSourceIdentity(at: second)
        let prepared = try await store.prepareTimelineImport(destination: destination, videoURL: second, expectedVideo: authorized)
        XCTAssertEqual(try Data(contentsOf: paths.projectJSONURL), oldManifest)
        XCTAssertEqual(prepared.base.timeline?.clips.first?.edits, destination.edits)
        XCTAssertEqual(prepared.base.mediaSources?.first?.kind, .flatVideo)
        XCTAssertEqual(prepared.base.mediaSources?.first?.sources.screen.file, prepared.base.sources.screen.file)
        XCTAssertEqual(prepared.base.sources.screen.width, destination.sources.screen.width)
        XCTAssertEqual(prepared.base.sources.screen.height, destination.sources.screen.height)
        XCTAssertEqual(prepared.base.sources.screen.frameRate, destination.sources.screen.frameRate)
        XCTAssertEqual(prepared.base.mediaSources?.first?.sources.screen.width, 64)
        XCTAssertFalse(prepared.base.sources.screen.external)
        XCTAssertEqual(prepared.source.sources.screen.width, 64)
        XCTAssertEqual(prepared.source.sources.screen.height, 32)
        XCTAssertEqual(prepared.source.sources.screen.rotationDegrees, 270)
        XCTAssertEqual(prepared.source.sources.screen.frameRate, 60, accuracy: 0.1)
        XCTAssertEqual(prepared.clip.sourceEnd, 1, accuracy: 0.001)
        XCTAssertNil(prepared.source.sources.events)
        XCTAssertNil(prepared.source.sources.camera)
        XCTAssertEqual(prepared.clip.overlays?.branding, true)
        let updated = assembled(prepared)
        let export = outsideURL.appendingPathComponent("Existing export.mp4")
        _ = try store.recordExport(id: destination.id, path: export.path)
        let saved = try store.saveEdits(StudioEditableState(project: updated), id: destination.id, expected: diskState)
        prepared.finalize()
        prepared.rollback() // A late cancellation cannot take back an accepted transaction.
        XCTAssertEqual(saved.exports.map(\.path), [export.path])
        XCTAssertThrowsError(try store.saveEdits(diskState, id: destination.id, expected: diskState)) {
            XCTAssertEqual($0 as? StudioProjectFolderError, .staleEdit)
        }
        try fileManager.removeItem(at: original)
        try fileManager.removeItem(at: second)
        let folder = outsideURL.appendingPathComponent("Portable")
        let file = folder.appendingPathComponent("Portable.tinyclips")
        try store.exportProjectFolder(id: saved.id, to: folder)
        let updatedState = StudioEditableState(project: updated)
        let undone = try store.saveEdits(diskState, id: saved.id, expected: updatedState)
        XCTAssertEqual(undone.schemaVersion, 1)
        XCTAssertFalse(undone.sources.screen.external)
        XCTAssertEqual(undone.sources.screen.width, 32)
        XCTAssertEqual(undone.sources.screen.height, 64)
        try store.updateSavedProject(id: saved.id, projectFile: file)
        _ = try store.saveEdits(updatedState, id: saved.id, expected: diskState)
        try store.updateSavedProject(id: saved.id, projectFile: file)
        let imported = try store.importProjectFolder(projectFile: file)
        XCTAssertEqual(imported.mediaSources, saved.mediaSources)
        XCTAssertEqual(imported.timeline, saved.timeline)
        let importedFolder = try store.paths(for: imported).projectDirectory
        for media in try XCTUnwrap(imported.mediaSources) {
            XCTAssertTrue(fileManager.fileExists(atPath: importedFolder.appendingPathComponent(media.sources.screen.file).path))
        }
    }

    func testNativeSnapshotCopiesReferencedLayersAndAssetsInsteadOfTheExport() async throws {
        let destination = try store.load(id: makeProject(camera: false))
        var recording = try store.load(id: makeProject(camera: true, events: true))
        let paths = try store.paths(for: recording)
        let movie = outsideURL.appendingPathComponent("Footage.mp4")
        try copyMovieFixture("native.mp4", to: movie)
        recording.sources.screen.file = "unedited.mp4"
        recording.sources.camera?.file = "bubble.mp4"
        recording.sources.camera?.startOffset = 0.125
        recording.sources.screen.audioTracks = ["microphone"]
        let screen = paths.projectDirectory.appendingPathComponent("unedited.mp4")
        let camera = paths.projectDirectory.appendingPathComponent("bubble.mp4")
        try fileManager.copyItem(at: movie, to: screen)
        try fileManager.copyItem(at: movie, to: camera)
        recording.edits.cuts = [StudioTimeRange(start: 0.5, end: 1)]
        recording.zooms = [StudioZoom(start: 0, end: 0.25, scale: 2)]
        recording.audio.volume = 0.75
        recording.canvas.background.image = "custom.png"
        let imageBytes = try XCTUnwrap(Data(base64Encoded: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNQj+kCAAG7AQ4tijP4AAAAAElFTkSuQmCC"))
        try imageBytes.write(to: paths.projectDirectory.appendingPathComponent("custom.png"))
        let originalManifest = try Data(contentsOf: paths.projectJSONURL)
        let prepared = try await store.prepareTimelineImport(
            destination: destination, videoURL: outsideURL.appendingPathComponent("missing-export.mp4"), recording: recording
        )
        XCTAssertEqual(prepared.source.kind, .nativeRecording)
        XCTAssertEqual(prepared.source.sources.camera?.startOffset, 0.125)
        XCTAssertEqual(prepared.source.sources.screen.audioTracks, ["microphone"])
        XCTAssertEqual(prepared.clip.edits, recording.edits)
        XCTAssertEqual(prepared.clip.zooms, recording.zooms)
        XCTAssertEqual(prepared.clip.audio, recording.audio)
        XCTAssertEqual(prepared.base.canvas, destination.canvas)
        XCTAssertEqual(try Data(contentsOf: paths.projectJSONURL), originalManifest)
        let bytes = try Data(contentsOf: movie)
        XCTAssertEqual(try Data(contentsOf: screen), bytes)
        XCTAssertEqual(try Data(contentsOf: camera), bytes)
        let saved = try store.save(assembled(prepared))
        prepared.finalize()
        try store.delete(id: recording.id)
        let folder = outsideURL.appendingPathComponent("Snapshot")
        try store.exportProjectFolder(id: saved.id, to: folder)
        let file = folder.appendingPathComponent("Snapshot.tinyclips")
        let imported = try store.importProjectFolder(projectFile: file)
        let directory = try store.paths(for: imported).projectDirectory
        let media = try XCTUnwrap(imported.mediaSources?.last)
        XCTAssertEqual(try Data(contentsOf: directory.appendingPathComponent(media.sources.screen.file)), bytes)
        XCTAssertEqual(try Data(contentsOf: directory.appendingPathComponent(try XCTUnwrap(media.sources.camera?.file))), bytes)
        XCTAssertEqual(try Data(contentsOf: directory.appendingPathComponent(try XCTUnwrap(media.sources.events))), Data("{}".utf8))
        guard case .string(let image) = media.extra["recordingBackgroundImage"] else { return XCTFail("Missing background asset") }
        XCTAssertEqual(try Data(contentsOf: directory.appendingPathComponent(image)), imageBytes)
        let previous = try names(in: storeURL)
        try fileManager.removeItem(at: folder.appendingPathComponent(image))
        XCTAssertThrowsError(try store.importProjectFolder(projectFile: file))
        try fileManager.createSymbolicLink(at: folder.appendingPathComponent(image), withDestinationURL: movie)
        XCTAssertThrowsError(try store.importProjectFolder(projectFile: file))
        XCTAssertEqual(try names(in: storeURL), previous)
    }

    func testFailedOrAbandonedPreparationLeavesManifestOriginalAndOtherImportsAlone() async throws {
        let original = outsideURL.appendingPathComponent("External.mp4")
        try copyMovieFixture("flat.mp4", to: original)
        let destination = try store.getOrCreateFlatProject(request: StudioFlatProjectRequest(
            videoURL: original, width: 32, height: 64, duration: 1, appVersion: "test"
        ))
        let paths = try store.paths(for: destination)
        let before = try names(in: paths.projectDirectory)
        let manifest = try Data(contentsOf: paths.projectJSONURL)
        let bytes = try Data(contentsOf: original)
        let bad = outsideURL.appendingPathComponent("Bad.mov")
        try Data("not video".utf8).write(to: bad)
        do {
            _ = try await store.prepareTimelineImport(destination: destination, videoURL: bad)
            XCTFail("Accepted a malformed movie")
        } catch {}
        XCTAssertEqual(try names(in: paths.projectDirectory), before)
        let keep = try await store.prepareTimelineBase(destination: destination)
        let withFirst = try names(in: paths.projectDirectory)
        let abandon = try await store.prepareTimelineImport(destination: destination, videoURL: original)
        abandon.rollback()
        XCTAssertEqual(try names(in: paths.projectDirectory), withFirst)
        keep.rollback()
        XCTAssertEqual(try names(in: paths.projectDirectory), before)
        XCTAssertEqual(try Data(contentsOf: paths.projectJSONURL), manifest)
        XCTAssertEqual(try Data(contentsOf: original), bytes)
    }

    func testClosedProjectEditRefusesANewEditorOrStaleManifest() throws {
        let before = try store.load(id: makeProject(camera: false))
        var edit = before
        edit.audio.muted = true
        store.beginUse(id: before.id)
        XCTAssertNil(try store.save(edit, replacing: before))
        store.endUse(id: before.id)
        _ = try store.recordExport(id: before.id, path: outsideURL.appendingPathComponent("Export.mp4").path)
        XCTAssertNil(try store.save(edit, replacing: before))
        let current = try store.load(id: before.id)
        XCTAssertFalse(current.audio.muted)
        XCTAssertEqual(current.exports.count, 1)
    }

    func testPreparingAnAuthorizedVideoRejectsAReplacementAtTheSamePath() async throws {
        let destination = try store.load(id: makeProject(camera: false))
        let selected = outsideURL.appendingPathComponent("Library.mp4")
        try copyMovieFixture("flat.mp4", to: selected)
        let authorized = try store.videoSourceIdentity(at: selected)
        try fileManager.moveItem(at: selected, to: outsideURL.appendingPathComponent("Previous.mp4"))
        try copyMovieFixture("native.mp4", to: selected)
        let paths = try store.paths(for: destination)
        let before = try names(in: paths.projectDirectory)
        do {
            _ = try await store.prepareTimelineImport(destination: destination, videoURL: selected, expectedVideo: authorized)
            XCTFail("Attached a file other than the authorized Library video")
        } catch {
            XCTAssertEqual((error as? CocoaError)?.code, .fileReadUnknown)
        }
        XCTAssertEqual(try names(in: paths.projectDirectory), before)
        XCTAssertEqual(try store.load(id: destination.id), destination)
    }

    func testInterruptingSecondCopyRollsBackExternalAdoptionAndPartialMedia() async throws {
        let original = outsideURL.appendingPathComponent("First.mp4")
        let incoming = outsideURL.appendingPathComponent("Incoming.mp4")
        try copyMovieFixture("flat.mp4", to: original)
        try copyMovieFixture("native.mp4", to: incoming)
        let destination = try store.getOrCreateFlatProject(request: StudioFlatProjectRequest(
            videoURL: original, width: 32, height: 64, duration: 1, appVersion: "test"
        ))
        let paths = try store.paths(for: destination)
        let before = try names(in: paths.projectDirectory)
        let manifest = try Data(contentsOf: paths.projectJSONURL)
        for cancel in [false, true] {
            let manager = InterruptedCopyManager(file: incoming.lastPathComponent, cancel: cancel)
            let interrupted = StudioProjectStore(rootURL: storeURL, fileManager: manager)
            let task = Task { try await interrupted.prepareTimelineImport(destination: destination, videoURL: incoming) }
            do {
                _ = try await task.value
                XCTFail("Interrupted import succeeded")
            } catch {
                if cancel { XCTAssertTrue(error is CancellationError) }
                else { XCTAssertEqual((error as? CocoaError)?.code, .fileWriteOutOfSpace) }
            }
            XCTAssertEqual(try Data(contentsOf: paths.projectJSONURL), manifest)
            XCTAssertEqual(try names(in: paths.projectDirectory), before)
            XCTAssertTrue(fileManager.fileExists(atPath: original.path))
            XCTAssertTrue(fileManager.fileExists(atPath: incoming.path))
        }
    }

    // MARK: - Helpers

    private final class InterruptedCopyManager: FileManager, @unchecked Sendable {
        let file: String
        let cancel: Bool

        init(file: String, cancel: Bool) {
            self.file = file
            self.cancel = cancel
            super.init()
        }

        override func copyItem(at source: URL, to destination: URL) throws {
            try super.copyItem(at: source, to: destination)
            guard source.lastPathComponent == file else { return }
            if cancel { withUnsafeCurrentTask { $0?.cancel() } } else { throw CocoaError(.fileWriteOutOfSpace) }
        }
    }

    private func assembled(_ prepared: StudioPreparedTimelineImport) -> StudioProject {
        var project = prepared.base
        project.mediaSources?.append(prepared.source)
        project.timeline?.clips.append(prepared.clip)
        return project
    }

    private func copyMovieFixture(_ name: String, to url: URL) throws {
        let fixture = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .appendingPathComponent("Fixtures/StudioRendering").appendingPathComponent(name)
        try fileManager.copyItem(at: fixture, to: url)
    }

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
