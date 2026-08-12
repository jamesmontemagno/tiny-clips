import AppKit
import Combine
import UserNotifications
import CryptoKit

struct RecentCaptureItem: Identifiable, Codable {
    let path: String
    let type: CaptureType
    let capturedAt: Date

    var id: String { path }
    var url: URL { URL(fileURLWithPath: path) }

    private enum CodingKeys: String, CodingKey {
        case path
        case type
        case capturedAt
    }

    init(path: String, type: CaptureType, capturedAt: Date = Date()) {
        self.path = path
        self.type = type
        self.capturedAt = capturedAt
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        path = try values.decode(String.self, forKey: .path)
        capturedAt = try values.decode(Date.self, forKey: .capturedAt)
        let rawType = try values.decode(String.self, forKey: .type)
        guard let decodedType = CaptureType(rawValue: rawType) else {
            throw DecodingError.dataCorruptedError(forKey: .type, in: values, debugDescription: "Unknown capture type")
        }
        type = decodedType
    }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(path, forKey: .path)
        try values.encode(type.rawValue, forKey: .type)
        try values.encode(capturedAt, forKey: .capturedAt)
    }
}

@MainActor
final class RecentCaptureStore: ObservableObject {
    static let shared = RecentCaptureStore()

    @Published private(set) var items: [RecentCaptureItem] = []

    private let defaultsKey = "recentCapturesV1"

    private init() {
        if let data = UserDefaults.standard.data(forKey: defaultsKey),
           let decoded = try? JSONDecoder().decode([RecentCaptureItem].self, from: data) {
            items = Array(decoded.prefix(10))
        }
        pruneMissing()
    }

    func record(url: URL, type: CaptureType) {
        guard (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true else {
            return
        }
        let path = url.standardizedFileURL.path
        items.removeAll { $0.path == path }
        items.insert(RecentCaptureItem(path: path, type: type), at: 0)
        items = Array(items.filter { FileManager.default.fileExists(atPath: $0.path) }.prefix(10))
        persist()
    }

    func remove(_ item: RecentCaptureItem) {
        items.removeAll { $0.path == item.path }
        persist()
    }

    func pruneMissing() {
        let existing = items.filter { FileManager.default.fileExists(atPath: $0.path) }
        guard existing.count != items.count else { return }
        items = existing
        persist()
    }

    private func persist() {
        if let data = try? JSONEncoder().encode(items) {
            UserDefaults.standard.set(data, forKey: defaultsKey)
        }
    }
}

@MainActor
final class AccessibilityAnnouncementService {
    static let shared = AccessibilityAnnouncementService()

    private init() {}

    func announceCaptureStart(for type: CaptureType, countdownCompleted: Bool) {
        let message: String

        switch type {
        case .screenshot:
            message = countdownCompleted ? "Countdown complete. Taking screenshot." : "Taking screenshot."
        case .video:
            message = countdownCompleted ? "Countdown complete. Starting video recording." : "Starting video recording."
        case .gif:
            message = countdownCompleted ? "Countdown complete. Starting GIF recording." : "Starting GIF recording."
        }

        announce(message, priority: .high)
    }

    func announceRecordingStopped(for type: CaptureType) {
        announce("\(type.label) recording stopped.", priority: .high)
    }

    func announceSaveSuccess(for type: CaptureType, url: URL) {
        announce("\(type.label) saved as \(url.lastPathComponent).", priority: .medium)
    }

    func announceError(_ message: String) {
        announce("Error. \(message) Press OK to dismiss.", priority: .high)
    }

    func announce(_ message: String, priority: NSAccessibilityPriorityLevel) {
        let userInfo: [NSAccessibility.NotificationUserInfoKey: Any] = [
            .announcement: message,
            .priority: priority.rawValue
        ]

        NSAccessibility.post(
            element: announcementElement(),
            notification: .announcementRequested,
            userInfo: userInfo
        )
    }

    private func announcementElement() -> Any {
        if let window = NSApp.mainWindow ?? NSApp.keyWindow ?? NSApp.windows.first {
            return window
        }
        return NSApp as Any
    }
}

class SaveService: NSObject, UNUserNotificationCenterDelegate {
    static let shared = SaveService()
    private let notificationURLKey = "savedFileURL"

    override init() {
        super.init()
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            UNUserNotificationCenter.current().delegate = self
        }
    }

#if APPSTORE
    private let saveDirectoryBookmarkKey = "saveDirectoryBookmark"
    private let screenshotSaveDirectoryBookmarkKey = "screenshotSaveDirectoryBookmark"
    private let videoSaveDirectoryBookmarkKey = "videoSaveDirectoryBookmark"
    private let gifSaveDirectoryBookmarkKey = "gifSaveDirectoryBookmark"
    private var activeSecurityScopedDirectoryURLs: [String: URL] = [:]
    private let bookmarkQueue = DispatchQueue(label: "com.tinyclips.save-service.bookmark")

    func invalidateSaveDirectoryBookmark(for type: CaptureType?) {
        let keys: [String]
        switch type {
        case .screenshot:
            keys = [screenshotSaveDirectoryBookmarkKey]
        case .video:
            keys = [videoSaveDirectoryBookmarkKey]
        case .gif:
            keys = [gifSaveDirectoryBookmarkKey]
        case nil:
            keys = [saveDirectoryBookmarkKey]
        }

        invalidateSaveDirectoryBookmarks(for: keys)
    }

    func invalidateAllSaveDirectoryBookmarks() {
        invalidateSaveDirectoryBookmarks(for: [
            saveDirectoryBookmarkKey,
            screenshotSaveDirectoryBookmarkKey,
            videoSaveDirectoryBookmarkKey,
            gifSaveDirectoryBookmarkKey
        ])
    }

    private func invalidateSaveDirectoryBookmarks(for keys: [String]) {
        bookmarkQueue.sync {
            for key in keys {
                activeSecurityScopedDirectoryURLs.removeValue(forKey: key)?
                    .stopAccessingSecurityScopedResource()
            }
        }
    }
#endif

    func generateURL(for type: CaptureType) -> URL {
        return generateURL(for: type, fileExtension: type.fileExtension)
    }

    func generateURL(for type: CaptureType, stemSuffix: String?) -> URL {
        return generateURL(for: type, fileExtension: type.fileExtension, stemSuffix: stemSuffix)
    }

    func generateURL(for type: CaptureType, fileExtension: String) -> URL {
        generateURL(for: type, fileExtension: fileExtension, stemSuffix: nil)
    }

    func generateURL(for type: CaptureType, fileExtension: String, stemSuffix: String?) -> URL {
        let directoryURL = outputDirectoryURL(for: type)

        try? FileManager.default.createDirectory(
            at: directoryURL,
            withIntermediateDirectories: true
        )

        var filename = generatedFileName(for: type, fileExtension: fileExtension)
        if let stemSuffix,
           !stemSuffix.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let ext = (filename as NSString).pathExtension
            let stem = (filename as NSString).deletingPathExtension
            filename = ext.isEmpty ? "\(stem) \(stemSuffix)" : "\(stem) \(stemSuffix).\(ext)"
        }

        return uniqueURL(in: directoryURL, filename: filename)
    }

    func outputDirectoryURL(for type: CaptureType) -> URL {
        let settings = CaptureSettings.shared
        guard !settings.useDefaultSaveDirectories else {
            return defaultDirectoryURL(for: type)
        }

#if APPSTORE
        return customDirectoryURLFromBookmark(for: type) ?? defaultDirectoryURL(for: type)
#else
        let directory = settings.saveDirectoryPath(for: type)
        return directory.isEmpty
            ? defaultDirectoryURL(for: type)
            : URL(fileURLWithPath: directory, isDirectory: true)
#endif
    }

    func generatedFileName(for type: CaptureType, fileExtension: String, date: Date = Date()) -> String {
        let settings = CaptureSettings.shared
        let rawTemplate = settings.fileNameTemplate.trimmingCharacters(in: .whitespacesAndNewlines)
        let template = rawTemplate.isEmpty ? "TinyClips {date} at {time}" : rawTemplate

        var stem = template
            .replacingOccurrences(of: "{app}", with: "TinyClips")
            .replacingOccurrences(of: "{type}", with: type.label)
            .replacingOccurrences(of: "{date}", with: formatted(date, format: "yyyy-MM-dd"))
            .replacingOccurrences(of: "{time}", with: formatted(date, format: "HH.mm.ss"))
            .replacingOccurrences(of: "{datetime}", with: formatted(date, format: "yyyy-MM-dd_HH.mm.ss"))

        stem = sanitizedFilenameStem(stem, fallbackDate: date)

        let cleanExtension = fileExtension
            .trimmingCharacters(in: CharacterSet(charactersIn: ". "))
            .lowercased()
        return cleanExtension.isEmpty ? stem : "\(stem).\(cleanExtension)"
    }

    func namingPreview(for type: CaptureType = .screenshot) -> String {
        generatedFileName(for: type, fileExtension: type.fileExtension)
    }

    private func formatted(_ date: Date, format: String) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = format
        return formatter.string(from: date)
    }

    private func sanitizedFilenameStem(_ stem: String, fallbackDate: Date) -> String {
        let invalidCharacters = CharacterSet(charactersIn: "/\\:?*\"<>|")
        var cleaned = stem
            .components(separatedBy: invalidCharacters)
            .joined(separator: "-")
            .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
            .trimmingCharacters(in: CharacterSet(charactersIn: " .\n\t"))

        if cleaned.isEmpty {
            cleaned = "TinyClips \(formatted(fallbackDate, format: "yyyy-MM-dd_HH.mm.ss"))"
        }

        return cleaned
    }

    private func uniqueURL(in directoryURL: URL, filename: String) -> URL {
        let initialURL = directoryURL.appendingPathComponent(filename)
        guard FileManager.default.fileExists(atPath: initialURL.path) else {
            return initialURL
        }

        let ext = initialURL.pathExtension
        let stem = initialURL.deletingPathExtension().lastPathComponent
        var suffix = 2

        while true {
            let candidateName = ext.isEmpty ? "\(stem) \(suffix)" : "\(stem) \(suffix).\(ext)"
            let candidateURL = directoryURL.appendingPathComponent(candidateName)
            if !FileManager.default.fileExists(atPath: candidateURL.path) {
                return candidateURL
            }
            suffix += 1
        }
    }

    private func defaultDirectoryURL(for type: CaptureType) -> URL {
        let fallbackBase = URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)
        let baseURL: URL

        switch type {
        case .video, .gif:
            baseURL = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask).first ?? fallbackBase
        case .screenshot:
            baseURL = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first ?? fallbackBase
        }

        return baseURL.appendingPathComponent("TinyClips", isDirectory: true)
    }

#if APPSTORE
    private func customDirectoryURLFromBookmark(for type: CaptureType) -> URL? {
        bookmarkQueue.sync {
            for key in bookmarkKeys(for: type) {
                if let activeURL = activeSecurityScopedDirectoryURLs[key] {
                    return activeURL
                }

                guard let bookmarkData = UserDefaults.standard.data(forKey: key), !bookmarkData.isEmpty else {
                    continue
                }

                do {
                    var isStale = false
                    let url = try URL(
                        resolvingBookmarkData: bookmarkData,
                        options: [.withSecurityScope],
                        relativeTo: nil,
                        bookmarkDataIsStale: &isStale
                    )

                    if isStale,
                       let refreshedBookmark = try? url.bookmarkData(options: [.withSecurityScope], includingResourceValuesForKeys: nil, relativeTo: nil) {
                        UserDefaults.standard.set(refreshedBookmark, forKey: key)
                    }

                    guard url.startAccessingSecurityScopedResource() else {
                        clearBookmark(for: key)
                        continue
                    }

                    activeSecurityScopedDirectoryURLs[key] = url
                    return url
                } catch {
                    clearBookmark(for: key)
                }
            }
            return nil
        }
    }

    private func bookmarkKeys(for type: CaptureType) -> [String] {
        switch type {
        case .screenshot:
            return [screenshotSaveDirectoryBookmarkKey]
        case .video:
            return [videoSaveDirectoryBookmarkKey]
        case .gif:
            return [gifSaveDirectoryBookmarkKey]
        }
    }

    private func clearBookmark(for bookmarkKey: String) {
        UserDefaults.standard.removeObject(forKey: bookmarkKey)
        UserDefaults.standard.removeObject(forKey: displayPathKey(for: bookmarkKey))
    }

    private func displayPathKey(for bookmarkKey: String) -> String {
        switch bookmarkKey {
        case screenshotSaveDirectoryBookmarkKey:
            return "screenshotSaveDirectoryDisplayPath"
        case videoSaveDirectoryBookmarkKey:
            return "videoSaveDirectoryDisplayPath"
        case gifSaveDirectoryBookmarkKey:
            return "gifSaveDirectoryDisplayPath"
        default:
            return "saveDirectoryDisplayPath"
        }
    }
#endif

    @MainActor
    func handleSavedFile(url: URL, type: CaptureType) {
        let settings = CaptureSettings.shared
        RecentCaptureStore.shared.record(url: url, type: type)

#if APPSTORE
        UserDefaults.standard.set(
            UserDefaults.standard.integer(forKey: "appStoreClipCountForReview") + 1,
            forKey: "appStoreClipCountForReview"
        )
#endif

        if settings.shouldCopyToClipboard(for: type) {
            copyToClipboard(url: url, type: type)
        }

        AccessibilityAnnouncementService.shared.announceSaveSuccess(for: type, url: url)

        if settings.showInFinder {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        }

        if settings.showSaveNotifications {
            showNotification(type: type, url: url)
        }

        startAutomaticUploadIfNeeded(for: url)
    }

    @MainActor
    private func copyToClipboard(url: URL, type: CaptureType) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()

        switch type {
        case .screenshot:
            guard let image = NSImage(contentsOf: url),
                  let tiffData = image.tiffRepresentation,
                  let bitmap = NSBitmapImageRep(data: tiffData) else {
                showError("Could not prepare the screenshot for the clipboard.")
                return
            }

            let pngData = (url.pathExtension.lowercased() == "png" ? try? Data(contentsOf: url) : nil)
                ?? bitmap.representation(using: .png, properties: [:])
            guard let pngData else {
                showError("Could not prepare the screenshot for the clipboard.")
                return
            }

            let didWritePNG = pasteboard.setData(pngData, forType: .png)
            let didWriteTIFF = pasteboard.setData(tiffData, forType: .tiff)
            if !didWritePNG || !didWriteTIFF {
                showError("Could not copy the screenshot to the clipboard.")
            }
        case .video, .gif:
            pasteboard.writeObjects([url as NSURL])
        }
    }

    @MainActor
    private func startAutomaticUploadIfNeeded(for url: URL) {
        let settings = CaptureSettings.shared
        guard settings.clipsManagerAutoUploadAfterSave else { return }
        guard settings.uploadcareEnabled else { return }

        let credentials = UploadcareCredentialsStore.shared.credentials()
        let publicKey = credentials.publicKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let secretKey = credentials.secretKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !publicKey.isEmpty, !secretKey.isEmpty else { return }

        let shouldCopyLink = settings.clipsManagerAutoCopyUploadLink

        Task {
            do {
                let result = try await UploadcareService.shared.upload(
                    fileURL: url,
                    publicKey: publicKey,
                    secretKey: secretKey
                )

                await MainActor.run {
                    ClipMetadataStore.shared.upsert(path: url.path) { metadata in
                        metadata.uploadcareURL = result.fileURL.absoluteString
                    }
                    if shouldCopyLink {
                        _ = self.copyTextToClipboard(result.fileURL.absoluteString)
                    }
                }
            } catch {
                await MainActor.run {
                    self.showError("Automatic Uploadcare upload failed: \(error.localizedDescription)")
                }
            }
        }
    }

    @MainActor
    func copyTextToClipboard(_ text: String) -> Bool {
        guard !text.isEmpty else { return false }

        let pasteboard = NSPasteboard.general
        let previousItems = pasteboard.pasteboardItems?.map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) {
                    copy.setData(data, forType: type)
                }
            }
            return copy
        }
        pasteboard.clearContents()
        guard pasteboard.setString(text, forType: .string) else {
            if let previousItems, !previousItems.isEmpty {
                pasteboard.clearContents()
                _ = pasteboard.writeObjects(previousItems)
            }
            return false
        }
        return true
    }

    private func showNotification(type: CaptureType, url: URL) {
        let content = UNMutableNotificationContent()
        content.title = "\(type.label) Saved"
        content.body = url.lastPathComponent
        content.sound = .default
        content.userInfo = [notificationURLKey: url.path]

        let request = UNNotificationRequest(
            identifier: UUID().uuidString,
            content: content,
            trigger: nil
        )

        UNUserNotificationCenter.current().add(request)
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list, .sound])
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        defer { completionHandler() }

        guard let savedFilePath = response.notification.request.content.userInfo[notificationURLKey] as? String else {
            return
        }

        let savedFileURL = URL(fileURLWithPath: savedFilePath)
        DispatchQueue.main.async {
            NSWorkspace.shared.activateFileViewerSelecting([savedFileURL])
        }
    }

    @MainActor
    func showError(_ message: String) {
        AccessibilityAnnouncementService.shared.announceError(message)

        let alert = NSAlert()
        alert.messageText = "TinyClips"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }
}

// MARK: - Uploadcare

struct UploadcareUploadResult {
    let uuid: String
    let fileURL: URL
}

enum UploadcareError: LocalizedError {
    case missingPublicKey
    case missingSecretKey
    case fileTooLarge
    case invalidResponse
    case api(statusCode: Int, message: String)

    var errorDescription: String? {
        switch self {
        case .missingPublicKey:
            return "Uploadcare public API key is missing."
        case .missingSecretKey:
            return "Uploadcare secret API key is missing."
        case .fileTooLarge:
            return "Uploadcare direct upload supports files up to 100 MiB. Please upload a smaller file."
        case .invalidResponse:
            return "Uploadcare returned an invalid response."
        case .api(_, let message):
            return message
        }
    }
}

final class UploadcareService {
    static let shared = UploadcareService()

    private init() {}

    func upload(
        fileURL: URL,
        publicKey: String,
        secretKey: String,
        onProgress: @escaping (Double) -> Void = { _ in }
    ) async throws -> UploadcareUploadResult {
        let key = publicKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else {
            throw UploadcareError.missingPublicKey
        }
        let secret = secretKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !secret.isEmpty else {
            throw UploadcareError.missingSecretKey
        }

        let values = try fileURL.resourceValues(forKeys: [.fileSizeKey])
        let fileSize = values.fileSize ?? 0
        if fileSize > 104_857_600 {
            throw UploadcareError.fileTooLarge
        }

        let boundary = "Boundary-\(UUID().uuidString)"
        var request = URLRequest(url: URL(string: "https://upload.uploadcare.com/base/")!)
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        var body = Data()
        body.appendFormField(named: "UPLOADCARE_PUB_KEY", value: key, boundary: boundary)
        body.appendFormField(named: "UPLOADCARE_STORE", value: "auto", boundary: boundary)
        let expire = Int(Date().timeIntervalSince1970) + 1_800
        let signature = makeSignedUploadSignature(secretKey: secret, expire: expire)
        body.appendFormField(named: "signature", value: signature, boundary: boundary)
        body.appendFormField(named: "expire", value: String(expire), boundary: boundary)

        let fileData = try Data(contentsOf: fileURL)
        body.appendFileField(
            named: "file",
            fileName: fileURL.lastPathComponent,
            mimeType: mimeType(for: fileURL),
            fileData: fileData,
            boundary: boundary
        )
        body.appendString("--\(boundary)--\r\n")

        onProgress(0)
        let delegate = UploadcareUploadProgressDelegate(onProgress: onProgress)
        let session = URLSession(configuration: .ephemeral, delegate: delegate, delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }

        let (data, response): (Data, URLResponse) = try await withCheckedThrowingContinuation { continuation in
            let task = session.uploadTask(with: request, from: body) { data, response, error in
                if let error {
                    continuation.resume(throwing: error)
                    return
                }
                guard let data, let response else {
                    continuation.resume(throwing: UploadcareError.invalidResponse)
                    return
                }
                continuation.resume(returning: (data, response))
            }
            task.resume()
        }
        guard let http = response as? HTTPURLResponse else {
            throw UploadcareError.invalidResponse
        }

        if !(200..<300).contains(http.statusCode) {
            throw UploadcareError.api(
                statusCode: http.statusCode,
                message: uploadcareErrorMessage(from: data, statusCode: http.statusCode)
            )
        }

        struct Response: Decodable { let file: String }
        guard let parsed = try? JSONDecoder().decode(Response.self, from: data),
              !parsed.file.isEmpty else {
            throw UploadcareError.invalidResponse
        }

        let url = try await fetchCanonicalFileURL(uuid: parsed.file, publicKey: key, secretKey: secret)
        onProgress(1)
        return UploadcareUploadResult(uuid: parsed.file, fileURL: url)
    }

    private func mimeType(for url: URL) -> String {
        switch url.pathExtension.lowercased() {
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "gif": return "image/gif"
        case "mp4": return "video/mp4"
        default: return "application/octet-stream"
        }
    }

    private func fetchCanonicalFileURL(uuid: String, publicKey: String, secretKey: String) async throws -> URL {
        struct FileInfoResponse: Decodable {
            let original_file_url: String?
            let url: String?
        }

        for attempt in 0..<20 {
            var request = URLRequest(url: URL(string: "https://api.uploadcare.com/files/\(uuid)/")!)
            request.httpMethod = "GET"
            request.setValue("application/vnd.uploadcare-v0.7+json", forHTTPHeaderField: "Accept")
            request.setValue("Uploadcare.Simple \(publicKey):\(secretKey)", forHTTPHeaderField: "Authorization")

            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw UploadcareError.invalidResponse
            }

            if (200..<300).contains(http.statusCode) {
                if let parsed = try? JSONDecoder().decode(FileInfoResponse.self, from: data) {
                    if let raw = parsed.original_file_url, let url = URL(string: raw), !raw.isEmpty {
                        return url
                    }
                    if let raw = parsed.url, let url = URL(string: raw), !raw.isEmpty {
                        return url
                    }
                }
            } else if http.statusCode != 404 && http.statusCode != 423 {
                throw UploadcareError.api(
                    statusCode: http.statusCode,
                    message: uploadcareErrorMessage(from: data, statusCode: http.statusCode)
                )
            }

            if attempt < 19 {
                try await Task.sleep(nanoseconds: 500_000_000)
            }
        }

        throw UploadcareError.api(
            statusCode: 0,
            message: "Could not resolve your Uploadcare file URL from REST API. Please verify your Uploadcare keys and try again."
        )
    }

    private func makeSignedUploadSignature(secretKey: String, expire: Int) -> String {
        let key = SymmetricKey(data: Data(secretKey.utf8))
        let signature = HMAC<SHA256>.authenticationCode(for: Data(String(expire).utf8), using: key)
        return signature.map { String(format: "%02x", $0) }.joined()
    }

    private func uploadcareErrorMessage(from data: Data, statusCode: Int) -> String {
        if let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            if let message = object["error"] as? String, !message.isEmpty {
                return message
            }
            if let message = object["error_content"] as? String, !message.isEmpty {
                return message
            }
            if let message = object["detail"] as? String, !message.isEmpty {
                return message
            }
        }
        return "Uploadcare upload failed (HTTP \(statusCode))."
    }
}

private final class UploadcareUploadProgressDelegate: NSObject, URLSessionTaskDelegate {
    private let onProgress: (Double) -> Void

    init(onProgress: @escaping (Double) -> Void) {
        self.onProgress = onProgress
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didSendBodyData bytesSent: Int64, totalBytesSent: Int64, totalBytesExpectedToSend: Int64) {
        guard totalBytesExpectedToSend > 0 else { return }
        let progress = min(max(Double(totalBytesSent) / Double(totalBytesExpectedToSend), 0), 1)
        onProgress(progress)
    }
}

private extension Data {
    mutating func appendString(_ string: String) {
        if let data = string.data(using: .utf8) {
            append(data)
        }
    }

    mutating func appendFormField(named name: String, value: String, boundary: String) {
        appendString("--\(boundary)\r\n")
        appendString("Content-Disposition: form-data; name=\"\(name)\"\r\n\r\n")
        appendString("\(value)\r\n")
    }

    mutating func appendFileField(named name: String, fileName: String, mimeType: String, fileData: Data, boundary: String) {
        appendString("--\(boundary)\r\n")
        appendString("Content-Disposition: form-data; name=\"\(name)\"; filename=\"\(fileName)\"\r\n")
        appendString("Content-Type: \(mimeType)\r\n\r\n")
        append(fileData)
        appendString("\r\n")
    }
}
