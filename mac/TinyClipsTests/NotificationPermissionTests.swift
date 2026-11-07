import UserNotifications
import XCTest
@testable import TinyClips

final class NotificationPermissionTests: XCTestCase {
    func testNotRequestedOffersPermissionRequest() {
        let state = NotificationPermissionState(
            authorizationStatus: .notDetermined,
            alertSetting: .disabled,
            alertStyle: .none
        )
        XCTAssertTrue(state.canRequest)
        XCTAssertEqual(state.status, "notDetermined")
        XCTAssertEqual(state.summary, "Not requested")
    }

    func testDeniedRequiresSystemSettings() {
        let state = NotificationPermissionState(
            authorizationStatus: .denied,
            alertSetting: .disabled,
            alertStyle: .none
        )
        XCTAssertFalse(state.canRequest)
        XCTAssertEqual(state.status, "denied")
        XCTAssertEqual(state.summary, "Disabled in macOS")
    }

    func testAuthorizedReflectsAlertSettingsWithoutRequestingAgain() {
        let enabled = NotificationPermissionState(
            authorizationStatus: .authorized,
            alertSetting: .enabled,
            alertStyle: .banner
        )
        let disabled = NotificationPermissionState(
            authorizationStatus: .authorized,
            alertSetting: .disabled,
            alertStyle: .none
        )
        let bannersDisabled = NotificationPermissionState(
            authorizationStatus: .authorized,
            alertSetting: .enabled,
            alertStyle: .none
        )

        XCTAssertFalse(enabled.canRequest)
        XCTAssertTrue(enabled.alertsEnabled)
        XCTAssertEqual(enabled.summary, "Enabled")
        XCTAssertFalse(disabled.alertsEnabled)
        XCTAssertEqual(disabled.summary, "Enabled without alerts")
        XCTAssertFalse(bannersDisabled.alertsEnabled)
        XCTAssertEqual(bannersDisabled.summary, "Enabled without banners")
    }

    func testProvisionalPermissionDoesNotClaimFullAlerts() {
        let state = NotificationPermissionState(
            authorizationStatus: .provisional,
            alertSetting: .enabled,
            alertStyle: .banner
        )
        XCTAssertFalse(state.canRequest)
        XCTAssertFalse(state.alertsEnabled)
        XCTAssertEqual(state.status, "provisional")
        XCTAssertEqual(state.summary, "Delivered quietly")
    }

    func testUnknownPermissionDoesNotOfferRequest() throws {
        let status = try XCTUnwrap(UNAuthorizationStatus(rawValue: 999))
        let state = NotificationPermissionState(
            authorizationStatus: status,
            alertSetting: .disabled,
            alertStyle: .none
        )
        XCTAssertFalse(state.canRequest)
        XCTAssertEqual(state.status, "unknown")
        XCTAssertEqual(state.summary, "Unknown")
    }
}
