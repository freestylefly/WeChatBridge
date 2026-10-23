import AppKit
import Foundation

guard CommandLine.arguments.count == 2 else { exit(64) }
let running = NSRunningApplication.runningApplications(withBundleIdentifier: CommandLine.arguments[1])
for app in running { app.terminate() }
let deadline = Date().addingTimeInterval(8)
while running.contains(where: { !$0.isTerminated }) && Date() < deadline {
    RunLoop.current.run(until: Date().addingTimeInterval(0.1))
}
guard running.allSatisfy(\.isTerminated) else {
    fputs("The running app did not quit. Installation stopped before replacing its bundle.\n", stderr)
    exit(1)
}
