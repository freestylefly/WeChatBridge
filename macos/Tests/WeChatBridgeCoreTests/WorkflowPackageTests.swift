import CryptoKit
import Foundation
import XCTest
@testable import WeChatBridgeCore

final class WorkflowPackageTests:XCTestCase {
    func testDownloadChecksWholeArchiveDigestAndPermission() async throws {
        let bytes=Data("package fixture".utf8)
        let hash=SHA256.hash(data:bytes).map{String(format:"%02x",$0)}.joined()
        let api=try CommerceAPI(baseURL:URL(string:"https://example.com")!,token:"limited-device",transport:{request in
            XCTAssertEqual(request.url?.path,"/v1/skills/demo-package/releases/release-1/package")
            XCTAssertEqual(request.value(forHTTPHeaderField:"Authorization"),"Bearer limited-device")
            return (bytes,HTTPURLResponse(url:request.url!,statusCode:200,httpVersion:nil,headerFields:["Content-Type":"application/zip","X-Artifact-SHA256":hash])!)
        })
        let file=try await api.downloadPackage(skillID:"demo-package",releaseID:"release-1")
        defer{try? FileManager.default.removeItem(at:file)}
        XCTAssertEqual(try Data(contentsOf:file),bytes)
        XCTAssertEqual((try FileManager.default.attributesOfItem(atPath:file.path)[.posixPermissions] as? NSNumber)?.intValue,0o600)
        let tampered=try CommerceAPI(baseURL:URL(string:"https://example.com")!,token:nil,transport:{request in
            (bytes,HTTPURLResponse(url:request.url!,statusCode:200,httpVersion:nil,headerFields:["Content-Type":"application/zip","X-Artifact-SHA256":String(repeating:"0",count:64)])!)
        })
        do {_ = try await tampered.downloadPackage(skillID:"demo-package",releaseID:"release-1");XCTFail("tampered archive accepted")}
        catch let error as CloudFailure{XCTAssertEqual(error.code,"package_digest_mismatch")}
    }
    func testInstalledCloudPackageSurvivesStubRefreshAndDetectsModeChange() throws {
        let fm=FileManager.default,root=fm.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer{try? fm.removeItem(at:root)}
        let source=root.appendingPathComponent("source")
        try fm.createDirectory(at:source.appendingPathComponent("scripts"),withIntermediateDirectories:true)
        try Data("---\nname: demo-package\ndescription: fixture\nmetadata:\n  version: '1.0.0'\n---\n# Package".utf8).write(to:source.appendingPathComponent("SKILL.md"))
        let script=source.appendingPathComponent("scripts/run.sh")
        try Data("#!/bin/sh\nprintf fixture".utf8).write(to:script)
        try fm.setAttributes([.posixPermissions:0o755],ofItemAtPath:script.path)
        let store=SkillStore(root:root.appendingPathComponent("library"),configDirectory:root.appendingPathComponent("config"))
        let first=try store.importCloudPackage(source:source,id:"demo-package",version:"1.0.0",originID:"origin-one",releaseID:"release-1")
        let again=try store.importCloudPackage(source:source,id:"demo-package",version:"1.0.0",originID:"origin-one",releaseID:"release-1")
        XCTAssertEqual(first.digest,again.digest)
        let skill=OfficialSkill(id:"demo-package",name:"Package",summary:"fixture",version:"1.0.0",package:nil,supportedAgents:[.chatGPTCodex],tier:.buyout,protection:.l1,releaseID:"release-1",cloudOriginID:"origin-one")
        let report=try store.syncProtected(catalog:OfficialSkillCatalog(skills:[skill]),cliPath:"/tmp/wcb")
        XCTAssertEqual(report.updated,0)
        XCTAssertTrue(fm.fileExists(atPath:store.skillDirectory("demo-package").appendingPathComponent("scripts/run.sh").path))
        XCTAssertEqual(store.state("demo-package"),.ready)
        try fm.setAttributes([.posixPermissions:0o644],ofItemAtPath:store.skillDirectory("demo-package").appendingPathComponent("scripts/run.sh").path)
        XCTAssertEqual(store.state("demo-package"),.conflict)
    }
    func testZipReaderPreservesUnixExecuteAndRejectsSymbolicLink() throws {
        let fm=FileManager.default,root=fm.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer{try? fm.removeItem(at:root)}
        let source=root.appendingPathComponent("source")
        try fm.createDirectory(at:source,withIntermediateDirectories:true)
        try Data("---\nname: demo-package\ndescription: fixture\n---\n# Package".utf8).write(to:source.appendingPathComponent("SKILL.md"))
        try Data("echo fixture".utf8).write(to:source.appendingPathComponent("run.sh"))
        let archive=root.appendingPathComponent("package.zip")
        try SkillZip.write(directory:source,rootName:"demo-package",to:archive)
        var bytes=try Data(contentsOf:archive)
        func setMode(_ mode:UInt32,in data:inout Data) {
            for i in 0..<(data.count-46) where data[i]==0x50&&data[i+1]==0x4b&&data[i+2]==0x01&&data[i+3]==0x02 {
                let nameLength=Int(data[i+28])+Int(data[i+29])*256
                if String(data:data.subdata(in:(i+46)..<(i+46+nameLength)),encoding:.utf8)=="demo-package/run.sh" {
                    data[i+5]=3
                    let attributes=mode<<16
                    for shift in 0..<4{data[i+38+shift]=UInt8(truncatingIfNeeded:attributes>>(shift*8))}
                }
            }
        }
        setMode(0o100755,in:&bytes);try bytes.write(to:archive)
        let result=try SkillArchive.extractToStaging(archivePath:archive,stagingParent:root)
        let mode=(try fm.attributesOfItem(atPath:result.directory.appendingPathComponent("run.sh").path)[.posixPermissions] as? NSNumber)?.intValue
        XCTAssertEqual(mode,0o755)
        setMode(0o120777,in:&bytes);try bytes.write(to:archive)
        XCTAssertThrowsError(try SkillArchive.extractToStaging(archivePath:archive,stagingParent:root))
    }
}
