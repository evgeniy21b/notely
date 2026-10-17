// Рисует нейтральную иконку «заметки»: серо-графитовый скруглённый квадрат, белый листок со строками.
import AppKit
let size: CGFloat = 1024
let img = NSImage(size: NSSize(width: size, height: size))
img.lockFocus()
let r = NSRect(x: 100, y: 100, width: 824, height: 824)
let bg = NSBezierPath(roundedRect: r, xRadius: 185, yRadius: 185)
NSGradient(starting: NSColor(calibratedWhite: 0.36, alpha: 1), ending: NSColor(calibratedWhite: 0.16, alpha: 1))!.draw(in: bg, angle: -90)
let sheet = NSBezierPath(roundedRect: NSRect(x: 270, y: 230, width: 484, height: 564), xRadius: 48, yRadius: 48)
NSColor(calibratedWhite: 0.97, alpha: 1).setFill(); sheet.fill()
NSColor(calibratedWhite: 0.62, alpha: 1).setFill()
for (i, w) in [380.0, 380, 300, 340].enumerated() {
    NSBezierPath(roundedRect: NSRect(x: 322, y: 660 - CGFloat(i) * 105, width: CGFloat(w), height: 34), xRadius: 17, yRadius: 17).fill()
}
img.unlockFocus()
let rep = NSBitmapImageRep(data: img.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
