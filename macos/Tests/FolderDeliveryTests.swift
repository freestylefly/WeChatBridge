// Standalone app-helper tests; no app launch or Package.swift change needed.
// After `mise run build`:
// folder_test_output="$(mktemp -d /tmp/wechatbridge-folder-tests.XXXXXX)"
// folder_test_sdk="$(xcode-select -p)/Platforms/MacOSX.platform/Developer"
// mise exec -- swiftc -swift-version 5 -D FOLDER_DELIVERY_TEST_MAIN -I .build/debug/Modules \
//   -F "$folder_test_sdk/Library/Frameworks" -L "$folder_test_sdk/usr/lib" \
//   -Xlinker -rpath -Xlinker "$folder_test_sdk/Library/Frameworks" \
//   -Xlinker -rpath -Xlinker "$folder_test_sdk/Library/PrivateFrameworks" \
//   -Xlinker -rpath -Xlinker "$folder_test_sdk/usr/lib" \
//   .build/debug/WeChatBridgeCore.build/DisplayName.swift.o .build/debug/WeChatBridgeCore.build/L10n.swift.o \
//   Sources/WeChatBridgeApp/FolderDelivery.swift Tests/FolderDeliveryTests.swift \
//   -o "$folder_test_output/runner"
// "$folder_test_output/runner"
import Foundation
import XCTest

final class FolderDeliveryTests: XCTestCase {
    private var root: URL!
    private var destination: URL!
    private let fm = FileManager.default

    override func setUpWithError() throws {
        root = fm.temporaryDirectory.appendingPathComponent("wechatbridge-folder-tests-" + UUID().uuidString, isDirectory: true)
        destination = root.appendingPathComponent("saved", isDirectory: true)
        try fm.createDirectory(at: destination, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try fm.removeItem(at: root) }

    private func source(_ name: String = "chat.zip", data: Data = Data("ZIP bytes".utf8)) throws -> URL {
        let directory = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: directory, withIntermediateDirectories: false)
        let url = directory.appendingPathComponent(name)
        try data.write(to: url)
        return url
    }

    private func savedNames() throws -> [String] { try fm.contentsOfDirectory(atPath: destination.path).sorted() }

    func testCollisionsPreserveExistingFilesDirectoriesAndDanglingSymlinks() throws {
        let existing = destination.appendingPathComponent("chat.zip")
        try Data("original".utf8).write(to: existing)
        let directory = destination.appendingPathComponent("chat (2).zip", isDirectory: true)
        try fm.createDirectory(at: directory, withIntermediateDirectories: false)
        let child = directory.appendingPathComponent("keep")
        try Data("inside directory".utf8).write(to: child)
        let link = destination.appendingPathComponent("chat (3).zip")
        try fm.createSymbolicLink(at: link, withDestinationURL: root.appendingPathComponent("absent"))
        let a = try source(data: Data("first".utf8))
        let b = try source(data: Data("second".utf8))
        let saved = try FolderDelivery.save([a, b], to: destination, checkCancellation: {})
        XCTAssertEqual(saved.map(\.lastPathComponent), ["chat (4).zip", "chat (5).zip"])
        XCTAssertEqual(try Data(contentsOf: saved[0]), Data("first".utf8))
        XCTAssertEqual(try Data(contentsOf: saved[1]), Data("second".utf8))
        XCTAssertEqual(try Data(contentsOf: existing), Data("original".utf8))
        XCTAssertEqual(try Data(contentsOf: child), Data("inside directory".utf8))
        XCTAssertEqual(try fm.destinationOfSymbolicLink(atPath: link.path), root.appendingPathComponent("absent").path)
        XCTAssertEqual(try Data(contentsOf: a), Data("first".utf8))
        XCTAssertEqual(try savedNames().count, 5)
    }

    func testCancellationBeforeSavingDoesNotCreateAnything() throws {
        let original = try source()
        XCTAssertThrowsError(try FolderDelivery.save([original], to: destination, checkCancellation: { throw CancellationError() })) {
            XCTAssertTrue($0 is CancellationError)
        }
        XCTAssertEqual(try savedNames(), [])
        XCTAssertEqual(try Data(contentsOf: original), Data("ZIP bytes".utf8))
    }

    func testCancellationDuringLargeFileCopyRemovesPartialStaging() throws {
        let bytes = Data(repeating: 0x5a, count: 3 * 1_048_576)
        let original = try source(data: bytes)
        var interrupted = false
        XCTAssertThrowsError(try FolderDelivery.save([original], to: destination, checkCancellation: {
            let staging = try self.fm.contentsOfDirectory(at: self.destination, includingPropertiesForKeys: nil)
                .first { $0.lastPathComponent.hasPrefix(".wechatbridge-forward-") }
            if let staging,
               let part = try self.fm.contentsOfDirectory(at: staging, includingPropertiesForKeys: nil).first,
               let size = try self.fm.attributesOfItem(atPath: part.path)[.size] as? NSNumber,
               size.intValue > 0 {
                XCTAssertLessThan(size.intValue, bytes.count)
                interrupted = true
                throw CancellationError()
            }
        })) { XCTAssertTrue($0 is CancellationError) }
        XCTAssertTrue(interrupted)
        XCTAssertEqual(try savedNames(), [])
        XCTAssertEqual(try Data(contentsOf: original), bytes)
    }

    func testCancellationAfterFirstPublishRollsBackTheWholeDelivery() throws {
        let a = try source("a.zip")
        let b = try source("b.zip")
        var interrupted = false
        XCTAssertThrowsError(try FolderDelivery.save([a, b], to: destination, checkCancellation: {
            if self.fm.fileExists(atPath: self.destination.appendingPathComponent("a.zip").path) {
                interrupted = true
                throw CancellationError()
            }
        })) { XCTAssertTrue($0 is CancellationError) }
        XCTAssertTrue(interrupted)
        XCTAssertEqual(try savedNames(), [])
        XCTAssertTrue(fm.fileExists(atPath: a.path))
        XCTAssertTrue(fm.fileExists(atPath: b.path))
    }

    func testFailedSourceLeavesNoPublishedFiles() throws {
        let a = try source()
        let missing = root.appendingPathComponent("missing.zip")
        XCTAssertThrowsError(try FolderDelivery.save([a, missing], to: destination, checkCancellation: {}))
        XCTAssertEqual(try savedNames(), [])
        XCTAssertEqual(try Data(contentsOf: a), Data("ZIP bytes".utf8))
    }

    func testInvalidDestinationsAndNonRegularSourcesAreRejected() throws {
        let a = try source()
        let remote = try XCTUnwrap(URL(string: "https://example.invalid/exports"))
        for folder in [a, root.appendingPathComponent("missing"), remote] {
            XCTAssertThrowsError(try FolderDelivery.save([a], to: folder, checkCancellation: {}))
        }
        let link = root.appendingPathComponent("source-link.zip")
        try fm.createSymbolicLink(at: link, withDestinationURL: a)
        for item in [root!, remote, link] {
            XCTAssertThrowsError(try FolderDelivery.save([item], to: destination, checkCancellation: {}))
        }
        XCTAssertEqual(try savedNames(), [])
        XCTAssertEqual(try Data(contentsOf: a), Data("ZIP bytes".utf8))
    }

    func testSafeNamesPreserveZIPContentsAndExtensions() throws {
        let names = [String(repeating: "群", count: 75) + ".zip", ".hidden.zip", "group: name\n.zip"]
        let originals = try names.map { try source($0) }
        let saved = try FolderDelivery.save(originals, to: destination, checkCancellation: {})
        XCTAssertEqual(saved.count, names.count)
        for file in saved {
            XCTAssertEqual(file.deletingLastPathComponent().resolvingSymlinksInPath(), destination.resolvingSymlinksInPath())
            XCTAssertFalse(file.lastPathComponent.hasPrefix("."))
            XCTAssertFalse(file.lastPathComponent.contains(":"))
            XCTAssertFalse(file.lastPathComponent.contains("\n"))
            XCTAssertLessThanOrEqual(file.lastPathComponent.utf8.count, 255)
            XCTAssertEqual(file.pathExtension, "zip")
            XCTAssertEqual(try Data(contentsOf: file), Data("ZIP bytes".utf8))
        }
    }

    func testSavingBesideSourceDoesNotReplaceIt() throws {
        let original = destination.appendingPathComponent("chat.zip")
        try Data("keep this".utf8).write(to: original)
        let saved = try FolderDelivery.save([original], to: destination, checkCancellation: {})
        XCTAssertEqual(saved.map(\.lastPathComponent), ["chat (2).zip"])
        XCTAssertEqual(try Data(contentsOf: original), Data("keep this".utf8))
        XCTAssertEqual(try Data(contentsOf: saved[0]), try Data(contentsOf: original))
    }

    func testConcurrentDeliveriesNeverOverwriteEachOther() throws {
        let original = try source()
        let lock = NSLock()
        var results: [URL] = []
        var failures: [Error] = []
        DispatchQueue.concurrentPerform(iterations: 8) { _ in
            do {
                let saved = try FolderDelivery.save([original], to: self.destination, checkCancellation: {})
                lock.lock(); results.append(contentsOf: saved); lock.unlock()
            } catch {
                lock.lock(); failures.append(error); lock.unlock()
            }
        }
        XCTAssertTrue(failures.isEmpty, "\(failures)")
        XCTAssertEqual(results.count, 8)
        XCTAssertEqual(Set(results).count, 8)
        XCTAssertEqual(try savedNames().count, 8)
        for result in results { XCTAssertEqual(try Data(contentsOf: result), Data("ZIP bytes".utf8)) }
    }

    func testRollbackPreservesAnOutputReplacedByAnotherWriter() throws {
        let original = try source()
        let output = destination.appendingPathComponent("chat.zip")
        let moved = root.appendingPathComponent("moved.zip")
        var replaced = false
        XCTAssertThrowsError(try FolderDelivery.save([original], to: destination, checkCancellation: {
            if !replaced, self.fm.fileExists(atPath: output.path) {
                try self.fm.moveItem(at: output, to: moved)
                try Data("another writer".utf8).write(to: output)
                replaced = true
                throw CancellationError()
            }
        })) { XCTAssertTrue($0 is CancellationError) }
        XCTAssertTrue(replaced)
        XCTAssertEqual(try savedNames(), ["chat.zip"])
        XCTAssertEqual(try Data(contentsOf: output), Data("another writer".utf8))
    }

    func testEmptyDeliveryCreatesNoStagingDirectory() throws {
        XCTAssertEqual(try FolderDelivery.save([], to: destination, checkCancellation: {}), [])
        XCTAssertEqual(try savedNames(), [])
    }
}

#if FOLDER_DELIVERY_TEST_MAIN
@main
enum FolderDeliveryTestRunner {
    static func main() {
        let suite = FolderDeliveryTests.defaultTestSuite
        suite.run()
        guard let run = suite.testRun, run.executionCount > 0, run.hasSucceeded else { exit(1) }
    }
}
#endif
