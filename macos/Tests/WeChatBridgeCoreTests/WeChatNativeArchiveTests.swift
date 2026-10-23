import WeChatBridgeCore
import Foundation
import XCTest

final class WeChatNativeArchiveTests: XCTestCase {
    // Independently generated with Python's zipfile: two repeated multiline
    // messages, a media reference, UTF-8 filenames, and an attachment CRC.
    private let storedZIP = "UEsDBBQAAAgAAMBAJV0fGRySiQAAAIkAAAAQAAAA6IGK5aSp6K6w5b2VLnR4dMK355SyCjIwMjblubQ55pyINeaXpSAwODowNQrkvaDlpb0K56ys5LqM6KGMCgrCt+S5mQoyMDI25bm0OeaciDXml6UgMDg6MDUK5L2g5aW9CuesrOS6jOihjAoKwrfnlLIKMjAyNuW5tDnmnIg15pelIDA4OjA2CmltYWdlcy9waG90by5wbmcKUEsDBBQAAAAAAMBAJV2KfiaRIAAAACAAAAAQAAAAaW1hZ2VzL3Bob3RvLnBuZwABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fUEsBAhQDFAAACAAAwEAlXR8ZHJKJAAAAiQAAABAAAAAAAAAAAAAAAIABAAAAAOiBiuWkqeiusOW9lS50eHRQSwECFAMUAAAAAADAQCVdin4mkSAAAAAgAAAAEAAAAAAAAAAAAAAAgAG3AAAAaW1hZ2VzL3Bob3RvLnBuZ1BLBQYAAAAAAgACAHwAAAAFAQAAAAA="

    func testNativeArchiveStoredWithMedia() throws {
        let data = try XCTUnwrap(Data(base64Encoded: storedZIP))
        XCTAssertEqual(try WeChatNativeArchive.messageCount(data), 3)
    }

    func testNativeArchiveRejectsEmptyFilesAndArchives() throws {
        let fixtures = [
            Data(),
            try XCTUnwrap(Data(base64Encoded: "UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==")),
            try XCTUnwrap(Data(base64Encoded: "UEsDBBQAAAAIAAAAJ10AAAAAAgAAAAAAAAAOAAAAdHJhbnNjcmlwdC50eHQDAFBLAQIUAxQAAAAIAAAAJ10AAAAAAgAAAAAAAAAOAAAAAAAAAAAAAACAAQAAAAB0cmFuc2NyaXB0LnR4dFBLBQYAAAAAAQABADwAAAAuAAAAAAA=")),
        ]
        for data in fixtures {
            XCTAssertThrowsError(try WeChatNativeArchive.messageCount(data)) { error in
                XCTAssertEqual(error as? WeChatReadError, .invalidTranscript)
            }
        }
    }

    func testTranscriptParserKeepsMultilineBodiesAndRejectsUnrecognizedHeaders() throws {
        let body = "·甲\n2026年9月5日 08:05\n第一行\n第二行\n\n·甲\n2026年9月5日 08:06\n再见\n"
        let rows = try WeChatTranscriptRecord.parse(body)
        XCTAssertEqual(rows.map(\.text), ["第一行\n第二行", "再见"])
        XCTAssertThrowsError(try WeChatTranscriptRecord.parse("unrecognized text"))
    }
}
