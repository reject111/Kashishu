import AppKit

@main
struct VisualRegression {
    static func main() throws {
        let root = URL(fileURLWithPath: CommandLine.arguments[1])
        for appearance in ["dark", "light", "reduced"] {
            let directory = root.appendingPathComponent(appearance)
            var metadata: [[String: Any]] = []
            var images: [NSImage] = []
            var background: [Double] = []
            for index in 0..<26 {
                let base = directory.appendingPathComponent(String(format: "%02d", index))
                let record = try JSONSerialization.jsonObject(with: Data(contentsOf: base.appendingPathExtension("json"))) as! [String: Any]
                let width = record["width"] as! Double
                let height = record["height"] as! Double
                precondition(width.isFinite && width >= 260 && height >= 64, "Invalid island geometry")
                if !(record["layoutAnimating"] as! Bool) {
                    precondition(width == record["targetWidth"] as! Double, "Settled layout must match its target exactly")
                }
                metadata.append(record)
                let imageURL = base.appendingPathExtension("window.png")
                let image = NSImage(contentsOf: imageURL)!
                images.append(image)
                if (1...8).contains(index), let rep = NSBitmapImageRep(data: try Data(contentsOf: imageURL)),
                   let color = rep.colorAt(x: rep.pixelsWide / 2, y: rep.pixelsHigh / 2 + Int(height * 0.80))?
                    .usingColorSpace(.deviceRGB) {
                    background.append(Double(color.redComponent * 0.2126 + color.greenComponent * 0.7152 + color.blueComponent * 0.0722))
                }
            }
            let firstBoundary = metadata[1...8].map { $0["lineIndex"] as! Int }
            precondition(firstBoundary == firstBoundary.sorted(), "Lyric indices must not oscillate around a boundary")
            let paused = metadata[12]["width"] as! Double
            precondition(!(metadata[12]["layoutAnimating"] as! Bool), "Paused layout must settle without fractional drift")
            let resumed = metadata[15]["width"] as! Double
            precondition(resumed > paused, "Resuming must restore artwork space")
            let change = (background.max() ?? 0) - (background.min() ?? 0)
            precondition(change < 0.06, "Background must not pulse darker during lyric changes")
            let selected = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 17, 19, 20, 22, 24]
            let size = NSSize(width: 1000, height: selected.count * 150)
            let sheet = NSImage(size: size, flipped: true) { rect in
                NSColor(calibratedWhite: 0.22, alpha: 1).setFill()
                rect.fill()
                for (row, index) in selected.enumerated() {
                    let record = metadata[index]
                    let time = record["time"] as! Double
                    let line = record["lineIndex"] as! Int
                    let caption = String(format: "%02d    %.3fs    line %d    %.1fpt", index, time, line, record["width"] as! Double)
                    (caption as NSString).draw(
                        at: NSPoint(x: 16, y: row * 150 + 8),
                        withAttributes: [.font: NSFont.monospacedSystemFont(ofSize: 13, weight: .regular), .foregroundColor: NSColor.white])
                    images[index].draw(
                        in: NSRect(x: 0, y: row * 150 + 30, width: 1000, height: 118),
                        from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
                }
                return true
            }
            let rep = NSBitmapImageRep(data: sheet.tiffRepresentation!)!
            try rep.representation(using: .png, properties: [:])!
                .write(to: root.appendingPathComponent("\(appearance)-contact.png"))
            let performance = try String(contentsOf: directory.appendingPathComponent("performance.json"), encoding: .utf8)
            print("\(appearance): 26 frames verified; background luminance range \(String(format: "%.4f", change)); drawing \(performance)")
        }
        print("native visual regression: ok")
    }
}
