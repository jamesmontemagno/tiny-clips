import UserNotifications
import XCTest
@testable import TinyClips

final class NotificationPermissionTests: XCTestCase {
    func testNotRequestedOffersPermissionRequest() {
        let state = NotificationPermissionState(authorizationStatus: .notDetermined, alertsEnabled: false)
        XCTAssertTrue(state.canRequest)
        XCTAssertEqual(state.status, "notDetermined")
        XCTAssertEqual(state.summary, "Not requested")
    }

    func testDeniedRequiresSystemSettings() {
        let state = NotificationPermissionState(authorizationStatus: .denied, alertsEnabled: false)
        XCTAssertFalse(state.canRequest)
        XCTAssertEqual(state.status, "denied")
        XCTAssertEqual(state.summary, "Disabled in macOS")
    }

    func testAuthorizedReflectsAlertSettingsWithoutRequestingAgain() {
        for status: UNAuthorizationStatus in [.authorized, .ephemeral] {
            let enabled = NotificationPermissionState(authorizationStatus: status, alertsEnabled: true)
            let quiet = NotificationPermissionState(authorizationStatus: status, alertsEnabled: false)
            XCTAssertFalse(enabled.canRequest)
            XCTAssertFalse(quiet.canRequest)
            XCTAssertEqual(enabled.summary, "Enabled")
            XCTAssertEqual(quiet.summary, "Enabled without alerts")
        }
    }

    func testProvisionalPermissionDoesNotClaimFullAlerts() {
        let state = NotificationPermissionState(authorizationStatus: .provisional, alertsEnabled: true)
        XCTAssertFalse(state.canRequest)
        XCTAssertEqual(state.status, "provisional")
        XCTAssertEqual(state.summary, "Delivered quietly")
    }

    func testUnknownPermissionDoesNotOfferRequest() throws {
        let status = try XCTUnwrap(UNAuthorizationStatus(rawValue: 999))
        let state = NotificationPermissionState(authorizationStatus: status, alertsEnabled: false)
        XCTAssertFalse(state.canRequest)
        XCTAssertEqual(state.status, "unknown")
        XCTAssertEqual(state.summary, "Unknown")
    }
}
