import AppKit
import UserNotifications
import CryptoKit

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
    private var activeSecurityScopedDirectoryURL: URL?
    private let bookmarkQueue = DispatchQueue(label: "com.tinyclips.save-service.bookmark")
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
#if APPSTORE
        let directoryURL = outputDirectoryURL(for: type)

        try? FileManager.default.createDirectory(
            at: directoryURL,
            withIntermediateDirectories: true
        )
#else
        let directory = UserDefaults.standard.string(forKey: "saveDirectory")
            ?? (NSHomeDirectory() + "/Desktop")

        // Ensure directory exists
        try? FileManager.default.createDirectory(
            atPath: directory,
            withIntermediateDirectories: true
        )
#endif

        var filename = generatedFileName(for: type, fileExtension: fileExtension)
        if let stemSuffix,
           !stemSuffix.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let ext = (filename as NSString).pathExtension
            let stem = (filename as NSString).deletingPathExtension
            filename = ext.isEmpty ? "\(stem) \(stemSuffix)" : "\(stem) \(stemSuffix).\(ext)"
        }

#if APPSTORE
        return uniqueURL(in: directoryURL, filename: filename)
#else
        return uniqueURL(in: URL(fileURLWithPath: directory), filename: filename)
#endif
    }

    func outputDirectoryURL(for type: CaptureType) -> URL {
#if APPSTORE
        return appStoreOutputDirectoryURL(for: type)
#else
        let directory = UserDefaults.standard.string(forKey: "saveDirectory")
            ?? (NSHomeDirectory() + "/Desktop")
        return URL(fileURLWithPath: directory, isDirectory: true)
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

#if APPSTORE
    private func appStoreOutputDirectoryURL(for type: CaptureType) -> URL {
        if let customDirectory = customDirectoryURLFromBookmark() {
            return customDirectory
        }
        return defaultDirectoryURL(for: type)
    }

    private func defaultDirectoryURL(for type: CaptureType) -> URL {
        let fallbackBase = URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)
        let baseURL: URL

        switch type {
        case .video:
            baseURL = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask).first ?? fallbackBase
        case .screenshot, .gif:
            baseURL = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first ?? fallbackBase
        }

        return baseURL.appendingPathComponent("TinyClips", isDirectory: true)
    }

    private func customDirectoryURLFromBookmark() -> URL? {
        bookmarkQueue.sync {
            if let activeSecurityScopedDirectoryURL {
                return activeSecurityScopedDirectoryURL
            }

            guard let bookmarkData = UserDefaults.standard.data(forKey: saveDirectoryBookmarkKey), !bookmarkData.isEmpty else {
                return nil
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
                    UserDefaults.standard.set(refreshedBookmark, forKey: saveDirectoryBookmarkKey)
                }

                guard url.startAccessingSecurityScopedResource() else {
                    return nil
                }

                activeSecurityScopedDirectoryURL = url
                return url
            } catch {
                UserDefaults.standard.removeObject(forKey: saveDirectoryBookmarkKey)
                return nil
            }
        }
    }
#endif

    @MainActor
    func handleSavedFile(url: URL, type: CaptureType) {
        let settings = CaptureSettings.shared

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

    private func copyToClipboard(url: URL, type: CaptureType) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()

        switch type {
        case .screenshot:
            if let image = NSImage(contentsOf: url) {
                pasteboard.writeObjects([image])
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
                        self.copyTextToClipboard(result.fileURL.absoluteString)
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
    private func copyTextToClipboard(_ text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
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
