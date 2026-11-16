import AppKit
import CoreImage
import CoreText

struct IslandLyricEffects {
    var lift = true
    var wave = false
    var glowIntensity = 4
}

enum IslandLyricContent: Equatable {
    case line(GlassLine, translation: Bool)
    case status(title: String, subtitle: String)
}

struct IslandLyricBitmap {
    let image: CGImage
    let size: NSSize
    let backingScale: CGFloat
}

final class IslandLyricRenderer {
    var effects = IslandLyricEffects()

    private struct Glyph {
        let path: CGPath?
        let font: CTFont
        let id: CGGlyph
        let position: CGPoint
        let start: Double
        let end: Double
        let syllableDuration: Double
    }
    private struct LayoutKey: Hashable {
        let line: GlassLine
        let font: NSFont
    }
    private var layouts: [LayoutKey: [Glyph]] = [:]
    private struct TextMetricKey: Hashable {
        let text: String
        let font: NSFont
    }

    // These are small, short-lived text bitmaps. Software filtering avoids Metal shader
    // compilation and GPU readback stalls on the first frames of a lyric transition.
    private let imageContext = CIContext(options: [.cacheIntermediates: false, .useSoftwareRenderer: true])
    private var textMetrics: [TextMetricKey: NSSize] = [:]

    init() {
        let bounds = CGRect(x: 0, y: 0, width: 128, height: 64)
        if let context = CGContext(
            data: nil, width: 128, height: 64, bitsPerComponent: 8, bytesPerRow: 128 * 4,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) {
            context.setFillColor(NSColor.white.cgColor)
            context.fill(CGRect(x: 12, y: 14, width: 42, height: 28))
            if let input = context.makeImage() {
                let sample = CIImage(cgImage: input)
                    .applyingFilter("CIGaussianBlur", parameters: [kCIInputRadiusKey: 3])
                let image = imageContext.createCGImage(sample, from: bounds)
                _ = image?.dataProvider?.data
            }
        }
    }

    func prepare(_ lines: ArraySlice<GlassLine>, scale: CGFloat) {
        for line in lines {
            _ = size(line.text, font: mainFont(scale))
            _ = translationSize(line.translation ?? "", ai: line.isAiTranslation, scale: scale)
            _ = glyphs(line, font: mainFont(scale))
        }
    }

    func measure(_ content: IslandLyricContent, scale: CGFloat) -> NSSize {
        switch content {
        case let .line(line, showTranslation):
            let main = size(line.text, font: mainFont(scale))
            let translation = showTranslation ? (line.translation ?? "") : ""
            let sub = translationSize(translation, ai: line.isAiTranslation, scale: scale)
            return NSSize(
                width: max(main.width, sub.width),
                height: translation.isEmpty ? 64 * scale : 84 * scale)
        case let .status(title, subtitle):
            return NSSize(
                width: max(
                    size(title, font: .systemFont(ofSize: 23 * scale, weight: .semibold)).width,
                    size(subtitle, font: subFont(scale)).width),
                height: 78 * scale)
        }
    }

    func draw(_ content: IslandLyricContent, in viewport: NSRect, scale: CGFloat, positionMs: Double) {
        guard viewport.width > 0, viewport.height > 0,
              let context = NSGraphicsContext.current?.cgContext else { return }
        context.saveGState()
        context.clip(to: viewport)
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        switch content {
        case let .line(line, showTranslation):
            drawLine(line, showTranslation: showTranslation, in: viewport, scale: scale, positionMs: positionMs)
        case let .status(title, subtitle):
            drawStatus(title: title, subtitle: subtitle, in: viewport, scale: scale)
        }
        context.endTransparencyLayer()
        context.restoreGState()
    }

    func snapshot(
        _ content: IslandLyricContent,
        viewport: NSSize,
        scale: CGFloat,
        positionMs: Double,
        backingScale: CGFloat
    ) -> IslandLyricBitmap? {
        bitmap(viewport: viewport, scale: scale, backingScale: backingScale) { rect in
            self.draw(content, in: rect, scale: scale, positionMs: positionMs)
        }
    }

    func bitmap(
        viewport: NSSize,
        scale: CGFloat,
        backingScale: CGFloat,
        drawing: (NSRect) -> Void
    ) -> IslandLyricBitmap? {
        let padding = 12 * scale
        let imageSize = NSSize(
            width: ceil((viewport.width + padding * 2) * backingScale) / backingScale,
            height: ceil((viewport.height + padding * 2) * backingScale) / backingScale)
        guard let bitmap = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: max(1, Int(ceil(imageSize.width * backingScale))),
            pixelsHigh: max(1, Int(ceil(imageSize.height * backingScale))),
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0),
              let graphics = NSGraphicsContext(bitmapImageRep: bitmap) else { return nil }
        bitmap.size = imageSize
        NSGraphicsContext.saveGraphicsState()
        let context = graphics.cgContext
        context.scaleBy(x: backingScale, y: backingScale)
        context.translateBy(x: 0, y: imageSize.height)
        context.scaleBy(x: 1, y: -1)
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
        drawing(NSRect(
            x: (imageSize.width - viewport.width) / 2,
            y: (imageSize.height - viewport.height) / 2,
            width: viewport.width,
            height: viewport.height))
        NSGraphicsContext.restoreGraphicsState()
        guard let image = bitmap.cgImage else { return nil }
        return IslandLyricBitmap(image: image, size: imageSize, backingScale: backingScale)
    }

    func draw(_ bitmap: IslandLyricBitmap, centeredAt center: NSPoint, opacity: CGFloat, blur: CGFloat) {
        guard opacity > 0.001 else { return }
        let image: CGImage
        if blur > 0.05 {
            let input = CIImage(cgImage: bitmap.image)
            let output = input.applyingFilter("CIGaussianBlur", parameters: [
                kCIInputRadiusKey: blur * bitmap.backingScale
            ]).cropped(to: input.extent)
            image = imageContext.createCGImage(output, from: input.extent) ?? bitmap.image
        } else {
            image = bitmap.image
        }
        NSImage(cgImage: image, size: bitmap.size).draw(
            in: NSRect(
                x: center.x - bitmap.size.width / 2,
                y: center.y - bitmap.size.height / 2,
                width: bitmap.size.width,
                height: bitmap.size.height),
            from: .zero,
            operation: .sourceOver,
            fraction: opacity,
            respectFlipped: true,
            hints: [.interpolation: NSImageInterpolation.high])
    }

    private func mainFont(_ scale: CGFloat) -> NSFont {
        .systemFont(ofSize: 26 * scale, weight: .semibold)
    }

    private func subFont(_ scale: CGFloat) -> NSFont {
        .systemFont(ofSize: 16 * scale, weight: .medium)
    }

    private func size(_ text: String, font: NSFont) -> NSSize {
        let key = TextMetricKey(text: text, font: font)
        if let cached = textMetrics[key] { return cached }
        let measured = (text as NSString).size(withAttributes: [.font: font])
        if textMetrics.count >= 512 { textMetrics.removeAll(keepingCapacity: true) }
        textMetrics[key] = measured
        return measured
    }

    private func drawLine(
        _ line: GlassLine,
        showTranslation: Bool,
        in viewport: NSRect,
        scale: CGFloat,
        positionMs: Double
    ) {
        let mainFont = mainFont(scale)
        let subFont = subFont(scale)
        let translation = showTranslation ? (line.translation ?? "").trimmingCharacters(in: .whitespacesAndNewlines) : ""
        let mainSize = size(line.text, font: mainFont)
        let subSize = translationSize(translation, ai: line.isAiTranslation, scale: scale)
        let gap = 5 * scale
        let totalHeight = mainSize.height + (translation.isEmpty ? 0 : gap + subSize.height)
        let top = viewport.midY - totalHeight / 2
        let activeWidth = activeWidth(line, font: mainFont, positionMs: positionMs)
        let overflow = max(0, mainSize.width - viewport.width)
        let scroll = min(max(activeWidth - viewport.width * 0.62, 0), overflow)
        let mainX = overflow > 0 ? viewport.minX - scroll : viewport.midX - mainSize.width / 2
        let highContrast = NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast
        let idleAlpha: CGFloat = highContrast ? 0.78 : 0.58
        drawKaraoke(line, font: mainFont, at: NSPoint(x: mainX, y: top + mainFont.ascender),
                    activeWidth: activeWidth, positionMs: positionMs, scale: scale, idleAlpha: idleAlpha)
        let translationOverflow = max(0, subSize.width - viewport.width)
        let duration = Double(max(1, line.endMs - line.startMs))
        let progress = min(max((positionMs - Double(line.startMs) - 300) / max(1, duration - 600), 0), 1)
        let translationX = translationOverflow > 0
            ? viewport.minX - CGFloat(progress) * translationOverflow
            : viewport.midX - subSize.width / 2
        if !translation.isEmpty {
            (translation as NSString).draw(
                at: NSPoint(x: translationX, y: top + mainSize.height + gap),
                withAttributes: [
                    .font: subFont,
                    .foregroundColor: NSColor.secondaryLabelColor
                ])
            if line.isAiTranslation {
                let badge = NSRect(x: translationX + size(translation, font: subFont).width + 6 * scale,
                                   y: top + mainSize.height + gap + 2 * scale,
                                   width: 24 * scale, height: 16 * scale)
                NSColor.labelColor.withAlphaComponent(0.10).setFill()
                NSBezierPath(roundedRect: badge, xRadius: 4 * scale, yRadius: 4 * scale).fill()
                drawBadgeLabel(in: badge, scale: scale)
            }
        }
        if overflow > 0 || translationOverflow > 0 {
            fadeEdges(in: viewport, width: 12 * scale)
        }
    }

    private func translationSize(_ text: String, ai: Bool, scale: CGFloat) -> NSSize {
        var result = size(text, font: subFont(scale))
        if !text.isEmpty && ai { result.width += 30 * scale }
        return result
    }

    private func drawBadgeLabel(in rect: NSRect, scale: CGFloat) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        let label = CTLineCreateWithAttributedString(NSAttributedString(string: "AI", attributes: [
            .font: NSFont.systemFont(ofSize: 10 * scale, weight: .semibold),
            .foregroundColor: NSColor.secondaryLabelColor
        ]))
        let ink = CTLineGetBoundsWithOptions(label, .useGlyphPathBounds)
        context.saveGState()
        context.translateBy(x: rect.midX, y: rect.midY)
        context.scaleBy(x: 1, y: -1)
        context.textMatrix = .identity
        context.textPosition = CGPoint(x: -ink.midX, y: -ink.midY)
        CTLineDraw(label, context)
        context.restoreGState()
    }

    private func glyphs(_ line: GlassLine, font: NSFont) -> [Glyph] {
        let key = LayoutKey(line: line, font: font)
        if let cached = layouts[key] { return cached }
        let shaped = CTLineCreateWithAttributedString(NSAttributedString(string: line.text, attributes: [.font: font]))
        let text = line.text as NSString
        var timings: [(range: NSRange, start: Double, end: Double, duration: Double)] = []
        var offset = 0
        let syllables = line.syllables.isEmpty
            ? [GlassSyllable(text: line.text, startMs: line.startMs, endMs: line.endMs)] : line.syllables
        for syllable in syllables {
            let count = (syllable.text as NSString).length
            let duration = Double(max(1, syllable.endMs - syllable.startMs))
            timings.append((NSRange(location: offset, length: count), Double(syllable.startMs),
                            Double(syllable.endMs), duration))
            offset += count
        }
        var result: [Glyph] = []
        for run in CTLineGetGlyphRuns(shaped) as! [CTRun] {
            let count = CTRunGetGlyphCount(run)
            var ids = [CGGlyph](repeating: 0, count: count)
            var positions = [CGPoint](repeating: .zero, count: count)
            var indices = [CFIndex](repeating: 0, count: count)
            CTRunGetGlyphs(run, CFRange(), &ids)
            CTRunGetPositions(run, CFRange(), &positions)
            CTRunGetStringIndices(run, CFRange(), &indices)
            let attributes = CTRunGetAttributes(run) as NSDictionary
            let runFont = attributes[kCTFontAttributeName] as! CTFont
            for index in 0..<count {
                let path = CTFontCreatePathForGlyph(runFont, ids[index], nil)
                let location = indices[index]
                guard location >= 0, location < text.length else { continue }
                let cluster = text.rangeOfComposedCharacterSequence(at: location)
                let timing = timings.first { NSLocationInRange(location, $0.range) }
                let duration = timing?.duration ?? Double(max(1, line.endMs - line.startMs))
                let range = timing?.range ?? NSRange(location: 0, length: text.length)
                let start = (timing?.start ?? Double(line.startMs))
                    + duration * Double(max(0, cluster.location - range.location)) / Double(max(1, range.length))
                let end = min(timing?.end ?? Double(line.endMs),
                              start + duration * Double(cluster.length) / Double(max(1, range.length)))
                result.append(Glyph(path: path, font: runFont, id: ids[index], position: positions[index], start: start, end: end,
                                    syllableDuration: duration))
            }
        }
        if layouts.count >= 64 { layouts.removeAll(keepingCapacity: true) }
        layouts[key] = result
        return result
    }

    private func drawKaraoke(_ line: GlassLine, font: NSFont, at origin: NSPoint, activeWidth: CGFloat,
                             positionMs: Double, scale: CGFloat, idleAlpha: CGFloat) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        let glow: CGFloat = [0.25, 0.5, 0.75, 1, 1.35][min(max(effects.glowIntensity, 1), 5) - 1]
        let bleed = 12 * (0.4 + 0.6 * glow) * scale
        let entry = min(max(activeWidth / (10 * scale), 0), 1)
        let ramp = entry * entry * (3 - 2 * entry)
        let width = size(line.text, font: font).width
        let remaining = min(max((width - activeWidth) / (12 * scale), 0), 1)
        let trail = activeWidth - 12 * scale * remaining
        let lead = activeWidth + bleed * ramp
        let label = NSColor.labelColor.usingColorSpace(.deviceRGB) ?? .white
        // A single continuous gradient avoids a brightness step when the sweep clears a glyph.
        let colors = (0...6).map { index -> CGColor in
            let t = CGFloat(index) / 6
            let smooth = t * t * t * (t * (t * 6 - 15) + 10)
            return label.withAlphaComponent((1 - smooth) * ramp).cgColor
        }
        let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors as CFArray,
                                  locations: (0...6).map { CGFloat($0) / 6 })!
        for glyph in glyphs(line, font: font) {
            let lift = effects.lift ? islandGlyphLift(position: positionMs, start: glyph.start, end: glyph.end,
                                                     syllableDuration: glyph.syllableDuration, wave: effects.wave) : 0
            context.saveGState()
            context.translateBy(x: origin.x + glyph.position.x, y: origin.y - glyph.position.y - 4 * scale * lift)
            context.scaleBy(x: 1, y: -1)
            guard let path = glyph.path else {
                // Color glyphs (for example emoji) have no outline. CoreText draws them intact.
                var id = glyph.id
                var point = CGPoint.zero
                CTFontDrawGlyphs(glyph.font, &id, &point, 1, context)
                context.restoreGState()
                continue
            }
            context.addPath(path)
            context.setFillColor(label.withAlphaComponent(idleAlpha).cgColor)
            context.fillPath()
            let lit = min(max((lead - glyph.position.x) / max(1, path.boundingBoxOfPath.width + bleed), 0), 1)
            if lit > 0 {
                context.saveGState()
                context.setShadow(offset: .zero, blur: 4 * scale,
                                  color: label.withAlphaComponent(0.32 * glow * lit * ramp).cgColor)
                context.setFillColor(label.withAlphaComponent(0.10 * glow * lit * ramp).cgColor)
                context.addPath(path)
                context.fillPath()
                context.restoreGState()
                context.addPath(path)
                context.clip()
                context.drawLinearGradient(gradient,
                    start: CGPoint(x: trail - glyph.position.x, y: 0),
                    end: CGPoint(x: max(trail + 0.01, lead) - glyph.position.x, y: 0),
                    options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
            }
            context.restoreGState()
        }
    }

    private func drawStatus(title: String, subtitle: String, in viewport: NSRect, scale: CGFloat) {
        let titleFont = NSFont.systemFont(ofSize: 23 * scale, weight: .semibold)
        let subFont = subFont(scale)
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        style.lineBreakMode = .byTruncatingTail
        let titleHeight = size(title, font: titleFont).height
        let subHeight = size(subtitle, font: subFont).height
        let gap = 4 * scale
        let top = viewport.midY - (titleHeight + gap + subHeight) / 2
        (title as NSString).draw(
            in: NSRect(x: viewport.minX, y: top, width: viewport.width, height: titleHeight),
            withAttributes: [.font: titleFont, .foregroundColor: NSColor.labelColor, .paragraphStyle: style])
        (subtitle as NSString).draw(
            in: NSRect(x: viewport.minX, y: top + titleHeight + gap, width: viewport.width, height: subHeight),
            withAttributes: [.font: subFont, .foregroundColor: NSColor.secondaryLabelColor, .paragraphStyle: style])
    }

    private func fadeEdges(in viewport: NSRect, width: CGFloat) {
        guard let context = NSGraphicsContext.current?.cgContext,
              let gradient = CGGradient(
                colorsSpace: CGColorSpaceCreateDeviceRGB(),
                colors: [NSColor.clear.cgColor, NSColor.black.cgColor, NSColor.black.cgColor, NSColor.clear.cgColor] as CFArray,
                locations: [0, min(0.25, width / viewport.width), max(0.75, 1 - width / viewport.width), 1]) else { return }
        context.saveGState()
        context.setBlendMode(.destinationIn)
        context.drawLinearGradient(
            gradient,
            start: CGPoint(x: viewport.minX, y: viewport.midY),
            end: CGPoint(x: viewport.maxX, y: viewport.midY),
            options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        context.restoreGState()
    }

    private func activeWidth(_ line: GlassLine, font: NSFont, positionMs: Double) -> CGFloat {
        guard positionMs > Double(line.startMs) else { return 0 }
        if line.syllables.isEmpty {
            let progress = min(max((positionMs - Double(line.startMs)) / Double(max(1, line.endMs - line.startMs)), 0), 1)
            return size(line.text, font: font).width * CGFloat(progress * progress * (3 - 2 * progress))
        }
        var prefix = ""
        for syllable in line.syllables {
            let prefixWidth = size(prefix, font: font).width
            if positionMs < Double(syllable.startMs) { return prefixWidth }
            if positionMs < Double(syllable.endMs) {
                let progress = min(max(
                    (positionMs - Double(syllable.startMs)) / Double(max(1, syllable.endMs - syllable.startMs)), 0), 1)
                let sweep = 0.65 * progress + 0.35 * progress * progress * (3 - 2 * progress)
                return prefixWidth + (size(prefix + syllable.text, font: font).width - prefixWidth) * CGFloat(sweep)
            }
            prefix += syllable.text
        }
        return size(line.text, font: font).width
    }
}
