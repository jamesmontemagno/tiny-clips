import AppKit
import SwiftUI
import AVFoundation
import AVKit
import SwiftData
import ImageIO

// MARK: - Root View

private struct ClipsManagerRootView: View {
    var body: some View {
        ClipsManagerContentView()
    }
}

@MainActor
func clipsManagerRootView() -> some View {
    ClipsManagerRootView()
        .frame(minWidth: 760, minHeight: 480)
}

// MARK: - Clip Item Model

struct ClipItem: Identifiable {
    let url: URL
    let type: CaptureType
    let date: Date
    let fileSize: Int64

    init?(url: URL) {
        let ext = url.pathExtension.lowercased()
        switch ext {
        case "png", "jpg", "jpeg": self.type = .screenshot
        case "mp4": self.type = .video
        case "gif": self.type = .gif
        default: return nil
        }
        let resources = try? url.resourceValues(forKeys: [.creationDateKey, .fileSizeKey])
        self.date = resources?.creationDate ?? Date()
        self.fileSize = Int64(resources?.fileSize ?? 0)
        self.url = url
    }

    var typeLabel: String { type.label }

    var formattedSize: String {
        let bytes = Double(fileSize)
        if bytes < 1_024 { return "\(fileSize) B" }
        if bytes < 1_048_576 { return String(format: "%.1f KB", bytes / 1_024) }
        return String(format: "%.1f MB", bytes / 1_048_576)
    }

    var typeIcon: String {
        switch type {
        case .screenshot: return "photo"
        case .video: return "video"
        case .gif: return "photo.on.rectangle"
        }
    }

    var filePath: String { url.path }
    var id: String { filePath }
}

// MARK: - Metadata

struct ClipMetadata {
    var displayName: String?
    var tags: [String]
    var isFavorite: Bool
    var notes: String
    var collection: String?
    var uploadcareURL: String?
}

@Model
final class ClipMetadataRecord {
    @Attribute(.unique) var clipPath: String
    var displayName: String?
    var tagsBlob: String
    var isFavorite: Bool
    var notes: String
    var collection: String?
    var uploadcareURL: String?

    init(
        clipPath: String,
        displayName: String? = nil,
        tags: [String] = [],
        isFavorite: Bool = false,
        notes: String = "",
        collection: String? = nil,
        uploadcareURL: String? = nil
    ) {
        self.clipPath = clipPath
        self.displayName = displayName
        self.tagsBlob = tags.joined(separator: "\n")
        self.isFavorite = isFavorite
        self.notes = notes
        self.collection = collection
        self.uploadcareURL = uploadcareURL
    }

    var tags: [String] {
        get {
            tagsBlob
                .split(separator: "\n")
                .map { String($0).trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
        }
        set {
            tagsBlob = newValue.joined(separator: "\n")
        }
    }
}

@MainActor
final class ClipMetadataStore {
    static let shared = ClipMetadataStore()

    private let modelContext: ModelContext?

    private init() {
        do {
            let container = try ModelContainer(for: ClipMetadataRecord.self)
            modelContext = ModelContext(container)
        } catch {
            modelContext = nil
        }
    }

    func metadataMap() -> [String: ClipMetadata] {
        guard let modelContext else { return [:] }

        let descriptor = FetchDescriptor<ClipMetadataRecord>()
        guard let records = try? modelContext.fetch(descriptor) else { return [:] }

        return records.reduce(into: [String: ClipMetadata]()) { partial, record in
            partial[record.clipPath] = ClipMetadata(
                displayName: record.displayName,
                tags: record.tags,
                isFavorite: record.isFavorite,
                notes: record.notes,
                collection: record.collection,
                uploadcareURL: record.uploadcareURL
            )
        }
    }

    func upsert(path: String, mutate: (inout ClipMetadata) -> Void) {
        guard let modelContext else { return }

        var descriptor = FetchDescriptor<ClipMetadataRecord>(
            predicate: #Predicate { $0.clipPath == path }
        )
        descriptor.fetchLimit = 1

        let record = (try? modelContext.fetch(descriptor).first) ?? ClipMetadataRecord(clipPath: path)
        if record.modelContext == nil {
            modelContext.insert(record)
        }

        var metadata = ClipMetadata(
            displayName: record.displayName,
            tags: record.tags,
            isFavorite: record.isFavorite,
            notes: record.notes,
            collection: record.collection,
            uploadcareURL: record.uploadcareURL
        )
        mutate(&metadata)

        record.displayName = metadata.displayName?.trimmingCharacters(in: .whitespacesAndNewlines)
        record.tags = metadata.tags
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        record.isFavorite = metadata.isFavorite
        record.notes = metadata.notes.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedCollection = metadata.collection?.trimmingCharacters(in: .whitespacesAndNewlines)
        record.collection = (trimmedCollection?.isEmpty == false) ? trimmedCollection : nil
        record.uploadcareURL = metadata.uploadcareURL?.trimmingCharacters(in: .whitespacesAndNewlines)

        try? modelContext.save()
    }

    func remove(path: String) {
        guard let modelContext else { return }

        var descriptor = FetchDescriptor<ClipMetadataRecord>(
            predicate: #Predicate { $0.clipPath == path }
        )
        descriptor.fetchLimit = 1

        if let record = try? modelContext.fetch(descriptor).first {
            modelContext.delete(record)
            try? modelContext.save()
        }
    }
}

// MARK: - Clips View Model

@MainActor
private class ClipsViewModel: ObservableObject {
    @Published var clips: [ClipItem] = []
    @Published var thumbnails: [String: NSImage] = [:]
    @Published var isLoading = false
    @Published var sortOption: SortOption = .newest { didSet { persistUIStateIfNeeded() } }
    @Published var filterType: FilterType = .all { didSet { persistUIStateIfNeeded() } }
    @Published var smartCollection: SmartCollection = .all { didSet { persistUIStateIfNeeded() } }
    @Published var dateFilter: DateFilter = .allTime { didSet { persistUIStateIfNeeded() } }
    @Published var viewMode: ViewMode = .grid { didSet { persistUIStateIfNeeded() } }
    @Published var searchText = "" { didSet { persistUIStateIfNeeded() } }
    @Published var selectedTag: String = "" { didSet { persistUIStateIfNeeded() } }
    @Published var selectedCollection: String = "" { didSet { persistUIStateIfNeeded() } }
    @Published var selectionMode = false
    @Published var selectedClipIDs: Set<String> = []
    /// The clip shown in the inspector pane (independent of multi-selection).
    @Published var inspectedClipID: String?
    @Published var uploadingClipIDs: Set<String> = []
    @Published var uploadStatusByPath: [String: String] = [:]
    @Published var uploadProgressByPath: [String: Double] = [:]
    @Published private var metadataByPath: [String: ClipMetadata] = [:]

    private let metadataStore = ClipMetadataStore.shared
    private let defaults = UserDefaults.standard
    private var isApplyingStatePreferences = false

    private var editorWindows: [NSWindow] = []

    enum SortOption: String, CaseIterable {
        case newest = "Newest First"
        case oldest = "Oldest First"
        case largest = "Largest"
        case name = "Name"
        case favoritesFirst = "Favorites First"
    }

    enum FilterType: String, CaseIterable {
        case all = "All"
        case screenshots = "Screenshots"
        case videos = "Videos"
        case gifs = "GIFs"
        case favorites = "Favorites"
    }

    enum SmartCollection: String, CaseIterable {
        case all = "All Clips"
        case recent = "Recent"
        case thisWeek = "This Week"
        case thisMonth = "This Month"
        case largeFiles = "Large Files"
        case favorites = "Favorites"
        case screenshots = "Screenshots"
        case videos = "Videos"
        case gifs = "GIFs"

        var icon: String {
            switch self {
            case .all: return "tray.2"
            case .recent: return "clock"
            case .thisWeek: return "calendar"
            case .thisMonth: return "calendar.badge.clock"
            case .largeFiles: return "externaldrive"
            case .favorites: return "star"
            case .screenshots: return "camera"
            case .videos: return "video"
            case .gifs: return "photo.on.rectangle"
            }
        }
    }

    enum DateFilter: String, CaseIterable {
        case allTime = "Any Date"
        case today = "Today"
        case last7Days = "Last 7 Days"
        case last30Days = "Last 30 Days"
    }

    enum ViewMode: String {
        case grid, list
    }

    init() {
        applyInitialPreferences()
    }

    /// The clip currently shown in the inspector, or nil when it was deleted/filtered away.
    var inspectedItem: ClipItem? {
        guard let inspectedClipID else { return nil }
        return clips.first { $0.id == inspectedClipID }
    }

    func inspect(_ item: ClipItem) {
        inspectedClipID = item.id
    }

    func isInspected(_ item: ClipItem) -> Bool {
        inspectedClipID == item.id
    }

    var filteredSortedClips: [ClipItem] {
        var result = clips.filter { matches($0, smartCollection: smartCollection) }

        // Type filter (additional, on top of smart collection)
        switch filterType {
        case .all: break
        case .screenshots: result = result.filter { $0.type == .screenshot }
        case .videos: result = result.filter { $0.type == .video }
        case .gifs: result = result.filter { $0.type == .gif }
        case .favorites: result = result.filter { isFavorite($0) }
        }

        if dateFilter != .allTime {
            let now = Date()
            result = result.filter { item in
                switch dateFilter {
                case .allTime:
                    return true
                case .today:
                    return Calendar.current.isDateInToday(item.date)
                case .last7Days:
                    guard let threshold = Calendar.current.date(byAdding: .day, value: -7, to: now) else { return true }
                    return item.date >= threshold
                case .last30Days:
                    guard let threshold = Calendar.current.date(byAdding: .day, value: -30, to: now) else { return true }
                    return item.date >= threshold
                }
            }
        }

        if !selectedTag.isEmpty {
            result = result.filter { allTags(for: $0).contains(selectedTag) }
        }

        if !selectedCollection.isEmpty {
            result = result.filter { collection(for: $0) == selectedCollection }
        }

        if !searchText.isEmpty {
            result = result.filter { matchesSearch(item: $0, query: searchText) }
        }

        switch sortOption {
        case .newest: result = result.sorted { $0.date > $1.date }
        case .oldest: result = result.sorted { $0.date < $1.date }
        case .largest: result = result.sorted { $0.fileSize > $1.fileSize }
        case .name: result = result.sorted {
                displayName(for: $0).localizedCaseInsensitiveCompare(displayName(for: $1)) == .orderedAscending
            }
        case .favoritesFirst:
            result = result.sorted {
                if isFavorite($0) == isFavorite($1) {
                    return $0.date > $1.date
                }
                return isFavorite($0)
            }
        }

        return result
    }

    /// Whether `item` belongs to `collection`; shared by filtering and the sidebar badges.
    func matches(_ item: ClipItem, smartCollection collection: SmartCollection) -> Bool {
        let now = Date()
        switch collection {
        case .all:
            return true
        case .recent:
            guard let threshold = Calendar.current.date(byAdding: .day, value: -1, to: now) else { return true }
            return item.date >= threshold
        case .thisWeek:
            guard let threshold = Calendar.current.date(byAdding: .day, value: -7, to: now) else { return true }
            return item.date >= threshold
        case .thisMonth:
            guard let threshold = Calendar.current.date(byAdding: .day, value: -30, to: now) else { return true }
            return item.date >= threshold
        case .largeFiles:
            return item.fileSize > 5_242_880
        case .favorites:
            return isFavorite(item)
        case .screenshots:
            return item.type == .screenshot
        case .videos:
            return item.type == .video
        case .gifs:
            return item.type == .gif
        }
    }

    func count(for collection: SmartCollection) -> Int {
        clips.reduce(into: 0) { total, item in
            if matches(item, smartCollection: collection) { total += 1 }
        }
    }

    /// True when anything beyond the default "All Clips" view narrows the list.
    var hasActiveFilters: Bool {
        filterType != .all
            || dateFilter != .allTime
            || smartCollection != .all
            || !selectedTag.isEmpty
            || !selectedCollection.isEmpty
            || !searchText.isEmpty
    }

    func clearFilters() {
        filterType = .all
        dateFilter = .allTime
        smartCollection = .all
        selectedTag = ""
        selectedCollection = ""
        searchText = ""
    }

    // MARK: - Loading

    func load() {
        isLoading = true
        selectedClipIDs = []
        metadataByPath = metadataStore.metadataMap()
        Task {
            let urls = await scanForClips()
            self.clips = urls.compactMap { ClipItem(url: $0) }
            self.isLoading = false
            loadThumbnails()
        }
    }

    private func scanForClips() async -> [URL] {
        let directories = clipDirectories()
        archiveOldClipsIfNeeded(in: directories)
        var urls: [URL] = []
        for dir in directories {
            urls.append(contentsOf: filesInDirectory(dir))
        }
        // Deduplicate by path
        var seen = Set<String>()
        return urls.filter { seen.insert($0.path).inserted }
    }

    private func clipDirectories() -> [URL] {
        let types: [CaptureType] = [.screenshot, .video, .gif]
        var seen = Set<String>()
        return types
            .map { SaveService.shared.outputDirectoryURL(for: $0) }
            .filter { seen.insert($0.standardizedFileURL.path).inserted }
    }

    private func filesInDirectory(_ dir: URL) -> [URL] {
        let settings = CaptureSettings.shared
        guard let contents = try? FileManager.default.contentsOfDirectory(
            at: dir,
            includingPropertiesForKeys: [.creationDateKey, .fileSizeKey, .isRegularFileKey],
            options: .skipsHiddenFiles
        ) else { return [] }
        return contents.filter {
            let values = try? $0.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
            let isRegularFile = values?.isRegularFile ?? false
            let fileSize = values?.fileSize ?? 0
            let ext = $0.pathExtension.lowercased()
            guard isRegularFile,
                  fileSize > 0,
                  ["png", "jpg", "jpeg", "mp4", "gif"].contains(ext) else { return false }
            if settings.clipsManagerIgnoreNonTinyClipsFiles {
                return $0.lastPathComponent.hasPrefix("TinyClips ")
            }
            return true
        }
    }

    func archiveOldClipsNow() {
        archiveOldClipsIfNeeded(in: clipDirectories())
        load()
    }

    func applyStatePreferences() {
        let settings = CaptureSettings.shared

        isApplyingStatePreferences = true
        defer { isApplyingStatePreferences = false }

        // Start from defaults.
        var resolvedViewMode = ViewMode(rawValue: settings.clipsManagerDefaultViewMode) ?? .grid
        var resolvedSortOption = SortOption(rawValue: settings.clipsManagerDefaultSortOption) ?? .newest
        var resolvedFilterType = FilterType(rawValue: settings.clipsManagerDefaultFilterType) ?? .all
        var resolvedDateFilter = DateFilter(rawValue: settings.clipsManagerDefaultDateFilter) ?? .allTime
        var resolvedSmartCollection: SmartCollection = .all
        var resolvedSearchText = ""
        var resolvedSelectedTag = ""
        var resolvedSelectedCollection = ""

        // If enabled, overlay all last-used values.
        if settings.clipsManagerRememberLastState {
            if let savedViewMode = ViewMode(rawValue: defaults.string(forKey: "clipsManagerLastViewMode") ?? "") {
                resolvedViewMode = savedViewMode
            }
            if let savedSort = SortOption(rawValue: defaults.string(forKey: "clipsManagerLastSortOption") ?? "") {
                resolvedSortOption = savedSort
            }
            if let savedType = FilterType(rawValue: defaults.string(forKey: "clipsManagerLastFilterType") ?? "") {
                resolvedFilterType = savedType
            }
            if let savedDate = DateFilter(rawValue: defaults.string(forKey: "clipsManagerLastDateFilter") ?? "") {
                resolvedDateFilter = savedDate
            }
            if let savedCollection = SmartCollection(rawValue: defaults.string(forKey: "clipsManagerLastSmartCollection") ?? "") {
                resolvedSmartCollection = savedCollection
            }
            resolvedSearchText = defaults.string(forKey: "clipsManagerLastSearchText") ?? ""
            resolvedSelectedTag = defaults.string(forKey: "clipsManagerLastSelectedTag") ?? ""
            resolvedSelectedCollection = defaults.string(forKey: "clipsManagerLastSelectedCollection") ?? ""
        }

        viewMode = resolvedViewMode
        sortOption = resolvedSortOption
        filterType = resolvedFilterType
        dateFilter = resolvedDateFilter
        smartCollection = resolvedSmartCollection
        searchText = resolvedSearchText
        selectedTag = resolvedSelectedTag
        selectedCollection = resolvedSelectedCollection
    }

    func persistUIStateIfNeeded() {
        guard !isApplyingStatePreferences else { return }
        defaults.set(viewMode.rawValue, forKey: "clipsManagerLastViewMode")
        defaults.set(sortOption.rawValue, forKey: "clipsManagerLastSortOption")
        defaults.set(filterType.rawValue, forKey: "clipsManagerLastFilterType")
        defaults.set(dateFilter.rawValue, forKey: "clipsManagerLastDateFilter")
        defaults.set(smartCollection.rawValue, forKey: "clipsManagerLastSmartCollection")
        defaults.set(searchText, forKey: "clipsManagerLastSearchText")
        defaults.set(selectedTag, forKey: "clipsManagerLastSelectedTag")
        defaults.set(selectedCollection, forKey: "clipsManagerLastSelectedCollection")
    }

    private func applyInitialPreferences() {
        applyStatePreferences()
    }

    private func archiveOldClipsIfNeeded(in directories: [URL]) {
        let settings = CaptureSettings.shared
        guard settings.clipsManagerArchiveOldClips else { return }
        guard settings.clipsManagerArchiveAfterDays > 0 else { return }

        let cutoff = Calendar.current.date(byAdding: .day, value: -settings.clipsManagerArchiveAfterDays, to: Date()) ?? .distantPast

        for directory in directories {
            let archiveDirectory = directory.appendingPathComponent("Archive", isDirectory: true)
            try? FileManager.default.createDirectory(at: archiveDirectory, withIntermediateDirectories: true)

            let candidates = filesInDirectory(directory).filter { url in
                let created = (try? url.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantFuture
                return created < cutoff
            }

            for url in candidates {
                let targetURL = uniqueArchivedURL(in: archiveDirectory, originalName: url.lastPathComponent)
                do {
                    try FileManager.default.moveItem(at: url, to: targetURL)
                } catch {
                    SaveService.shared.showError("Could not archive \(url.lastPathComponent): \(error.localizedDescription)")
                }
            }
        }
    }

    private func uniqueArchivedURL(in directory: URL, originalName: String) -> URL {
        let initial = directory.appendingPathComponent(originalName)
        if !FileManager.default.fileExists(atPath: initial.path) {
            return initial
        }

        let ext = initial.pathExtension
        let stem = initial.deletingPathExtension().lastPathComponent
        var suffix = 2
        while true {
            let candidateName = ext.isEmpty ? "\(stem) \(suffix)" : "\(stem) \(suffix).\(ext)"
            let candidateURL = directory.appendingPathComponent(candidateName)
            if !FileManager.default.fileExists(atPath: candidateURL.path) {
                return candidateURL
            }
            suffix += 1
        }
    }

    // MARK: - Thumbnails

    private func loadThumbnails() {
        let items = self.clips
        for item in items {
            let url = item.url
            let type = item.type
            let itemID = item.id
            DispatchQueue.global(qos: .utility).async { [weak self] in
                let image: NSImage?
                switch type {
                case .screenshot, .gif:
                    image = NSImage(contentsOf: url)
                case .video:
                    let asset = AVURLAsset(url: url)
                    let generator = AVAssetImageGenerator(asset: asset)
                    generator.appliesPreferredTrackTransform = true
                    // 2x the 220×135 grid tile so thumbnails stay crisp on Retina displays.
                    generator.maximumSize = CGSize(width: 440, height: 270)
                    let time = CMTime(seconds: 0, preferredTimescale: 600)
                    var generatedImage: CGImage?
                    let semaphore = DispatchSemaphore(value: 0)
                    generator.generateCGImagesAsynchronously(forTimes: [NSValue(time: time)]) { _, cgImage, _, result, _ in
                        if result == .succeeded {
                            generatedImage = cgImage
                        }
                        semaphore.signal()
                    }
                    _ = semaphore.wait(timeout: .now() + 2)
                    if let cgImage = generatedImage {
                        image = NSImage(cgImage: cgImage, size: NSSize(width: cgImage.width, height: cgImage.height))
                    } else {
                        image = nil
                    }
                }
                DispatchQueue.main.async { [weak self] in
                    self?.thumbnails[itemID] = image
                }
            }
        }
    }

    // MARK: - Actions

    func delete(_ item: ClipItem) {
        do {
            try FileManager.default.removeItem(at: item.url)
            clips.removeAll { $0.id == item.id }
            thumbnails.removeValue(forKey: item.id)
            if inspectedClipID == item.id {
                inspectedClipID = nil
            }
            metadataStore.remove(path: item.filePath)
            metadataByPath.removeValue(forKey: item.filePath)
            uploadStatusByPath.removeValue(forKey: item.filePath)
            uploadProgressByPath.removeValue(forKey: item.filePath)
        } catch {
            SaveService.shared.showError("Could not delete clip: \(error.localizedDescription)")
        }
    }

    func copyToClipboard(_ item: ClipItem) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        switch item.type {
        case .screenshot:
            if let image = NSImage(contentsOf: item.url) {
                pasteboard.writeObjects([image])
            }
        case .video, .gif:
            pasteboard.writeObjects([item.url as NSURL])
        }
    }

    func revealInFinder(_ item: ClipItem) {
        NSWorkspace.shared.activateFileViewerSelecting([item.url])
    }

    var canUploadToUploadcare: Bool {
        let settings = CaptureSettings.shared
        guard settings.uploadcareEnabled else { return false }
        let credentials = UploadcareCredentialsStore.shared.credentials()
        return !credentials.publicKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !credentials.secretKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    func uploadToUploadcare(_ item: ClipItem) {
        guard canUploadToUploadcare else {
            SaveService.shared.showError("Configure Uploadcare in Clips Manager settings before uploading.")
            return
        }

        guard uploadingClipIDs.insert(item.id).inserted else { return }
        uploadStatusByPath[item.filePath] = "Uploading…"
        uploadProgressByPath[item.filePath] = 0

        let credentials = UploadcareCredentialsStore.shared.credentials()
        let publicKey = credentials.publicKey
        let secretKey = credentials.secretKey
        let clipPath = item.filePath

        Task {
            defer { uploadingClipIDs.remove(item.id) }
            do {
                let result = try await UploadcareService.shared.upload(
                    fileURL: item.url,
                    publicKey: publicKey,
                    secretKey: secretKey,
                    onProgress: { [weak self] progress in
                        Task { @MainActor in
                            self?.uploadProgressByPath[clipPath] = progress
                            self?.uploadStatusByPath[clipPath] = "Uploading… \(Int(progress * 100))%"
                        }
                    }
                )
                metadataStore.upsert(path: clipPath) { metadata in
                    metadata.uploadcareURL = result.fileURL.absoluteString
                }
                metadataByPath = metadataStore.metadataMap()
                uploadStatusByPath[clipPath] = "Uploaded"
                uploadProgressByPath.removeValue(forKey: clipPath)
                copyTextToClipboard(result.fileURL.absoluteString)
            } catch {
                uploadStatusByPath[clipPath] = "Upload failed"
                uploadProgressByPath.removeValue(forKey: clipPath)
                SaveService.shared.showError("Uploadcare upload failed: \(error.localizedDescription)")
            }
        }
    }

    private func copyTextToClipboard(_ text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }

    func displayName(for item: ClipItem) -> String {
        let customName = metadataByPath[item.filePath]?.displayName?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let customName, !customName.isEmpty {
            return customName
        }
        return item.url.lastPathComponent
    }

    func note(for item: ClipItem) -> String {
        metadataByPath[item.filePath]?.notes ?? ""
    }

    func uploadStatus(for item: ClipItem) -> String? {
        uploadStatusByPath[item.filePath]
    }

    func uploadProgress(for item: ClipItem) -> Double? {
        uploadProgressByPath[item.filePath]
    }

    func uploadcareLink(for item: ClipItem) -> String? {
        metadataByPath[item.filePath]?.uploadcareURL
    }

    func copyUploadcareLink(_ item: ClipItem) {
        guard let link = uploadcareLink(for: item), !link.isEmpty else {
            SaveService.shared.showError("No Uploadcare link found for this clip yet.")
            return
        }
        copyTextToClipboard(link)
    }

    func isFavorite(_ item: ClipItem) -> Bool {
        metadataByPath[item.filePath]?.isFavorite ?? false
    }

    func tags(for item: ClipItem) -> [String] {
        metadataByPath[item.filePath]?.tags ?? []
    }

    func autoTags(for item: ClipItem) -> [String] {
        var tags: [String] = []
        tags.append(item.type.rawValue)

        let hour = Calendar.current.component(.hour, from: item.date)
        switch hour {
        case 5..<12: tags.append("morning")
        case 12..<17: tags.append("afternoon")
        case 17..<21: tags.append("evening")
        default: tags.append("night")
        }

        let weekday = Calendar.current.component(.weekday, from: item.date)
        tags.append((weekday == 1 || weekday == 7) ? "weekend" : "weekday")

        switch item.fileSize {
        case ..<102_400: tags.append("small")
        case 102_400..<5_242_880: tags.append("medium")
        default: tags.append("large")
        }

        return tags
    }

    func allTags(for item: ClipItem) -> [String] {
        tags(for: item) + autoTags(for: item)
    }

    func setDisplayName(_ item: ClipItem, name: String) {
        metadataStore.upsert(path: item.filePath) { metadata in
            let trimmedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
            metadata.displayName = trimmedName.isEmpty ? nil : trimmedName
        }
        metadataByPath = metadataStore.metadataMap()
    }

    func toggleFavorite(_ item: ClipItem) {
        metadataStore.upsert(path: item.filePath) { metadata in
            metadata.isFavorite.toggle()
        }
        metadataByPath = metadataStore.metadataMap()
    }

    func setTags(_ item: ClipItem, tags: [String]) {
        metadataStore.upsert(path: item.filePath) { metadata in
            metadata.tags = tags
        }
        metadataByPath = metadataStore.metadataMap()
    }

    func setNotes(_ item: ClipItem, notes: String) {
        metadataStore.upsert(path: item.filePath) { metadata in
            metadata.notes = notes
        }
        metadataByPath = metadataStore.metadataMap()
    }

    func collection(for item: ClipItem) -> String {
        metadataByPath[item.filePath]?.collection ?? ""
    }

    func setCollection(_ item: ClipItem, collection: String) {
        metadataStore.upsert(path: item.filePath) { metadata in
            let trimmed = collection.trimmingCharacters(in: .whitespacesAndNewlines)
            metadata.collection = trimmed.isEmpty ? nil : trimmed
        }
        metadataByPath = metadataStore.metadataMap()
    }

    var availableTags: [String] {
        let userTags = clips.flatMap { self.tags(for: $0) }
        let autoTags = clips.flatMap { self.autoTags(for: $0) }
        return Array(Set(userTags + autoTags)).sorted()
    }

    var availableCollections: [String] {
        let values = clips.map { collection(for: $0) }.filter { !$0.isEmpty }
        return Array(Set(values)).sorted()
    }

    func isSelected(_ item: ClipItem) -> Bool {
        selectedClipIDs.contains(item.id)
    }

    func toggleSelection(_ item: ClipItem) {
        if selectedClipIDs.contains(item.id) {
            selectedClipIDs.remove(item.id)
        } else {
            selectedClipIDs.insert(item.id)
        }
    }

    func clearSelection() {
        selectedClipIDs.removeAll()
    }

    func selectAllVisible() {
        selectedClipIDs = Set(filteredSortedClips.map(\.id))
    }

    var canSelectAllVisible: Bool {
        let visibleIDs = Set(filteredSortedClips.map(\.id))
        return !visibleIDs.isEmpty && !visibleIDs.isSubset(of: selectedClipIDs)
    }

    var selectedItems: [ClipItem] {
        clips.filter { selectedClipIDs.contains($0.id) }
    }

    func deleteSelected() {
        let items = selectedItems
        for item in items {
            delete(item)
        }
        clearSelection()
    }

    func favoriteSelected() {
        for item in selectedItems where !isFavorite(item) {
            toggleFavorite(item)
        }
    }

    func addTagToSelected(_ tag: String) {
        let cleaned = tag.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !cleaned.isEmpty else { return }
        for item in selectedItems {
            var current = tags(for: item)
            if !current.contains(cleaned) {
                current.append(cleaned)
                setTags(item, tags: current)
            }
        }
    }

    func editClip(_ item: ClipItem) {
        switch item.type {
        case .screenshot:
            presentScreenshotEditor(item)
        case .video:
            presentVideoTrimmer(item)
        case .gif:
            presentGifTrimmer(item)
        }
    }

    private func presentScreenshotEditor(_ item: ClipItem) {
        ScreenshotEditorRegistry.shared.present(imageURL: item.url) { [weak self] _ in
            DispatchQueue.main.async {
                self?.load()
            }
        }
    }

    private func presentVideoTrimmer(_ item: ClipItem) {
        var windowRef: VideoTrimmerWindow?
        let window = VideoTrimmerWindow(videoURL: item.url) { [weak self] _ in
            DispatchQueue.main.async {
                if let windowRef {
                    self?.releaseEditorWindow(windowRef)
                }
                self?.load()
            }
        }
        windowRef = window
        retainEditorWindow(window)
    }

    private func presentGifTrimmer(_ item: ClipItem) {
        guard let gifData = makeGifCaptureData(from: item.url) else {
            SaveService.shared.showError("Could not load GIF frames for trimming.")
            return
        }

        let outputURL = item.url.deletingLastPathComponent()
            .appendingPathComponent(item.url.deletingPathExtension().lastPathComponent + " (trimmed)")
            .appendingPathExtension("gif")

        var windowRef: GifTrimmerWindow?
        let window = GifTrimmerWindow(gifData: gifData, outputURL: outputURL) { [weak self] _ in
            DispatchQueue.main.async {
                if let windowRef {
                    self?.releaseEditorWindow(windowRef)
                }
                self?.load()
            }
        }
        windowRef = window
        retainEditorWindow(window)
    }

    private func retainEditorWindow(_ window: NSWindow) {
        editorWindows.append(window)
        DispatchQueue.main.async {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    private func releaseEditorWindow(_ window: NSWindow) {
        editorWindows.removeAll { $0 === window }
    }

    private func makeGifCaptureData(from url: URL) -> GifCaptureData? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let count = CGImageSourceGetCount(source)
        guard count > 0 else { return nil }

        var frames: [CGImage] = []
        var frameDelay: Double = 0.08
        var maxWidth: CGFloat = 0

        for index in 0..<count {
            guard let cgImage = CGImageSourceCreateImageAtIndex(source, index, nil) else { continue }
            frames.append(cgImage)
            maxWidth = max(maxWidth, CGFloat(cgImage.width))

            if index == 0,
               let props = CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any],
               let gifDict = props[kCGImagePropertyGIFDictionary] as? [CFString: Any] {
                if let unclamped = gifDict[kCGImagePropertyGIFUnclampedDelayTime] as? Double {
                    frameDelay = unclamped
                } else if let delayed = gifDict[kCGImagePropertyGIFDelayTime] as? Double {
                    frameDelay = delayed
                }
            }
        }

        guard !frames.isEmpty else { return nil }
        return GifCaptureData(frames: frames, frameDelay: frameDelay, maxWidth: maxWidth)
    }

    private func matchesSearch(item: ClipItem, query: String) -> Bool {
        let loweredQuery = query.lowercased()
        let tokens = loweredQuery.split(separator: " ").map(String.init)
        let itemTags = allTags(for: item)
        let itemNote = note(for: item).lowercased()
        let itemName = displayName(for: item).lowercased()

        for token in tokens where token.contains(":") {
            if token.hasPrefix("type:") {
                let value = String(token.dropFirst(5))
                if value == "video" && item.type != .video { return false }
                if value == "gif" && item.type != .gif { return false }
                if (value == "shot" || value == "screenshot" || value == "image") && item.type != .screenshot { return false }
            } else if token.hasPrefix("tag:") {
                let value = String(token.dropFirst(4))
                if !itemTags.contains(where: { $0.lowercased() == value }) { return false }
            }
        }

        if loweredQuery.contains("fav") || loweredQuery.contains("favorite") {
            if !isFavorite(item) { return false }
        }

        if loweredQuery.contains("today") && !Calendar.current.isDateInToday(item.date) {
            return false
        }

        let plainTerms = tokens.filter { !$0.contains(":") }
        if plainTerms.isEmpty { return true }

        let searchable = [itemName, item.url.lastPathComponent.lowercased(), itemTags.joined(separator: " ").lowercased(), itemNote]
            .joined(separator: " ")
        return plainTerms.allSatisfy { searchable.localizedCaseInsensitiveContains($0) }
    }
}

// MARK: - Clips Manager Content View

private struct ClipsManagerContentView: View {
    @ObservedObject private var settings = CaptureSettings.shared
    @StateObject private var viewModel = ClipsViewModel()
    @State private var renameClip: ClipItem?
    @State private var tagEditorClip: ClipItem?
    @State private var notesClip: ClipItem?
    @State private var collectionClip: ClipItem?
    @State private var batchTag = ""
    @State private var showProUpsell = false
    @State private var showUploadcareSettings = false
    @State private var sidebarVisibility: NavigationSplitViewVisibility = .automatic
    @State private var pendingDeleteClip: ClipItem?
    @State private var showBatchDeleteConfirmation = false

    private let gridColumns = [
        GridItem(.adaptive(minimum: 220, maximum: 220), spacing: 12)
    ]

    var body: some View {
        NavigationSplitView(columnVisibility: $sidebarVisibility) {
            sidebar
                .navigationSplitViewColumnWidth(min: 170, ideal: 210, max: 280)
        } detail: {
            VStack(spacing: 0) {
                if viewModel.selectionMode {
                    batchToolbar
                }
                content
            }
            .inspector(isPresented: $settings.clipsManagerShowInspector) {
                ClipInspectorView(
                    viewModel: viewModel,
                    item: viewModel.inspectedItem,
                    onEditMedia: { item in viewModel.editClip(item) },
                    onReveal: { item in viewModel.revealInFinder(item) },
                    onCopy: { item in viewModel.copyToClipboard(item) },
                    onDelete: { item in confirmDelete(item) },
                    onUpload: { item in
                        if viewModel.canUploadToUploadcare {
                            viewModel.uploadToUploadcare(item)
                        } else {
                            showUploadcareSettings = true
                        }
                    },
                    onCopyUploadcareLink: { item in viewModel.copyUploadcareLink(item) }
                )
                .inspectorColumnWidth(min: 280, ideal: 320, max: 440)
            }
        }
        .searchable(text: $viewModel.searchText, placement: .toolbar, prompt: "Search names, tags, notes")
        .navigationSubtitle(countSubtitle)
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                // Sort & Filter menu
                Menu {
                    Picker("Sort", selection: $viewModel.sortOption) {
                        ForEach(ClipsViewModel.SortOption.allCases, id: \.self) { opt in
                            Text(opt.rawValue).tag(opt)
                        }
                    }

                    Divider()

                    Picker("Type", selection: $viewModel.filterType) {
                        ForEach(ClipsViewModel.FilterType.allCases, id: \.self) { type in
                            Text(type.rawValue).tag(type)
                        }
                    }

                    Divider()

                    Picker("Date", selection: $viewModel.dateFilter) {
                        ForEach(ClipsViewModel.DateFilter.allCases, id: \.self) { opt in
                            Text(opt.rawValue).tag(opt)
                        }
                    }

                    Divider()

                    Picker("Tag", selection: $viewModel.selectedTag) {
                        Text("All Tags").tag("")
                        ForEach(viewModel.availableTags, id: \.self) { tag in
                            Text(tag).tag(tag)
                        }
                    }
                } label: {
                    Label("Sort & Filter", systemImage: "line.3.horizontal.decrease.circle")
                }

                if viewModel.hasActiveFilters {
                    Button {
                        viewModel.clearFilters()
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                    }
                    .help("Clear filters")
                    .accessibilityLabel("Clear filters")
                    .accessibilityHint("Resets search, type, date, tag, and collection filters.")
                }

                // View mode toggle
                Picker(selection: $viewModel.viewMode) {
                    Image(systemName: "square.grid.2x2").tag(ClipsViewModel.ViewMode.grid)
                    Image(systemName: "list.bullet").tag(ClipsViewModel.ViewMode.list)
                } label: {
                    EmptyView()
                }
                .pickerStyle(.segmented)
                .frame(width: 64)
                .accessibilityLabel("View mode")
                .accessibilityValue(viewModel.viewMode == .grid ? "Grid" : "List")
                .help("Switch between grid and list views.")

                Button {
                    viewModel.load()
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .help("Refresh")
                .accessibilityLabel("Refresh clips")
                .accessibilityHint("Reloads clips from disk.")

                Button(viewModel.selectionMode ? "Done" : "Select") {
                    viewModel.selectionMode.toggle()
                    if !viewModel.selectionMode {
                        viewModel.clearSelection()
                    }
                }
                .help(viewModel.selectionMode ? "Exit selection mode." : "Select multiple clips.")

                Toggle(isOn: $settings.clipsManagerShowInspector) {
                    Image(systemName: "info.circle")
                }
                .toggleStyle(.button)
                .keyboardShortcut("i", modifiers: [.command, .option])
                .help(settings.clipsManagerShowInspector ? "Hide the details pane (⌥⌘I)" : "Show the details pane (⌥⌘I)")
                .accessibilityLabel("Details pane")
                .accessibilityValue(settings.clipsManagerShowInspector ? "Shown" : "Hidden")
                .accessibilityHint("Shows a pane with a preview and editable details for the selected clip.")

                Button {
                    showUploadcareSettings = true
                } label: {
                    Image(systemName: "gearshape")
                }
                .help("Clip Manager settings")
                .accessibilityLabel("Clip Manager settings")
            }
        }
        .onAppear {
            viewModel.applyStatePreferences()
            viewModel.load()
        }
        .task(id: settings.clipsManagerAutoRefreshSeconds) {
            let interval = settings.clipsManagerAutoRefreshSeconds
            guard interval > 0 else { return }
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: UInt64(interval) * 1_000_000_000)
                if Task.isCancelled { break }
                await MainActor.run {
                    viewModel.load()
                }
            }
        }
    #if APPSTORE
        .sheet(isPresented: $showProUpsell) {
            VStack(spacing: 0) {
                HStack {
                    Spacer()
                    Button {
                        showProUpsell = false
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .font(.title3)
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("Close")
                    .accessibilityLabel("Close")
                }
                .padding(.top, 12)
                .padding(.horizontal, 12)

                ProSubscriptionView()
            }
            .frame(minWidth: 500, minHeight: 500)
        }
    #endif
        .sheet(isPresented: $showUploadcareSettings) {
            ClipManagerUploadcareSettingsView(onRunArchiveNow: {
                viewModel.archiveOldClipsNow()
            })
                .frame(width: 500, height: 460)
        }
        .sheet(item: $renameClip) { item in
            ClipRenamePopover(
                currentName: viewModel.displayName(for: item),
                onSave: { newName in
                    viewModel.setDisplayName(item, name: newName)
                    renameClip = nil
                },
                onCancel: {
                    renameClip = nil
                }
            )
        }
        .sheet(item: $tagEditorClip) { item in
            ClipTagEditorPopover(
                initialTags: viewModel.tags(for: item),
                onSave: { tags in
                    viewModel.setTags(item, tags: tags)
                    tagEditorClip = nil
                },
                onCancel: {
                    tagEditorClip = nil
                }
            )
        }
        .sheet(item: $notesClip) { item in
            ClipNotesPopover(
                initialNotes: viewModel.note(for: item),
                onSave: { notes in
                    viewModel.setNotes(item, notes: notes)
                    notesClip = nil
                },
                onCancel: {
                    notesClip = nil
                }
            )
        }
        .sheet(item: $collectionClip) { item in
            ClipCollectionPopover(
                initialCollection: viewModel.collection(for: item),
                suggestions: viewModel.availableCollections,
                onSave: { value in
                    viewModel.setCollection(item, collection: value)
                    collectionClip = nil
                },
                onCancel: {
                    collectionClip = nil
                }
            )
        }
        .alert("Delete clip?", isPresented: Binding(
            get: { pendingDeleteClip != nil },
            set: { if !$0 { pendingDeleteClip = nil } }
        )) {
            Button("Delete", role: .destructive) {
                if let pendingDeleteClip {
                    viewModel.delete(pendingDeleteClip)
                }
                pendingDeleteClip = nil
            }
            Button("Cancel", role: .cancel) {
                pendingDeleteClip = nil
            }
        } message: {
            Text("This action cannot be undone.")
        }
        .alert("Delete selected clips?", isPresented: $showBatchDeleteConfirmation) {
            Button("Delete", role: .destructive) {
                viewModel.deleteSelected()
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This will permanently remove \(viewModel.selectedItems.count) clip(s).")
        }
    }

    private func confirmDelete(_ item: ClipItem) {
        if settings.clipsManagerConfirmDelete {
            pendingDeleteClip = item
        } else {
            viewModel.delete(item)
        }
    }

    /// Selects the clip for the inspector and makes sure the pane is visible.
    private func openDetails(for item: ClipItem) {
        viewModel.inspect(item)
        if !settings.clipsManagerShowInspector {
            settings.clipsManagerShowInspector = true
        }
    }

    private func colorForTag(_ tag: String) -> Color {
        let palette: [Color] = [.blue, .purple, .pink, .orange, .green, .teal, .indigo]
        let hash = tag.unicodeScalars.reduce(0) { $0 + Int($1.value) }
        return palette[hash % palette.count]
    }

    private var countSubtitle: String {
        let visible = viewModel.filteredSortedClips.count
        let total = viewModel.clips.count
        if total == 0 { return "" }
        if visible == total { return total == 1 ? "1 clip" : "\(total) clips" }
        return "\(visible) of \(total) clips"
    }

    // MARK: - Sidebar

    private var sidebar: some View {
        List(selection: $viewModel.smartCollection) {
            Section("Smart Collections") {
                ForEach(ClipsViewModel.SmartCollection.allCases, id: \.self) { collection in
                    Label(collection.rawValue, systemImage: collection.icon)
                        .badge(viewModel.count(for: collection))
                        .tag(collection)
                        .accessibilityLabel("\(collection.rawValue), \(viewModel.count(for: collection)) clips")
                }
            }

            if !viewModel.availableCollections.isEmpty {
                Section("Collections") {
                    Button {
                        viewModel.selectedCollection = ""
                    } label: {
                        Label("All", systemImage: "folder")
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(viewModel.selectedCollection.isEmpty ? .primary : .secondary)

                    ForEach(viewModel.availableCollections, id: \.self) { collection in
                        Button {
                            viewModel.selectedCollection = collection
                        } label: {
                            Label(collection, systemImage: "folder.fill")
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(viewModel.selectedCollection == collection ? .primary : .secondary)
                    }
                }
            }

            if !viewModel.availableTags.isEmpty {
                Section("Tags") {
                    Button {
                        viewModel.selectedTag = ""
                    } label: {
                        HStack(spacing: 8) {
                            Label("All Tags", systemImage: "tag")
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(
                            viewModel.selectedTag.isEmpty ? Color.accentColor.opacity(0.24) : Color.clear,
                            in: Capsule()
                        )
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(viewModel.selectedTag.isEmpty ? Color.accentColor : .secondary)

                    ForEach(viewModel.availableTags, id: \.self) { tag in
                        Button {
                            viewModel.selectedTag = tag
                        } label: {
                            HStack(spacing: 8) {
                                Circle()
                                    .fill(colorForTag(tag))
                                    .frame(width: 8, height: 8)
                                Text(tag)
                                Spacer(minLength: 0)
                            }
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(
                                viewModel.selectedTag == tag ? Color.accentColor.opacity(0.24) : Color.clear,
                                in: Capsule()
                            )
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(viewModel.selectedTag == tag ? Color.accentColor : .secondary)
                    }
                }
            }
        }
        .listStyle(.sidebar)
    }

    // MARK: - Batch Toolbar

    private var batchToolbar: some View {
        HStack(spacing: 8) {
            Text("\(viewModel.selectedItems.count) selected")
                .font(.caption)
                .foregroundStyle(.secondary)

            Spacer()

            Button("Select All") { viewModel.selectAllVisible() }
                .disabled(!viewModel.canSelectAllVisible)
            Button("Favorite") { viewModel.favoriteSelected() }
                .disabled(viewModel.selectedItems.isEmpty)
            Button("Delete", role: .destructive) {
                if settings.clipsManagerConfirmDelete {
                    showBatchDeleteConfirmation = true
                } else {
                    viewModel.deleteSelected()
                }
            }
                .disabled(viewModel.selectedItems.isEmpty)

            TextField("Tag", text: $batchTag)
                .textFieldStyle(.roundedBorder)
                .frame(width: 100)
            Button("Add Tag") {
                viewModel.addTagToSelected(batchTag)
                batchTag = ""
            }
            .disabled(viewModel.selectedItems.isEmpty || batchTag.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(.quaternary.opacity(0.5))
    }

    // MARK: - Content

    @ViewBuilder
    private var content: some View {
        if viewModel.isLoading {
            ProgressView("Loading clips…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if viewModel.filteredSortedClips.isEmpty {
            emptyState
        } else {
            switch viewModel.viewMode {
            case .grid:
                gridContent
            case .list:
                listContent
            }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 12) {
            Image(systemName: viewModel.hasActiveFilters ? "magnifyingglass" : "photo.on.rectangle")
                .font(.system(size: 40))
                .foregroundStyle(.secondary)
            Text(viewModel.hasActiveFilters ? "No clips match your filters" : "No clips yet")
                .font(.headline)
                .foregroundStyle(.secondary)
            if viewModel.hasActiveFilters {
                Text("Try a different search, or clear the filters to see everything.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 300)
                Button("Clear Filters") { viewModel.clearFilters() }
                    .keyboardShortcut(.cancelAction)
            } else {
                Text("Captures will appear here after you take screenshots, record videos, or create GIFs.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 300)
                Button("Open Save Folder") {
                    NSWorkspace.shared.open(SaveService.shared.outputDirectoryURL(for: .screenshot))
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .contain)
    }

    // MARK: - Grid View

    private var gridContent: some View {
        ScrollView {
            LazyVGrid(columns: gridColumns, spacing: 12) {
                ForEach(viewModel.filteredSortedClips) { item in
                    ClipGridCell(
                        item: item,
                        title: viewModel.displayName(for: item),
                        tags: viewModel.tags(for: item),
                        autoTags: viewModel.autoTags(for: item),
                        notes: viewModel.note(for: item),
                        showAutoTags: settings.clipsManagerShowAutoTags,
                        showNotesPreview: settings.clipsManagerShowNotesPreview,
                        showUploadStatus: settings.clipsManagerShowUploadStatus,
                        uploadStatus: viewModel.uploadStatus(for: item),
                        uploadProgress: viewModel.uploadProgress(for: item),
                        hasUploadcareLink: viewModel.uploadcareLink(for: item)?.isEmpty == false,
                        isFavorite: viewModel.isFavorite(item),
                        isSelectionMode: viewModel.selectionMode,
                        isSelected: viewModel.isSelected(item),
                        isInspected: viewModel.isInspected(item),
                        rowTapSelectsInSelectionMode: settings.clipsManagerSelectionRowTapSelects,
                        thumbnail: viewModel.thumbnails[item.id],
                        onDelete: { confirmDelete(item) },
                        onCopy: { viewModel.copyToClipboard(item) },
                        onReveal: { viewModel.revealInFinder(item) },
                        onToggleFavorite: { viewModel.toggleFavorite(item) },
                        onRename: { renameClip = item },
                        onEditTags: { tagEditorClip = item },
                        onEditNotes: { notesClip = item },
                        onEditCollection: { collectionClip = item },
                        onOpenDetails: { openDetails(for: item) },
                        onEditMedia: { viewModel.editClip(item) },
                        onCopyUploadcareLink: { viewModel.copyUploadcareLink(item) },
                        onUpload: {
                            if viewModel.canUploadToUploadcare {
                                viewModel.uploadToUploadcare(item)
                            } else {
                                showUploadcareSettings = true
                            }
                        },
                        canUpload: true,
                        isUploading: viewModel.uploadingClipIDs.contains(item.id),
                        onToggleSelection: { viewModel.toggleSelection(item) }
                    )
                }
            }
            .padding(16)
        }
    }

    // MARK: - List View

    private var listContent: some View {
        List(viewModel.filteredSortedClips) { item in
            ClipListRow(
                item: item,
                title: viewModel.displayName(for: item),
                tags: viewModel.tags(for: item),
                autoTags: viewModel.autoTags(for: item),
                notes: viewModel.note(for: item),
                showAutoTags: settings.clipsManagerShowAutoTags,
                showNotesPreview: settings.clipsManagerShowNotesPreview,
                showQuickActions: settings.clipsManagerShowQuickActions,
                showUploadStatus: settings.clipsManagerShowUploadStatus,
                compactDensity: settings.clipsManagerCompactListDensity,
                rowTapSelectsInSelectionMode: settings.clipsManagerSelectionRowTapSelects,
                uploadStatus: viewModel.uploadStatus(for: item),
                uploadProgress: viewModel.uploadProgress(for: item),
                hasUploadcareLink: viewModel.uploadcareLink(for: item)?.isEmpty == false,
                isFavorite: viewModel.isFavorite(item),
                isSelectionMode: viewModel.selectionMode,
                isSelected: viewModel.isSelected(item),
                isInspected: viewModel.isInspected(item),
                thumbnail: viewModel.thumbnails[item.id],
                onDelete: { confirmDelete(item) },
                onCopy: { viewModel.copyToClipboard(item) },
                onReveal: { viewModel.revealInFinder(item) },
                onToggleFavorite: { viewModel.toggleFavorite(item) },
                onRename: { renameClip = item },
                onEditTags: { tagEditorClip = item },
                onEditNotes: { notesClip = item },
                onEditCollection: { collectionClip = item },
                onOpenDetails: { openDetails(for: item) },
                onEditMedia: { viewModel.editClip(item) },
                onCopyUploadcareLink: { viewModel.copyUploadcareLink(item) },
                onUpload: {
                    if viewModel.canUploadToUploadcare {
                        viewModel.uploadToUploadcare(item)
                    } else {
                        showUploadcareSettings = true
                    }
                },
                canUpload: true,
                isUploading: viewModel.uploadingClipIDs.contains(item.id),
                onToggleSelection: { viewModel.toggleSelection(item) }
            )
        }
        .listStyle(.plain)
    }
}

// MARK: - Grid Cell

private struct ClipGridCell: View {
    let item: ClipItem
    let title: String
    let tags: [String]
    let autoTags: [String]
    let notes: String
    let showAutoTags: Bool
    let showNotesPreview: Bool
    let showUploadStatus: Bool
    let uploadStatus: String?
    let uploadProgress: Double?
    let hasUploadcareLink: Bool
    let isFavorite: Bool
    let isSelectionMode: Bool
    let isSelected: Bool
    let isInspected: Bool
    let rowTapSelectsInSelectionMode: Bool
    let thumbnail: NSImage?
    let onDelete: () -> Void
    let onCopy: () -> Void
    let onReveal: () -> Void
    let onToggleFavorite: () -> Void
    let onRename: () -> Void
    let onEditTags: () -> Void
    let onEditNotes: () -> Void
    let onEditCollection: () -> Void
    let onOpenDetails: () -> Void
    let onEditMedia: () -> Void
    let onCopyUploadcareLink: () -> Void
    let onUpload: () -> Void
    let canUpload: Bool
    let isUploading: Bool
    let onToggleSelection: () -> Void

    @State private var isHovered = false

    var body: some View {
        VStack(spacing: 6) {
            ZStack {
                RoundedRectangle(cornerRadius: 8)
                    .fill(.quaternary)

                if let thumbnail {
                    Image(nsImage: thumbnail)
                        .resizable()
                        .scaledToFill()
                        .frame(width: 220, height: 135)
                } else {
                    Image(systemName: item.typeIcon)
                        .font(.system(size: 28))
                        .foregroundStyle(.secondary)
                }

                // Hover overlay
                if isHovered {
                    RoundedRectangle(cornerRadius: 8)
                        .fill(.black.opacity(0.4))
                    HStack(spacing: 12) {
                        cellButton(icon: "doc.on.doc", help: "Copy", action: onCopy)
                        cellButton(icon: "folder", help: "Reveal in Finder", action: onReveal)
                        cellButton(icon: "pencil", help: item.type == .screenshot ? "Edit Screenshot" : "Trim Clip", action: onEditMedia)
                        Spacer(minLength: 0)
                        cellButton(icon: "trash", help: "Delete", action: onDelete)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.horizontal, 10)
                }
            }
            .frame(width: 220, height: 135)
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .contentShape(RoundedRectangle(cornerRadius: 8))
            .overlay {
                if isInspected {
                    RoundedRectangle(cornerRadius: 8)
                        .strokeBorder(Color.accentColor, lineWidth: 2)
                }
            }
            .overlay(alignment: .topLeading) {
                Button(action: onToggleFavorite) {
                    Image(systemName: isFavorite ? "star.fill" : "star")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(isFavorite ? .yellow : .white.opacity(0.85))
                        .padding(6)
                        .background(.ultraThinMaterial, in: Circle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(isFavorite ? "Remove favorite" : "Add favorite")
                .accessibilityHint("Toggles favorite state for this clip.")
                .padding(8)
            }
            .overlay(alignment: .topTrailing) {
                if isSelectionMode {
                    Button(action: onToggleSelection) {
                        Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                            .foregroundStyle(isSelected ? .green : .white.opacity(0.9))
                            .padding(6)
                            .background(.ultraThinMaterial, in: Circle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(isSelected ? "Deselect clip" : "Select clip")
                    .accessibilityHint("Toggles clip selection.")
                    .padding(8)
                } else {
                    Text(item.typeLabel)
                        .font(.caption2.weight(.semibold))
                        .lineLimit(1)
                        .minimumScaleFactor(0.8)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(.ultraThinMaterial, in: Capsule())
                        .padding(8)
                }
            }
            .onHover { isHovered = $0 }
            .onTapGesture(count: 2) {
                if !isSelectionMode {
                    onEditMedia()
                }
            }
            .onTapGesture {
                if isSelectionMode {
                    if rowTapSelectsInSelectionMode {
                        onToggleSelection()
                    }
                } else {
                    onOpenDetails()
                }
            }

            VStack(spacing: 2) {
                Text(title)
                    .font(.caption)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if !tags.isEmpty || (showAutoTags && !autoTags.isEmpty) {
                    HStack(spacing: 4) {
                        ForEach(tags.prefix(2), id: \.self) { tag in
                            Text(tag)
                                .font(.caption2)
                                .padding(.horizontal, 4)
                                .padding(.vertical, 1)
                                .background(.blue.opacity(0.15), in: Capsule())
                        }
                        if showAutoTags {
                            ForEach(autoTags.prefix(2), id: \.self) { tag in
                                Text(tag)
                                    .font(.caption2)
                                    .foregroundStyle(.tertiary)
                            }
                        }
                    }
                    .lineLimit(1)
                }
                if showNotesPreview && !notes.isEmpty {
                    Text(notes)
                        .font(.caption2)
                        .lineLimit(1)
                        .foregroundStyle(.tertiary)
                }
                if showUploadStatus, let uploadStatus {
                    Text(uploadStatus)
                        .font(.caption2)
                        .foregroundStyle(uploadStatus == "Upload failed" ? .red : .secondary)
                        .lineLimit(1)
                }
                if showUploadStatus, let uploadProgress {
                    ProgressView(value: uploadProgress)
                        .progressViewStyle(.linear)
                        .frame(maxWidth: 120)
                }
                Text(item.date, style: .date)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(width: 220, alignment: .top)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(title), \(item.typeLabel)")
        .accessibilityAddTraits(isInspected ? .isSelected : [])
        .contextMenu {
            Button(isFavorite ? "Remove Favorite" : "Add Favorite") { onToggleFavorite() }
            Button("Rename…") { onRename() }
            Button("Edit Tags…") { onEditTags() }
            Button("Edit Notes…") { onEditNotes() }
            Button("Set Collection…") { onEditCollection() }
            Button("Details…") { onOpenDetails() }
            Button(item.type == .screenshot ? "Edit Screenshot…" : "Trim Clip…") { onEditMedia() }
            Divider()
            Button("Open in Finder") { onReveal() }
            Button("Copy") { onCopy() }
            Divider()
            ShareLink(item: item.url) {
                Label("Share…", systemImage: "square.and.arrow.up")
            }
            Divider()
            Button("Upload to Uploadcare") { onUpload() }
                .disabled(isUploading || !canUpload)
            if hasUploadcareLink {
                Button("Copy Uploadcare Link") { onCopyUploadcareLink() }
            }
            Divider()
            Button("Delete", role: .destructive) { onDelete() }
        }
    }

    private func cellButton(icon: String, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 32, height: 32)
                .background(.ultraThinMaterial, in: Circle())
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
    }
}

// MARK: - List Row

private struct ClipListRow: View {
    let item: ClipItem
    let title: String
    let tags: [String]
    let autoTags: [String]
    let notes: String
    let showAutoTags: Bool
    let showNotesPreview: Bool
    let showQuickActions: Bool
    let showUploadStatus: Bool
    let compactDensity: Bool
    let rowTapSelectsInSelectionMode: Bool
    let uploadStatus: String?
    let uploadProgress: Double?
    let hasUploadcareLink: Bool
    let isFavorite: Bool
    let isSelectionMode: Bool
    let isSelected: Bool
    let isInspected: Bool
    let thumbnail: NSImage?
    let onDelete: () -> Void
    let onCopy: () -> Void
    let onReveal: () -> Void
    let onToggleFavorite: () -> Void
    let onRename: () -> Void
    let onEditTags: () -> Void
    let onEditNotes: () -> Void
    let onEditCollection: () -> Void
    let onOpenDetails: () -> Void
    let onEditMedia: () -> Void
    let onCopyUploadcareLink: () -> Void
    let onUpload: () -> Void
    let canUpload: Bool
    let isUploading: Bool
    let onToggleSelection: () -> Void

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()
    private var thumbnailSize: CGSize {
        compactDensity ? CGSize(width: 52, height: 39) : CGSize(width: 60, height: 45)
    }

    var body: some View {
        HStack(spacing: 12) {
            if isSelectionMode {
                Button(action: onToggleSelection) {
                    Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                        .foregroundStyle(isSelected ? .green : .secondary)
                }
                .buttonStyle(.plain)
                .frame(width: 22, height: 22)
                .contentShape(Rectangle())
                .accessibilityLabel(isSelected ? "Deselect clip" : "Select clip")
            }

            // Thumbnail
            ZStack {
                RoundedRectangle(cornerRadius: 6)
                    .fill(.quaternary)
                    .frame(width: thumbnailSize.width, height: thumbnailSize.height)
                if let thumbnail {
                    Image(nsImage: thumbnail)
                        .resizable()
                        .scaledToFill()
                        .frame(width: thumbnailSize.width, height: thumbnailSize.height)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                } else {
                    Image(systemName: item.typeIcon)
                        .foregroundStyle(.secondary)
                }
            }

            // Info
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(title)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    if isFavorite {
                        Image(systemName: "star.fill")
                            .font(.caption)
                            .foregroundStyle(.yellow)
                    }
                }
                HStack(spacing: 8) {
                    Label(item.typeLabel, systemImage: item.typeIcon)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text("·")
                        .foregroundStyle(.secondary)
                    Text(Self.dateFormatter.string(from: item.date))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text("·")
                        .foregroundStyle(.secondary)
                    Text(item.formattedSize)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if !tags.isEmpty || (showAutoTags && !autoTags.isEmpty) {
                    HStack(spacing: 4) {
                        ForEach(tags, id: \.self) { tag in
                            Text(tag)
                                .font(.caption2)
                                .padding(.horizontal, 4)
                                .padding(.vertical, 1)
                                .background(.blue.opacity(0.15), in: Capsule())
                        }
                        if showAutoTags {
                            ForEach(autoTags.prefix(3), id: \.self) { tag in
                                Text(tag)
                                    .font(.caption2)
                                    .foregroundStyle(.tertiary)
                            }
                        }
                    }
                    .lineLimit(1)
                }
                if showNotesPreview && !notes.isEmpty {
                    Text(notes)
                        .font(.caption2)
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .foregroundStyle(.tertiary)
                }
                if showUploadStatus, let uploadStatus {
                    Text(uploadStatus)
                        .font(.caption2)
                        .foregroundStyle(uploadStatus == "Upload failed" ? .red : .secondary)
                }
                if showUploadStatus, let uploadProgress {
                    ProgressView(value: uploadProgress)
                        .progressViewStyle(.linear)
                        .frame(width: 120)
                }
            }

            Spacer()

            if showQuickActions {
                // Actions
                HStack(spacing: 8) {
                    Button { onToggleFavorite() } label: {
                        Image(systemName: isFavorite ? "star.fill" : "star")
                            .foregroundStyle(isFavorite ? .yellow : .secondary)
                    }
                    .buttonStyle(.borderless)
                    .help(isFavorite ? "Remove Favorite" : "Add Favorite")
                    .accessibilityLabel(isFavorite ? "Remove favorite" : "Add favorite")

                    Button { onOpenDetails() } label: {
                        Image(systemName: "info.circle")
                    }
                    .buttonStyle(.borderless)
                    .help("Details")
                    .accessibilityLabel("Show details")

                    Button { onCopy() } label: {
                        Image(systemName: "doc.on.doc")
                    }
                    .buttonStyle(.borderless)
                    .help("Copy")
                    .accessibilityLabel("Copy clip")

                    Button { onReveal() } label: {
                        Image(systemName: "folder.badge.questionmark")
                    }
                    .buttonStyle(.borderless)
                    .help("Show in Finder")
                    .accessibilityLabel("Show in Finder")

                    ShareLink(item: item.url) {
                        Image(systemName: "square.and.arrow.up")
                    }
                    .buttonStyle(.borderless)
                    .help("Share")
                    .accessibilityLabel("Share clip")

                    Button { onUpload() } label: {
                        if isUploading {
                            ProgressView()
                                .controlSize(.small)
                        } else {
                            Image(systemName: "icloud.and.arrow.up")
                        }
                    }
                    .buttonStyle(.borderless)
                    .help("Upload to Uploadcare")
                    .disabled(isUploading || !canUpload)
                    .accessibilityLabel("Upload to Uploadcare")

                    if hasUploadcareLink {
                        Button { onCopyUploadcareLink() } label: {
                            Image(systemName: "link")
                        }
                        .buttonStyle(.borderless)
                        .help("Copy Uploadcare Link")
                        .accessibilityLabel("Copy Uploadcare link")
                    }

                    Button(role: .destructive) { onDelete() } label: {
                        Image(systemName: "trash")
                            .foregroundStyle(.red)
                    }
                    .buttonStyle(.borderless)
                    .help("Delete")
                    .accessibilityLabel("Delete clip")
                }
            }
        }
        .padding(.vertical, compactDensity ? 2 : 4)
        .padding(.horizontal, 6)
        .background(
            RoundedRectangle(cornerRadius: 6)
                .fill(isInspected ? Color.accentColor.opacity(0.12) : Color.clear)
        )
        .contentShape(Rectangle())
        .accessibilityAddTraits(isInspected ? .isSelected : [])
        .onTapGesture(count: 2) {
            if !isSelectionMode {
                onEditMedia()
            }
        }
        .onTapGesture {
            if isSelectionMode {
                if rowTapSelectsInSelectionMode {
                    onToggleSelection()
                }
            } else {
                onOpenDetails()
            }
        }
        .contextMenu {
            Button(isFavorite ? "Remove Favorite" : "Add Favorite") { onToggleFavorite() }
            Button("Rename…") { onRename() }
            Button("Edit Tags…") { onEditTags() }
            Button("Edit Notes…") { onEditNotes() }
            Button("Set Collection…") { onEditCollection() }
            Button("Details…") { onOpenDetails() }
            Button(item.type == .screenshot ? "Edit Screenshot…" : "Trim Clip…") { onEditMedia() }
            Divider()
            Button("Open in Finder") { onReveal() }
            Button("Copy") { onCopy() }
            Divider()
            ShareLink(item: item.url) {
                Label("Share…", systemImage: "square.and.arrow.up")
            }
            Divider()
            Button("Upload to Uploadcare") { onUpload() }
                .disabled(isUploading || !canUpload)
            if hasUploadcareLink {
                Button("Copy Uploadcare Link") { onCopyUploadcareLink() }
            }
            Divider()
            Button("Delete", role: .destructive) { onDelete() }
        }
    }
}

// MARK: - Metadata Popovers

private struct ClipRenamePopover: View {
    let currentName: String
    let onSave: (String) -> Void
    let onCancel: () -> Void

    @State private var newName = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Rename Clip")
                .font(.headline)
            TextField("Clip Name", text: $newName)
                .textFieldStyle(.roundedBorder)
            HStack {
                Spacer()
                Button("Cancel") { onCancel() }
                Button("Save") { onSave(newName) }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(14)
        .frame(width: 280)
        .onAppear {
            newName = currentName
        }
    }
}

private struct ClipTagEditorPopover: View {
    let initialTags: [String]
    let onSave: ([String]) -> Void
    let onCancel: () -> Void

    @State private var draftTags: [String] = []
    @State private var newTag = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Edit Tags")
                .font(.headline)

            if !draftTags.isEmpty {
                FlowTagWrap(tags: draftTags) { tag in
                    draftTags.removeAll { $0 == tag }
                }
            }

            HStack {
                TextField("Add tag", text: $newTag)
                    .textFieldStyle(.roundedBorder)
                Button("Add") {
                    addTag()
                }
                .disabled(newTag.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            HStack {
                Spacer()
                Button("Cancel") { onCancel() }
                Button("Save") { onSave(draftTags) }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(14)
        .frame(width: 320)
        .onAppear {
            draftTags = initialTags
        }
    }

    private func addTag() {
        let normalized = newTag
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        guard !normalized.isEmpty else { return }
        if !draftTags.contains(normalized) {
            draftTags.append(normalized)
        }
        newTag = ""
    }
}

private struct FlowTagWrap: View {
    let tags: [String]
    let onRemove: (String) -> Void

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 80), spacing: 6)], spacing: 6) {
            ForEach(tags, id: \.self) { tag in
                HStack(spacing: 6) {
                    Text(tag)
                        .lineLimit(1)
                    Button {
                        onRemove(tag)
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Remove tag")
                }
                .font(.caption)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(.quaternary, in: Capsule())
            }
        }
    }
}

private struct ClipNotesPopover: View {
    let initialNotes: String
    let onSave: (String) -> Void
    let onCancel: () -> Void

    @State private var draft = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Clip Notes")
                .font(.headline)
            TextEditor(text: $draft)
                .font(.body)
                .frame(height: 120)
                .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary, lineWidth: 1))
            HStack {
                Spacer()
                Button("Cancel") { onCancel() }
                Button("Save") { onSave(draft) }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(14)
        .frame(width: 340)
        .onAppear {
            draft = initialNotes
        }
    }
}

// MARK: - Inspector

/// Right-hand details pane: preview, primary actions, editable metadata and file facts for the
/// clip selected in the grid or list. Mirrors the Windows Library's details pane.
private struct ClipInspectorView: View {
    @ObservedObject var viewModel: ClipsViewModel
    let item: ClipItem?
    let onEditMedia: (ClipItem) -> Void
    let onReveal: (ClipItem) -> Void
    let onCopy: (ClipItem) -> Void
    let onDelete: (ClipItem) -> Void
    let onUpload: (ClipItem) -> Void
    let onCopyUploadcareLink: (ClipItem) -> Void

    @State private var draftTitle = ""
    @State private var draftTagInput = ""
    @State private var draftTagList: [String] = []
    @State private var draftNotes = ""
    @State private var draftCollection = ""
    @State private var previewImage: NSImage?
    @State private var player: AVPlayer?
    @State private var pixelSize: CGSize?
    @State private var duration: Double?

    private static let dateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        return formatter
    }()

    private static let durationFormatter: DateComponentsFormatter = {
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = [.minute, .second]
        formatter.zeroFormattingBehavior = .pad
        return formatter
    }()

    var body: some View {
        Group {
            if let item {
                inspector(for: item)
            } else {
                placeholder
            }
        }
        .onChange(of: item?.id, initial: true) { _, _ in
            resetDrafts()
        }
        .task(id: item?.id) {
            await loadPreview()
        }
        .onDisappear {
            player?.pause()
        }
    }

    // MARK: Placeholder

    private var placeholder: some View {
        VStack(spacing: 10) {
            Image(systemName: "sidebar.trailing")
                .font(.system(size: 34))
                .foregroundStyle(.tertiary)
            Text("Select a clip to preview it")
                .font(.headline)
                .foregroundStyle(.secondary)
            Text("Edit its name, tags, notes and collection here.")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .multilineTextAlignment(.center)
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .combine)
    }

    // MARK: Inspector

    private func inspector(for item: ClipItem) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                preview(for: item)
                primaryActions(for: item)
                secondaryActions(for: item)
                metadataEditor(for: item)
                if let link = viewModel.uploadcareLink(for: item), !link.isEmpty {
                    uploadLinkSection(link, item: item)
                }
                fileFacts(for: item)
            }
            .padding(16)
        }
        .accessibilityLabel("Clip details")
    }

    @ViewBuilder
    private func preview(for item: ClipItem) -> some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10)
                .fill(.quaternary)

            if item.type == .video, let player {
                VideoPlayer(player: player)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                    .accessibilityLabel("Video preview")
            } else if let previewImage {
                Image(nsImage: previewImage)
                    .resizable()
                    .scaledToFit()
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                    .accessibilityLabel("\(item.typeLabel) preview")
            } else if let thumbnail = viewModel.thumbnails[item.id] {
                Image(nsImage: thumbnail)
                    .resizable()
                    .scaledToFit()
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                    .accessibilityLabel("\(item.typeLabel) preview")
            } else {
                Image(systemName: item.typeIcon)
                    .font(.system(size: 32))
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: 180)
        .overlay(alignment: .topLeading) {
            Text(item.typeLabel)
                .font(.caption2.weight(.semibold))
                .padding(.horizontal, 6)
                .padding(.vertical, 2)
                .background(.ultraThinMaterial, in: Capsule())
                .padding(8)
        }
    }

    private func primaryActions(for item: ClipItem) -> some View {
        HStack(spacing: 8) {
            inspectorActionButton(
                item.type == .screenshot ? "Edit" : "Trim",
                systemImage: item.type == .screenshot ? "slider.horizontal.3" : "scissors",
                prominent: true
            ) { onEditMedia(item) }
            inspectorActionButton("Copy", systemImage: "doc.on.doc") { onCopy(item) }
            ShareLink(item: item.url) {
                VStack(spacing: 4) {
                    Image(systemName: "square.and.arrow.up")
                        .font(.callout.weight(.semibold))
                    Text("Share")
                        .font(.caption2)
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 6)
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
            .help("Share this clip")
            inspectorActionButton("Show", systemImage: "folder") { onReveal(item) }
        }
    }

    private func secondaryActions(for item: ClipItem) -> some View {
        HStack(spacing: 8) {
            Button {
                viewModel.toggleFavorite(item)
            } label: {
                Label("Favorite", systemImage: viewModel.isFavorite(item) ? "star.fill" : "star")
                    .foregroundStyle(viewModel.isFavorite(item) ? .yellow : .primary)
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
            .help(viewModel.isFavorite(item) ? "Remove from favorites" : "Add to favorites")
            .accessibilityLabel(viewModel.isFavorite(item) ? "Remove favorite" : "Add favorite")

            Menu {
                Button("Upload to Uploadcare") { onUpload(item) }
                    .disabled(viewModel.uploadingClipIDs.contains(item.id))
                if viewModel.uploadcareLink(for: item)?.isEmpty == false {
                    Button("Copy Uploadcare Link") { onCopyUploadcareLink(item) }
                }
                Divider()
                Button("Delete…", role: .destructive) { onDelete(item) }
            } label: {
                Label("More", systemImage: "ellipsis.circle")
            }
            .menuStyle(.button)
            .buttonStyle(.bordered)
            .controlSize(.small)
            .fixedSize()
            .help("More actions")

            Spacer()
        }
    }

    private func metadataEditor(for item: ClipItem) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            LabeledContent("Name") {
                TextField(item.url.lastPathComponent, text: $draftTitle)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Clip name")
            }
            .labeledContentStyle(.inspectorField)

            VStack(alignment: .leading, spacing: 6) {
                Text("Tags")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                if !draftTagList.isEmpty {
                    FlowTagWrap(tags: draftTagList) { tag in
                        draftTagList.removeAll { $0 == tag }
                    }
                }

                HStack(spacing: 8) {
                    TextField("Add a tag", text: $draftTagInput)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit {
                            addDraftTag(draftTagInput)
                            draftTagInput = ""
                        }
                        .accessibilityLabel("Add tag")
                    Button("Add") {
                        addDraftTag(draftTagInput)
                        draftTagInput = ""
                    }
                    .disabled(draftTagInput.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }

                if !normalizedTagSuggestions.isEmpty {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 6) {
                            ForEach(normalizedTagSuggestions, id: \.self) { suggestion in
                                Button {
                                    toggleDraftTag(suggestion)
                                } label: {
                                    Text(suggestion)
                                        .font(.caption2)
                                        .padding(.horizontal, 8)
                                        .padding(.vertical, 3)
                                        .background(
                                            draftTagList.contains(suggestion) ? Color.accentColor.opacity(0.24) : Color.secondary.opacity(0.14),
                                            in: Capsule()
                                        )
                                }
                                .buttonStyle(.plain)
                                .foregroundStyle(draftTagList.contains(suggestion) ? Color.accentColor : .primary)
                                .accessibilityLabel("\(draftTagList.contains(suggestion) ? "Remove" : "Add") tag \(suggestion)")
                            }
                        }
                    }
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Collection")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                TextField("None", text: $draftCollection)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Collection")
                if !normalizedCollectionSuggestions.isEmpty {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 6) {
                            ForEach(normalizedCollectionSuggestions, id: \.self) { suggestion in
                                Button(suggestion) { draftCollection = suggestion }
                                    .buttonStyle(.plain)
                                    .font(.caption2)
                                    .padding(.horizontal, 8)
                                    .padding(.vertical, 3)
                                    .background(
                                        trimmedDraftCollection == suggestion ? Color.accentColor.opacity(0.24) : Color.secondary.opacity(0.14),
                                        in: Capsule()
                                    )
                                    .foregroundStyle(trimmedDraftCollection == suggestion ? Color.accentColor : .primary)
                            }
                        }
                    }
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Notes")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                TextEditor(text: $draftNotes)
                    .font(.body)
                    .frame(minHeight: 72)
                    .scrollContentBackground(.hidden)
                    .padding(4)
                    .background(.background.opacity(0.6), in: RoundedRectangle(cornerRadius: 6))
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary, lineWidth: 1))
                    .accessibilityLabel("Notes")
            }

            HStack {
                Spacer()
                Button("Revert") { resetDrafts() }
                    .disabled(!isDirty(item))
                Button("Save") { save(item) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!isDirty(item))
            }
        }
    }

    private func uploadLinkSection(_ link: String, item: ClipItem) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Upload link")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack(spacing: 6) {
                Text(link)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .textSelection(.enabled)
                Spacer(minLength: 0)
                Button { onCopyUploadcareLink(item) } label: {
                    Image(systemName: "link")
                }
                .buttonStyle(.borderless)
                .help("Copy link")
                .accessibilityLabel("Copy upload link")
                if let url = URL(string: link) {
                    Link(destination: url) {
                        Image(systemName: "arrow.up.right.square")
                    }
                    .help("Open link")
                    .accessibilityLabel("Open upload link")
                }
            }
        }
    }

    private func fileFacts(for item: ClipItem) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("File")
                .font(.caption)
                .foregroundStyle(.secondary)
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 4) {
                factRow("Type", item.typeLabel)
                factRow("Captured", Self.dateFormatter.string(from: item.date))
                factRow("Size", item.formattedSize)
                if let pixelSize {
                    factRow("Dimensions", "\(Int(pixelSize.width)) × \(Int(pixelSize.height))")
                }
                if let duration, duration > 0 {
                    factRow("Duration", Self.durationFormatter.string(from: duration) ?? "")
                }
                GridRow(alignment: .top) {
                    Text("Location")
                        .foregroundStyle(.secondary)
                    Text(item.url.path)
                        .textSelection(.enabled)
                        .lineLimit(3)
                        .truncationMode(.middle)
                }
            }
            .font(.caption)
        }
    }

    private func factRow(_ label: String, _ value: String) -> some View {
        GridRow {
            Text(label)
                .foregroundStyle(.secondary)
            Text(value)
                .textSelection(.enabled)
        }
    }

    private func inspectorActionButton(
        _ title: String,
        systemImage: String,
        prominent: Bool = false,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Image(systemName: systemImage)
                    .font(.callout.weight(.semibold))
                Text(title)
                    .font(.caption2)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
        }
        .buttonStyle(.bordered)
        .tint(prominent ? Color.accentColor : nil)
        .controlSize(.small)
        .help(title)
    }

    // MARK: Drafts

    private func resetDrafts() {
        guard let item else {
            draftTitle = ""
            draftTagList = []
            draftNotes = ""
            draftCollection = ""
            draftTagInput = ""
            return
        }
        draftTitle = viewModel.displayName(for: item)
        draftTagList = viewModel.tags(for: item)
        draftNotes = viewModel.note(for: item)
        draftCollection = viewModel.collection(for: item)
        draftTagInput = ""
    }

    private func isDirty(_ item: ClipItem) -> Bool {
        draftTitle.trimmingCharacters(in: .whitespacesAndNewlines) != viewModel.displayName(for: item)
            || draftTagList != viewModel.tags(for: item)
            || draftNotes != viewModel.note(for: item)
            || trimmedDraftCollection != viewModel.collection(for: item)
    }

    private func save(_ item: ClipItem) {
        let name = draftTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        // Typing the file name back means "no custom name".
        viewModel.setDisplayName(item, name: name == item.url.lastPathComponent ? "" : name)
        viewModel.setTags(item, tags: draftTagList)
        viewModel.setNotes(item, notes: draftNotes)
        viewModel.setCollection(item, collection: trimmedDraftCollection)
        resetDrafts()
    }

    private var trimmedDraftCollection: String {
        draftCollection.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func addDraftTag(_ rawTag: String) {
        let normalized = rawTag.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !normalized.isEmpty else { return }
        if !draftTagList.contains(normalized) {
            draftTagList.append(normalized)
        }
    }

    private func toggleDraftTag(_ tag: String) {
        if draftTagList.contains(tag) {
            draftTagList.removeAll { $0 == tag }
        } else {
            draftTagList.append(tag)
        }
    }

    private var normalizedTagSuggestions: [String] {
        viewModel.availableTags
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }
            .filter { !$0.isEmpty }
            .reduce(into: [String]()) { acc, tag in
                if !acc.contains(tag) { acc.append(tag) }
            }
            .sorted()
    }

    private var normalizedCollectionSuggestions: [String] {
        viewModel.availableCollections
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .reduce(into: [String]()) { acc, value in
                if !acc.contains(value) { acc.append(value) }
            }
            .sorted()
    }

    // MARK: Preview + file facts

    @MainActor
    private func loadPreview() async {
        player?.pause()
        player = nil
        previewImage = nil
        pixelSize = nil
        duration = nil

        guard let item else { return }
        let url = item.url

        switch item.type {
        case .video:
            let asset = AVURLAsset(url: url)
            let newPlayer = AVPlayer(playerItem: AVPlayerItem(asset: asset))
            newPlayer.actionAtItemEnd = .pause
            player = newPlayer
            if let loadedDuration = try? await asset.load(.duration) {
                duration = CMTimeGetSeconds(loadedDuration)
            }
            if let track = (try? await asset.loadTracks(withMediaType: .video))?.first,
               let size = try? await track.load(.naturalSize),
               let transform = try? await track.load(.preferredTransform) {
                let transformed = size.applying(transform)
                pixelSize = CGSize(width: abs(transformed.width), height: abs(transformed.height))
            }
        case .screenshot, .gif:
            let result = await Task.detached(priority: .userInitiated) { () -> (NSImage?, CGSize?) in
                guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return (nil, nil) }
                var size: CGSize?
                if let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
                   let width = properties[kCGImagePropertyPixelWidth] as? CGFloat,
                   let height = properties[kCGImagePropertyPixelHeight] as? CGFloat {
                    size = CGSize(width: width, height: height)
                }
                let options: [CFString: Any] = [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 1200,
                ]
                guard let cgImage = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else {
                    return (nil, size)
                }
                return (NSImage(cgImage: cgImage, size: NSSize(width: cgImage.width, height: cgImage.height)), size)
            }.value
            guard !Task.isCancelled, self.item?.id == item.id else { return }
            previewImage = result.0
            pixelSize = result.1
        }
    }
}

/// Compact "label above field" layout used by the inspector's name field.
private struct InspectorFieldLabeledContentStyle: LabeledContentStyle {
    func makeBody(configuration: Configuration) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            configuration.label
                .font(.caption)
                .foregroundStyle(.secondary)
            configuration.content
        }
    }
}

private extension LabeledContentStyle where Self == InspectorFieldLabeledContentStyle {
    static var inspectorField: InspectorFieldLabeledContentStyle { InspectorFieldLabeledContentStyle() }
}

private struct ClipCollectionPopover: View {
    let initialCollection: String
    let suggestions: [String]
    let onSave: (String) -> Void
    let onCancel: () -> Void

    @State private var draft = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Set Collection")
                .font(.headline)
            TextField("Collection name", text: $draft)
                .textFieldStyle(.roundedBorder)

            if !suggestions.isEmpty {
                Text("Existing")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(suggestions, id: \.self) { suggestion in
                            Button(suggestion) {
                                draft = suggestion
                            }
                            .buttonStyle(.plain)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                }
                .frame(height: 80)
            }

            HStack {
                Spacer()
                Button("Clear") { onSave("") }
                Button("Cancel") { onCancel() }
                Button("Save") { onSave(draft) }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(14)
        .frame(width: 320)
        .onAppear {
            draft = initialCollection
        }
    }
}

private struct ClipManagerUploadcareSettingsView: View {
    @ObservedObject private var settings = CaptureSettings.shared
    @Environment(\.dismiss) private var dismiss
    let onRunArchiveNow: () -> Void

    @State private var showSecretKey = false
    @State private var publicKey = ""
    @State private var secretKey = ""

    private let refreshIntervals = [0, 15, 30, 60, 120, 300]

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Clips Manager Settings")
                    .font(.title3.weight(.semibold))
                Text("Configure uploads and Clips Manager behavior.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 16)
            .padding(.top, 14)
            .padding(.bottom, 6)

            Form {
                Section("Uploads") {
                    Toggle("Enable Uploadcare uploads", isOn: $settings.uploadcareEnabled)

                    if settings.uploadcareEnabled {
                        TextField("Uploadcare public API key", text: $publicKey)
                        HStack(spacing: 8) {
                            Group {
                                if showSecretKey {
                                    TextField("Uploadcare secret API key", text: $secretKey)
                                } else {
                                    SecureField("Uploadcare secret API key", text: $secretKey)
                                }
                            }

                            Button(showSecretKey ? "Hide" : "Show") {
                                showSecretKey.toggle()
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                        }
                        Link("Create Uploadcare account / find API keys", destination: URL(string: "https://app.uploadcare.com/projects/-/api-keys/")!)
                            .font(.caption)
                        Text("TinyClips does not ship with Uploadcare credentials or manage your account.")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                        Text("Your secret key is used for signed uploads and REST API URL resolution.")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                        Text("Uploadcare API keys are stored in your macOS Keychain.")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                    }

                    Toggle("Auto-upload new captures to Uploadcare", isOn: $settings.clipsManagerAutoUploadAfterSave)
                        .disabled(!settings.uploadcareEnabled)
                    Toggle("Automatically copy uploaded link to clipboard", isOn: $settings.clipsManagerAutoCopyUploadLink)
                        .disabled(!settings.clipsManagerAutoUploadAfterSave || !settings.uploadcareEnabled)
                }

                Section("Display") {
                    Toggle("Show auto-tags in clips", isOn: $settings.clipsManagerShowAutoTags)
                    Toggle("Show notes preview in clips", isOn: $settings.clipsManagerShowNotesPreview)
                    Toggle("Show quick action buttons in list view", isOn: $settings.clipsManagerShowQuickActions)
                    Toggle("Show upload status in clips", isOn: $settings.clipsManagerShowUploadStatus)
                    Toggle("Use compact list density", isOn: $settings.clipsManagerCompactListDensity)
                }

                Section("Behavior") {
                    Toggle("Always confirm delete", isOn: $settings.clipsManagerConfirmDelete)
                    Toggle("Row tap selects in selection mode", isOn: $settings.clipsManagerSelectionRowTapSelects)
                    Toggle("Ignore non-TinyClips files", isOn: $settings.clipsManagerIgnoreNonTinyClipsFiles)
                }

                Section("Defaults") {
                    Toggle("Remember last used manager settings", isOn: $settings.clipsManagerRememberLastState)

                    Picker("Default view", selection: $settings.clipsManagerDefaultViewMode) {
                        Text("Grid").tag("grid")
                        Text("List").tag("list")
                    }
                    .disabled(settings.clipsManagerRememberLastState)

                    Picker("Default sort", selection: $settings.clipsManagerDefaultSortOption) {
                        ForEach(ClipsViewModel.SortOption.allCases, id: \.self) { option in
                            Text(option.rawValue).tag(option.rawValue)
                        }
                    }
                    .disabled(settings.clipsManagerRememberLastState)

                    Picker("Default type filter", selection: $settings.clipsManagerDefaultFilterType) {
                        ForEach(ClipsViewModel.FilterType.allCases, id: \.self) { option in
                            Text(option.rawValue).tag(option.rawValue)
                        }
                    }
                    .disabled(settings.clipsManagerRememberLastState)

                    Picker("Default date filter", selection: $settings.clipsManagerDefaultDateFilter) {
                        ForEach(ClipsViewModel.DateFilter.allCases, id: \.self) { option in
                            Text(option.rawValue).tag(option.rawValue)
                        }
                    }
                    .disabled(settings.clipsManagerRememberLastState)
                }

                Section("Automation") {
                    Picker("Auto-refresh interval", selection: $settings.clipsManagerAutoRefreshSeconds) {
                        ForEach(refreshIntervals, id: \.self) { seconds in
                            Text(refreshIntervalLabel(seconds)).tag(seconds)
                        }
                    }

                    Toggle("Archive old clips automatically", isOn: $settings.clipsManagerArchiveOldClips)
                    if settings.clipsManagerArchiveOldClips {
                        Stepper(value: $settings.clipsManagerArchiveAfterDays, in: 1...365) {
                            Text("Archive clips older than \(settings.clipsManagerArchiveAfterDays) day(s)")
                        }
                        Button("Archive now") {
                            onRunArchiveNow()
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                    }
                }
            }
            .formStyle(.grouped)
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            Divider()

            HStack {
                Spacer()
                Button("Done") {
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(.bar)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear {
            if settings.uploadcareEnabled {
                let credentials = UploadcareCredentialsStore.shared.credentials()
                publicKey = credentials.publicKey
                secretKey = credentials.secretKey
            }
        }
        .onChange(of: settings.uploadcareEnabled) { _, enabled in
            guard enabled else { return }
            let credentials = UploadcareCredentialsStore.shared.credentials()
            publicKey = credentials.publicKey
            secretKey = credentials.secretKey
        }
        .onChange(of: publicKey) { _, updated in
            UploadcareCredentialsStore.shared.setPublicKey(updated)
        }
        .onChange(of: secretKey) { _, updated in
            UploadcareCredentialsStore.shared.setSecretKey(updated)
        }
    }

    private func refreshIntervalLabel(_ seconds: Int) -> String {
        if seconds == 0 { return "Off" }
        return "\(seconds) seconds"
    }

}

// MARK: - Pro Upsell View

#if APPSTORE
private struct ProUpsellView: View {
    var body: some View {
        ProSubscriptionView()
    }
}
#endif