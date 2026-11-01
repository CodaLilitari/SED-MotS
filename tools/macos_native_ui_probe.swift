import Foundation
import CoreGraphics

guard CommandLine.arguments.count == 2, let requestedPID = Int(CommandLine.arguments[1]) else {
    fputs("Expected one owned process ID\n", stderr)
    exit(2)
}
let records = CGWindowListCopyWindowInfo(.optionOnScreenOnly, kCGNullWindowID) as? [[String: Any]] ?? []
var selected: [[String: Any]] = []
for record in records {
    guard let pid = record[kCGWindowOwnerPID as String] as? Int, pid == requestedPID else { continue }
    let bounds = record[kCGWindowBounds as String] as? [String: Any] ?? [:]
    selected.append([
        "pid": pid,
        "windowID": record[kCGWindowNumber as String] as? Int ?? 0,
        "onScreen": record[kCGWindowIsOnscreen as String] as? Bool ?? false,
        "layer": record[kCGWindowLayer as String] as? Int ?? -1,
        "width": bounds["Width"] as? Double ?? 0,
        "height": bounds["Height"] as? Double ?? 0,
        "title": record[kCGWindowName as String] as? String ?? ""
    ])
}
let data = try JSONSerialization.data(withJSONObject: selected, options: [.sortedKeys])
FileHandle.standardOutput.write(data)
FileHandle.standardOutput.write(Data("\n".utf8))
