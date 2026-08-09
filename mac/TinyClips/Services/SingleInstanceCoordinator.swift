import AppKit
import Foundation
import Darwin

// MARK: - Single Instance Coordinator

final class SingleInstanceCoordinator {
    static let shared = SingleInstanceCoordinator()

    enum AcquisitionResult {
        case primary
        case alreadyRunning
        case failure(Error)
    }

    enum Error: LocalizedError {
        case applicationSupportDirectoryUnavailable
        case createApplicationSupportDirectory(Swift.Error)
        case openLockFile(Int32)
        case acquireLock(Int32)

        var errorDescription: String? {
            switch self {
            case .applicationSupportDirectoryUnavailable:
                return "Tiny Clips could not locate its Application Support directory."
            case let .createApplicationSupportDirectory(error):
                return "Tiny Clips could not create its Application Support directory: \(error.localizedDescription)"
            case let .openLockFile(errorCode):
                return "Tiny Clips could not open its single-instance lock file: \(String(cString: strerror(errorCode)))."
            case let .acquireLock(errorCode):
                return "Tiny Clips could not acquire its single-instance lock: \(String(cString: strerror(errorCode)))."
            }
        }
    }

    private let notificationName: Notification.Name
    private var lockFileDescriptor: Int32?
    private var activationObserver: NSObjectProtocol?

    private init() {
        let bundleIdentifier = Bundle.main.bundleIdentifier ?? "com.tinyclips.app"
        notificationName = Notification.Name("\(bundleIdentifier).activateExistingInstance")
    }

    deinit {
        release()
    }

    func acquire() -> AcquisitionResult {
        if lockFileDescriptor != nil {
            return .primary
        }

        guard let applicationSupportDirectory = FileManager.default.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first else {
            return .failure(.applicationSupportDirectoryUnavailable)
        }

        let bundleIdentifier = Bundle.main.bundleIdentifier ?? "com.tinyclips.app"
        let lockDirectory = applicationSupportDirectory
            .appendingPathComponent(bundleIdentifier, isDirectory: true)

        do {
            try FileManager.default.createDirectory(
                at: lockDirectory,
                withIntermediateDirectories: true
            )
        } catch {
            return .failure(.createApplicationSupportDirectory(error))
        }

        let lockURL = lockDirectory.appendingPathComponent("TinyClips.lock", isDirectory: false)
        let fileDescriptor = lockURL.path.withCString {
            open($0, O_RDWR | O_CREAT, mode_t(S_IRUSR | S_IWUSR))
        }
        guard fileDescriptor != -1 else {
            return .failure(.openLockFile(errno))
        }

        guard flock(fileDescriptor, LOCK_EX | LOCK_NB) == 0 else {
            let errorCode = errno
            close(fileDescriptor)

            if errorCode == EWOULDBLOCK || errorCode == EAGAIN {
                postActivationRequest()
                // The first process may have acquired the lock immediately before registering its observer.
                usleep(50_000)
                postActivationRequest()
                return .alreadyRunning
            }

            return .failure(.acquireLock(errorCode))
        }

        lockFileDescriptor = fileDescriptor
        observeActivationRequests()
        return .primary
    }

    func release() {
        if let activationObserver {
            DistributedNotificationCenter.default().removeObserver(activationObserver)
            self.activationObserver = nil
        }

        guard let lockFileDescriptor else { return }
        flock(lockFileDescriptor, LOCK_UN)
        close(lockFileDescriptor)
        self.lockFileDescriptor = nil
    }

    private func observeActivationRequests() {
        guard activationObserver == nil else { return }

        activationObserver = DistributedNotificationCenter.default().addObserver(
            forName: notificationName,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.activateExistingInstance()
        }
    }

    private func postActivationRequest() {
        DistributedNotificationCenter.default().postNotificationName(
            notificationName,
            object: nil,
            userInfo: nil,
            deliverImmediately: true
        )
    }

    private func activateExistingInstance() {
        NSRunningApplication.current.activate(options: [.activateAllWindows])

        guard let window = NSApp.keyWindow ?? NSApp.windows.first(where: { $0.isVisible && $0.canBecomeKey }) else {
            return
        }

        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }
}
