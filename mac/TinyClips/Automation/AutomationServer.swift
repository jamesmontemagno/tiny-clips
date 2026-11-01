import Darwin
import Foundation

/// Where the automation socket lives. See docs/agent-integration.md.
enum AutomationEndpoint {
    static let directFileName = "automation.sock"
    static let sandboxedFileName = "tinyclips-automation.sock"

    static var isSandboxed: Bool {
        ProcessInfo.processInfo.environment["APP_SANDBOX_CONTAINER_ID"] != nil
    }

    static var socketPath: String {
        if isSandboxed {
            return (NSHomeDirectory() as NSString).appendingPathComponent(sandboxedFileName)
        }
        return AutomationStorage.directory.appendingPathComponent(directFileName).path
    }

    /// `sun_path` holds 104 bytes on macOS, including the terminating zero.
    static var maximumPathLength: Int {
        MemoryLayout.size(ofValue: sockaddr_un().sun_path) - 1
    }
}

/// A Unix domain socket server speaking newline-delimited JSON. Each connection is served on its
/// own thread, one request at a time, so responses come back in order. Socket IO never runs on
/// the main thread; `handler` decides where commands execute.
final class AutomationServer {
    typealias Handler = (Data) async -> Data

    static let maximumLineBytes = 1 << 20
    static let maximumConnections = 16

    let path: String
    private let handler: Handler
    private let lock = NSLock()
    private var listenFD: Int32 = -1
    private var acceptSource: DispatchSourceRead?
    private var connectionFDs: Set<Int32> = []
    private let acceptQueue = DispatchQueue(label: "com.tinyclips.automation.accept")

    init(path: String, handler: @escaping Handler) {
        self.path = path
        self.handler = handler
    }

    deinit {
        stop()
    }

    func start() throws {
        guard path.utf8.count <= AutomationEndpoint.maximumPathLength else {
            throw AutomationError(.internal, "The automation socket path is too long: \(path)")
        }
        try FileManager.default.createDirectory(
            atPath: (path as NSString).deletingLastPathComponent,
            withIntermediateDirectories: true
        )
        unlink(path)

        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw Self.posixError("socket") }
        _ = fcntl(fd, F_SETFD, FD_CLOEXEC)

        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        withUnsafeMutableBytes(of: &address.sun_path) { buffer in
            buffer.initializeMemory(as: UInt8.self, repeating: 0)
            _ = path.utf8CString.withUnsafeBytes { source in
                memcpy(buffer.baseAddress, source.baseAddress, min(source.count, buffer.count - 1))
            }
        }
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard bound == 0 else {
            let error = Self.posixError("bind")
            close(fd)
            throw error
        }
        chmod(path, 0o600)
        guard listen(fd, 8) == 0 else {
            let error = Self.posixError("listen")
            close(fd)
            unlink(path)
            throw error
        }
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)

        let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: acceptQueue)
        source.setEventHandler { [weak self] in
            self?.acceptPendingConnections(on: fd)
        }
        source.setCancelHandler {
            close(fd)
        }
        lock.lock()
        listenFD = fd
        acceptSource = source
        lock.unlock()
        source.resume()
    }

    /// Stops listening, closes every connection, and removes the socket file.
    func stop() {
        lock.lock()
        let source = acceptSource
        let hadListener = listenFD >= 0
        acceptSource = nil
        listenFD = -1
        let connections = connectionFDs
        lock.unlock()

        source?.cancel()
        for fd in connections {
            shutdown(fd, SHUT_RDWR)
        }
        if hadListener {
            unlink(path)
        }
    }

    private func acceptPendingConnections(on listenFD: Int32) {
        while true {
            let fd = accept(listenFD, nil, nil)
            if fd < 0 {
                if errno == EINTR { continue }
                return
            }
            _ = fcntl(fd, F_SETFD, FD_CLOEXEC)
            _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) & ~O_NONBLOCK)
            var noSigPipe: Int32 = 1
            setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))

            // Only processes of the same user may connect.
            var uid: uid_t = 0
            var gid: gid_t = 0
            guard getpeereid(fd, &uid, &gid) == 0, uid == getuid() else {
                close(fd)
                continue
            }

            lock.lock()
            let accepted = connectionFDs.count < Self.maximumConnections
            if accepted { connectionFDs.insert(fd) }
            lock.unlock()
            guard accepted else {
                close(fd)
                continue
            }

            let thread = Thread { [weak self] in
                self?.serve(fd)
                close(fd)
            }
            thread.name = "TinyClips automation connection"
            thread.start()
        }
    }

    private func serve(_ fd: Int32) {
        defer {
            lock.lock()
            connectionFDs.remove(fd)
            lock.unlock()
        }
        var buffer = Data()
        var chunk = [UInt8](repeating: 0, count: 64 * 1024)
        while true {
            let count = read(fd, &chunk, chunk.count)
            if count < 0 && errno == EINTR { continue }
            guard count > 0 else { return }
            buffer.append(chunk, count: count)

            while let newline = buffer.firstIndex(of: 0x0A) {
                let line = buffer[buffer.startIndex..<newline]
                buffer.removeSubrange(buffer.startIndex...newline)
                guard line.contains(where: { !($0 == 0x20 || $0 == 0x09 || $0 == 0x0D) }) else { continue }
                var response = run(Data(line))
                response.append(0x0A)
                guard writeAll(response, to: fd) else { return }
            }

            if buffer.count > Self.maximumLineBytes {
                var response = AutomationResponse.failure(
                    id: nil,
                    error: AutomationError(.invalidParams, "Request is larger than 1 MB.")
                )
                response.append(0x0A)
                _ = writeAll(response, to: fd)
                return
            }
        }
    }

    /// Runs the async handler and waits for it on this connection's thread.
    private func run(_ line: Data) -> Data {
        final class Box: @unchecked Sendable { var value = Data() }
        let box = Box()
        let semaphore = DispatchSemaphore(value: 0)
        let handler = self.handler
        Task.detached {
            box.value = await handler(line)
            semaphore.signal()
        }
        semaphore.wait()
        return box.value
    }

    private func writeAll(_ data: Data, to fd: Int32) -> Bool {
        data.withUnsafeBytes { raw -> Bool in
            guard var pointer = raw.baseAddress else { return true }
            var remaining = raw.count
            while remaining > 0 {
                let written = write(fd, pointer, remaining)
                if written < 0 && errno == EINTR { continue }
                guard written > 0 else { return false }
                remaining -= written
                pointer = pointer.advanced(by: written)
            }
            return true
        }
    }

    private static func posixError(_ call: String) -> AutomationError {
        AutomationError(.internal, "\(call) failed: \(String(cString: strerror(errno)))")
    }
}

/// Builds response lines.
enum AutomationResponse {
    static func success(id: Any?, result: [String: Any]) -> Data {
        encode(["id": id ?? NSNull(), "ok": true, "result": result])
    }

    static func failure(id: Any?, error: AutomationError) -> Data {
        encode([
            "id": id ?? NSNull(),
            "ok": false,
            "error": ["code": error.code.rawValue, "message": error.message]
        ])
    }

    private static func encode(_ object: [String: Any]) -> Data {
        if JSONSerialization.isValidJSONObject(object),
           let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes]) {
            return data
        }
        return Data(#"{"id":null,"ok":false,"error":{"code":"internal","message":"Could not encode the response."}}"#.utf8)
    }
}
