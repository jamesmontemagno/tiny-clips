import Foundation
import IOKit.pwr_mgt

final class IdleSleepAssertion {
    typealias CreateAssertion = (UnsafeMutablePointer<IOPMAssertionID>) -> IOReturn
    typealias ReleaseAssertion = (IOPMAssertionID) -> IOReturn

    private let createAssertion: CreateAssertion
    private let releaseAssertion: ReleaseAssertion
    private var assertionID: IOPMAssertionID?

    init(
        createAssertion: @escaping CreateAssertion = { assertionID in
            IOPMAssertionCreateWithName(
                kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
                IOPMAssertionLevel(kIOPMAssertionLevelOn),
                "TinyClips is recording" as CFString,
                assertionID
            )
        },
        releaseAssertion: @escaping ReleaseAssertion = IOPMAssertionRelease
    ) {
        self.createAssertion = createAssertion
        self.releaseAssertion = releaseAssertion
    }

    func begin() throws {
        guard assertionID == nil else { return }

        var newAssertionID = IOPMAssertionID()
        let result = createAssertion(&newAssertionID)
        guard result == kIOReturnSuccess else {
            throw IdleSleepAssertionError.operationFailed(result)
        }

        assertionID = newAssertionID
    }

    func end() throws {
        guard let assertionID else { return }

        let result = releaseAssertion(assertionID)
        guard result == kIOReturnSuccess else {
            throw IdleSleepAssertionError.operationFailed(result)
        }

        self.assertionID = nil
    }

    deinit {
        do {
            try end()
        } catch {
            NSLog("Unable to release TinyClips idle sleep assertion: \(error.localizedDescription)")
        }
    }
}

private enum IdleSleepAssertionError: LocalizedError {
    case operationFailed(IOReturn)

    var errorDescription: String? {
        switch self {
        case let .operationFailed(status):
            return "Power management assertion failed with status \(status)."
        }
    }
}
