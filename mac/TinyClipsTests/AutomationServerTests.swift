import Darwin
import XCTest
@testable import TinyClips

final class AutomationServerTests: XCTestCase {
    func testDisconnectCancelsActiveRequestWithAnotherRequestQueued() async throws {
        let started = expectation(description: "request started")
        let cancelled = expectation(description: "request cancelled")
        let path = "/tmp/tc-\(UUID().uuidString).sock"
        let server = AutomationServer(path: path) { _ in
            started.fulfill()
            do {
                try await Task.sleep(nanoseconds: 5_000_000_000)
            } catch is CancellationError {
                cancelled.fulfill()
            } catch {
                XCTFail("Unexpected handler error: \(error)")
            }
            return Data("response".utf8)
        }
        try server.start()
        defer { server.stop() }
        let fd = try connect(path)
        var isOpen = true
        defer { if isOpen { close(fd) } }
        try sendLine("first\nsecond\n", to: fd)
        await fulfillment(of: [started], timeout: 2)

        close(fd)
        isOpen = false

        await fulfillment(of: [cancelled], timeout: 2)
    }

    func testWriteHalfClosedClientStillReceivesResponse() async throws {
        let path = "/tmp/tc-\(UUID().uuidString).sock"
        let server = AutomationServer(path: path) { _ in
            try? await Task.sleep(nanoseconds: 100_000_000)
            return Data("response".utf8)
        }
        try server.start()
        defer { server.stop() }
        let fd = try connect(path)
        defer { close(fd) }
        var timeout = timeval(tv_sec: 2, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        try sendLine("request\n", to: fd)
        XCTAssertEqual(shutdown(fd, SHUT_WR), 0)

        var bytes = [UInt8](repeating: 0, count: 32)
        let count = read(fd, &bytes, bytes.count)
        XCTAssertGreaterThan(count, 0)
        if count > 0 {
            XCTAssertEqual(String(decoding: bytes[..<count], as: UTF8.self), "response\n")
        }
    }

    private func connect(_ path: String) throws -> Int32 {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw POSIXError(.ENOTSOCK) }
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        withUnsafeMutableBytes(of: &address.sun_path) { buffer in
            _ = path.utf8CString.withUnsafeBytes { source in
                memcpy(buffer.baseAddress, source.baseAddress, min(source.count, buffer.count))
            }
        }
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard result == 0 else {
            close(fd)
            throw POSIXError(.ECONNREFUSED)
        }
        return fd
    }

    private func sendLine(_ text: String, to fd: Int32) throws {
        let data = Data(text.utf8)
        let count = data.withUnsafeBytes { write(fd, $0.baseAddress, data.count) }
        guard count == data.count else { throw POSIXError(.EIO) }
    }
}
