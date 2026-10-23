import XCTest
import Foundation
@testable import WeChatBridgeApp
import WeChatBridgeCore

final class ShareEntryAvailabilityTests: XCTestCase {
    @MainActor
    func testMissingAppsCannotEnableAndDoNotStartAnElection() {
        let probe = ShareEntryProbe(isAppInstalled: { _ in false }, customTargets: { [] })
        for action in ShareAction.allCases where action.targetBundleIdentifier != nil {
            probe.setEnabled(true, for: action)
            XCTAssertNotNil(probe.enablingFailure)
            XCTAssertFalse(probe.isOn(action))
            XCTAssertFalse(probe.isSwitching(action))
        }
    }

    @MainActor
    func testInstalledAppIsCheckedAgainAfterRemoval() {
        var installed = true
        let probe = ShareEntryProbe(isAppInstalled: { _ in installed })
        XCTAssertNil(probe.installationFailure(for: .obsidian))
        installed = false
        XCTAssertNotNil(probe.installationFailure(for: .obsidian))
    }

    @MainActor
    func testCustomEntryNeedsAnInstalledTarget() {
        var targets: [ForwardTarget] = []
        let probe = ShareEntryProbe(isAppInstalled: { $0 == "installed.app" }, customTargets: { targets })
        XCTAssertNotNil(probe.installationFailure(for: .custom))
        targets = [ForwardTarget(bundleIdentifier: "missing.app", displayName: "Missing", addedAt: Date())]
        XCTAssertNotNil(probe.installationFailure(for: .custom))
        targets.append(ForwardTarget(bundleIdentifier: "installed.app", displayName: "Installed", addedAt: Date()))
        XCTAssertNil(probe.installationFailure(for: .custom))
    }

    @MainActor
    func testLocalEntriesDoNotRequireAnApp() {
        let probe = ShareEntryProbe(isAppInstalled: { _ in XCTFail("Unexpected app check"); return false })
        for action in [ShareAction.folder, .collect, .clipboard] {
            XCTAssertNil(probe.installationFailure(for: action))
        }
    }
}
