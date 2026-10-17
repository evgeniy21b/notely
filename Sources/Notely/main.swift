import AppKit

setvbuf(stdout, nil, _IOLBF, 0)
let args = CommandLine.arguments
if let i = args.firstIndex(of: "--test-file"), i + 1 < args.count {
    let speed = args.firstIndex(of: "--speed").flatMap { Double(args[$0 + 1]) } ?? 1
    let t = FileTest(path: args[i + 1], speed: speed)
    t.run()
    RunLoop.main.run()
} else {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)   // без Dock, только значок в строке меню
    app.run()
}
