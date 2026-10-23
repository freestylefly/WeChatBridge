import WeChatBridgeCore
import Foundation
import XCTest

final class RepresentationTests: XCTestCase {
    func testPrefersZipOverTheFileURLTheSameProviderAlsoOffers() {
        // Measured shape of a Finder / NSSharingService provider for a .zip.
        let choice = Representation.choose(from: [
            "public.zip-archive", "public.file-url", "public.url",
        ])
        XCTAssertEqual(choice, Representation(typeIdentifier: "public.zip-archive", strategy: .fileRepresentation))
    }

    func testFallsBackToAnyArchiveType() {
        let choice = Representation.choose(from: ["org.gnu.gnu-tar-archive", "public.file-url"])
        XCTAssertEqual(choice?.typeIdentifier, "org.gnu.gnu-tar-archive")
        XCTAssertEqual(choice?.strategy, .fileRepresentation)
    }

    func testFallsBackToAConcreteDataType() {
        let choice = Representation.choose(from: ["com.adobe.pdf", "public.file-url"])
        XCTAssertEqual(choice, Representation(typeIdentifier: "com.adobe.pdf", strategy: .fileRepresentation))
    }

    func testUsesTheURLStrategyOnlyWhenNothingElseIsOffered() {
        let choice = Representation.choose(from: ["public.file-url", "public.url"])
        XCTAssertEqual(choice, Representation(typeIdentifier: "public.file-url", strategy: .fileURL))
    }

    func testNeverAsksForAFileRepresentationOfAURLType() {
        // public.url conforms to public.data. Asking for its file
        // representation yields a file *containing the URL*, not the file it
        // points at, which would silently stage a 40-byte alias.
        let choice = Representation.choose(from: ["public.url"])
        XCTAssertNil(choice)
    }

    func testRejectsWhatItCannotSaveAsAFile() {
        XCTAssertNil(Representation.choose(from: ["public.plain-text-not-a-real-uti"]))
        XCTAssertNil(Representation.choose(from: []))
    }

    func testDisplayNamePrefersTheProviderNameOverTheGenericTemporaryFile() {
        let name = Representation.displayName(
            preferredName: "聊天记录 测试.zip",
            temporaryURL: URL(fileURLWithPath: "/tmp/x/Zip归档.zip"),
            typeIdentifier: "public.zip-archive"
        )
        XCTAssertEqual(name, "聊天记录 测试.zip")
    }

    func testDisplayNameBorrowsTheTemporaryFileExtensionWhenTheNameHasNone() {
        let name = Representation.displayName(
            preferredName: "聊天记录",
            temporaryURL: URL(fileURLWithPath: "/tmp/x/Zip归档.zip"),
            typeIdentifier: "public.zip-archive"
        )
        XCTAssertEqual(name, "聊天记录.zip")
    }

    func testDisplayNameFallsBackToTheTemporaryFileWhenTheProviderNamedNothing() {
        let name = Representation.displayName(
            preferredName: nil,
            temporaryURL: URL(fileURLWithPath: "/tmp/x/Zip归档.zip"),
            typeIdentifier: "public.zip-archive"
        )
        XCTAssertEqual(name, "Zip归档.zip")
    }
}
