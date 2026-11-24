import Foundation
import XCTest
@testable import WeChatBridgeCore

final class CommerceTests: XCTestCase {
    func testSharedGoSwiftContractFixture() throws {
        let file = try XCTUnwrap(Bundle.module.url(forResource: "examples", withExtension: "json", subdirectory: "Fixtures/Commerce"))
        let root = try JSONDecoder().decode(CloudRecord.self, from: Data(contentsOf: file))
        let wallet = try XCTUnwrap(root["wallet"]?.object)
        XCTAssertEqual(wallet.points("balance"), 300)
        XCTAssertEqual(wallet.points("reserved"), 10)
        XCTAssertEqual(wallet.points("available"), wallet.points("balance") - wallet.points("reserved"))
        XCTAssertEqual(root["error"]?.object["error"]?.object.text("code"), "input_mismatch")
    }
    func testOriginRejectsCredentialsRemotePlaintextAndUnexpectedComponents() throws {
        for origin in ["http://example.com", "https://user:password@example.com", "https://example.com/?token=x", "https://example.com/#x", "https://example.com/other"] {
            XCTAssertThrowsError(try CloudOrigin.validate(URL(string: origin)!, allowLoopback: true))
        }
        XCTAssertEqual(try CloudOrigin.validate(URL(string: "http://127.0.0.1:8780")!, allowLoopback: true).host, "127.0.0.1")
        XCTAssertThrowsError(try CloudOrigin.validate(URL(string: "http://127.0.0.1:8780")!, allowLoopback: false))
    }
    func testPointsRemainIntegerAcrossJSONRoundTrip() throws {
        let value: CloudRecord = ["balance": .integer(9_007_199_254_740_993)]
        let roundTrip = try JSONDecoder().decode(CloudRecord.self, from: JSONEncoder().encode(value))
        XCTAssertEqual(roundTrip.points("balance"), 9_007_199_254_740_993)
    }
    func testServerErrorCodeDoesNotBecomeGenericTransportFailure() async throws {
        let client = try CommerceAPI(baseURL: URL(string: "https://service.test")!, token: "device-test") { request in
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer device-test")
            return (Data(#"{"error":{"code":"input_mismatch","retryable":false}}"#.utf8), HTTPURLResponse(url: request.url!, statusCode: 409, httpVersion: nil, headerFields: nil)!)
        }
        do { _ = try await client.request("v1/executions", body: [:]); XCTFail("expected rejection") }
        catch let error as CloudFailure { XCTAssertEqual(error.code, "input_mismatch") }
    }
    func testProtectedBuyoutPinsReleaseAndOrigin() {
        let skill = OfficialSkill(id: "demo-buyout", name: "Demo", summary: "demo", version: "r1", package: nil, supportedAgents: [.chatGPTCodex], tier: .buyout, protection: .l1, releaseID: "release-1", cloudOriginID: "origin-hash")
        let stub = SkillStub.markdown(for: skill, cliPath: "/Applications/Test.app/Contents/MacOS/wcb")
        XCTAssertTrue(stub.contains("--release-id release-1"))
        XCTAssertTrue(stub.contains("--origin-id origin-hash"))
        XCTAssertFalse(stub.contains("正文不保存在本机"))
    }
}
