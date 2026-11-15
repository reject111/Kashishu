import AppKit
import QuartzCore
#if DEBUG
import ScreenCaptureKit
#endif

private let glassProtocolVersion = 1

struct GlassSyllable: Decodable, Equatable {
    let text: String
    let startMs: Int64
    let endMs: Int64
}

struct GlassLine: Decodable, Equatable {
    let text: String
    let translation: String?
    let startMs: Int64
    let endMs: Int64
    let syllables: [GlassSyllable]
}

private struct GlassTrack: Decodable {
    let id: String
    let title: String
    let artists: [String]
    let album: String
    let durationMs: Int64
    let albumArt: String?
    let artistImages: [String]
    let lyrics: [GlassLine]
}

private struct GlassPlayback: Decodable {
    let trackId: String?
    let positionMs: Double
    let isPlaying: Bool
    let status: String
    let attention: Bool
    let canPlayPause: Bool
    let canPrevious: Bool
    let canNext: Bool
    let canFavorite: Bool
    let isFavorite: Bool
    let busy: Bool
}

private struct GlassSettings: Decodable {
    let showIsland: Bool
    let showTranslation: Bool
    let scale: Double
    let maxWidthPercent: Double
    let rememberPosition: Bool
    let lockPosition: Bool
    let clickThrough: Bool
    let pausedDisplay: String
    let temporaryHideSeconds: Int
    let leftIconAction: String
    let rightIconAction: String
    let maxFrameRate: Int
    let topOffset: Int
    let windowX: Int?
    let windowY: Int?
    let displayId: String?
    let enableTrackOffsets: Bool
    let trackOffsetMs: Int
}

private struct GlassMessage: Decodable {
    let type: String
    let protocolVersion: Int?
    let track: GlassTrack?
    let playback: GlassPlayback?
    let settings: GlassSettings?
    let trackId: String?
    let seconds: Int?
    let appIcon: String?
}

private enum Side {
    case left
    case right
}

private struct LoadedTrack {
    let id: String
    let title: String
    let artists: [String]
    let album: String
    let durationMs: Int64
    let albumArt: NSImage?
    let artistImages: [NSImage]
    let lyrics: [GlassLine]
}

private struct LoadedPlayback {
    let trackId: String?
    let isPlaying: Bool
    let status: String
    let attention: Bool
    let canPlayPause: Bool
    let canPrevious: Bool
    let canNext: Bool
    let canFavorite: Bool
    let isFavorite: Bool
    let busy: Bool
}

private struct LoadedSettings {
    let showIsland: Bool
    let showTranslation: Bool
    let scale: CGFloat
    let maxWidthPercent: CGFloat
    let rememberPosition: Bool
    let lockPosition: Bool
    let clickThrough: Bool
    let pausedDisplay: String
    let temporaryHideSeconds: Int
    let leftIconAction: IslandIcon
    let rightIconAction: IslandIcon
    let maxFrameRate: Int
    let topOffset: CGFloat
    let windowX: CGFloat?
    let windowY: CGFloat?
    let displayId: String?
    let enableTrackOffsets: Bool
    let trackOffsetMs: Int

    static let initial = LoadedSettings(
        showIsland: true,
        showTranslation: true,
        scale: 1,
        maxWidthPercent: 70,
        rememberPosition: true,
        lockPosition: false,
        clickThrough: false,
        pausedDisplay: "HideImmediately",
        temporaryHideSeconds: 2,
        leftIconAction: .lyricsWindow,
        rightIconAction: .favorite,
        maxFrameRate: 0,
        topOffset: 58,
        windowX: nil,
        windowY: nil,
        displayId: nil,
        enableTrackOffsets: false,
        trackOffsetMs: 0)
}

private final class LineAssembler: @unchecked Sendable {
    private let lock = NSLock()
    private var buffer = Data()

    func append(_ chunk: Data) -> [Data] {
        lock.lock()
        defer { lock.unlock() }
        buffer.append(chunk)
        var lines: [Data] = []
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = buffer.prefix(upTo: newline)
            buffer.removeSubrange(buffer.startIndex...newline)
            if !line.isEmpty {
                lines.append(Data(line))
            }
        }
        return lines
    }
}

private let transparentColor = NSColor.clear

private func clamp(_ value: CGFloat, _ lower: CGFloat, _ upper: CGFloat) -> CGFloat {
    min(max(value, lower), max(lower, upper))
}

private func statusSubtitle(track: LoadedTrack?, status: String) -> String {
    if let track {
        return status.isEmpty ? track.artists.joined(separator: ", ") : status
    }
    if status.contains("MPRIS") {
        return "读取本机播放器的 MPRIS 信息"
    }
    if status.contains("Spotify") {
        return "首次使用 Spotify 会打开浏览器授权"
    }
    return status.isEmpty ? "等待播放信息" : status
}

private func decodeImage(_ base64: String?) -> NSImage? {
    guard let base64, !base64.isEmpty, let data = Data(base64Encoded: base64) else {
        return nil
    }
    return NSImage(data: data)
}

private final class GlassSurface: NSView {
    let content: IslandView
    private let effectView: NSView
    private let contentHost: FlippedView

    init(content: IslandView) {
        self.content = content
        contentHost = FlippedView(frame: .zero)
        contentHost.addSubview(content)
        contentHost.autoresizesSubviews = true
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.style = .regular
            glass.cornerRadius = 45
            glass.contentView = contentHost
            if glass.responds(to: NSSelectorFromString("setEffectIsInteractive:")) {
                glass.setValue(true, forKey: "effectIsInteractive")
            }
            effectView = glass
        } else {
            let visual = NSVisualEffectView()
            visual.material = .hudWindow
            visual.blendingMode = .behindWindow
            visual.state = .active
            visual.wantsLayer = true
            visual.layer?.masksToBounds = true
            visual.addSubview(contentHost)
            effectView = visual
        }
        super.init(frame: .zero)
        wantsLayer = true
        addSubview(effectView)
        autoresizesSubviews = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override var isFlipped: Bool { true }

    func applyCornerRadius(_ radius: CGFloat) {
        if #available(macOS 26.0, *),
           let glass = effectView as? NSGlassEffectView {
            if glass.cornerRadius != radius { glass.cornerRadius = radius }
        } else {
            effectView.layer?.cornerRadius = radius
        }
    }

    override func layout() {
        super.layout()
        effectView.frame = bounds
        contentHost.frame = bounds
        content.frame = contentHost.bounds
    }

}

private final class IslandPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor
private final class AppController: NSObject, NSApplicationDelegate {
    private let window: NSPanel
    private let root: NSView
    private let surface: GlassSurface
    private let island: IslandView
    private var settings = LoadedSettings.initial
    private var playback: LoadedPlayback?
    private var track: LoadedTrack?
    private var appIcon: NSImage?
    private var timer: Timer?
    private var lastTick = CACurrentMediaTime()
    private var frameTime = CACurrentMediaTime()
    private var framePositionMs = 0.0
    private var playbackClock = IslandPlaybackClock()
    private let lyricRenderer = IslandLyricRenderer()
    private var lyricContent = IslandLyricContent.status(title: "等待播放信息", subtitle: "Lyricify Island")
    private var outgoingLyrics: IslandLyricBitmap?
    private var lyricViewport = NSSize(width: 240, height: 70)
    private var transitionReversed = false
    private var lineIndex = Int.min
    private var lineChangedAt: CFTimeInterval = 0
    private var trackChangedAt: CFTimeInterval = 0
    private var sideHover: Side?
    private var pressedSide: Side?
    private var pressedTrackID: String?
    private var menuTrackID: String?
    private var pressPoint: NSPoint?
    private var dragMouseOffset: NSPoint?
    private var dragPressScreen: NSPoint?
    private var dragScreen: NSScreen?
    private var dragged = false
    private var pendingClick: DispatchWorkItem?
    private var suppressMouseUpClick = false
    private var temporarilyHiddenUntil: CFTimeInterval = 0
    private var pauseRetention = IslandPauseRetention()
    private var scrollGate = IslandScrollGate()
    private var inactiveSince: CFTimeInterval = 0
    private var displayWidth: CGFloat = 450
    private var displayHeight: CGFloat = 90
    private var capsuleProgress: Double = 0
    private var sidesProgress: Double = 1
    private var widthMotion = IslandTween(450)
    private var heightMotion = IslandTween(90)
    private var sideMotion = IslandTween(1)
    private var visibilityMotion = IslandTween(0)
    private var avatar: NSImage?
    private var previousAvatar: NSImage?
    private var avatarChangedAt = 0.0
    private var album: NSImage?
    private var previousAlbum: NSImage?
    private var albumChangedAt = 0.0
    private var didShowLogged = false
    private var didReceiveTrack = false
    #if DEBUG
    private var snapshotScheduled = false
    private var visualTestStartedAt = 0.0
    private var visualTestFrame = 0
    private var visualTestBackdrop: NSWindow?
    private var visualDrawTimes: [Double] = []
    private var visualSlowFrames: [[String: Any]] = []
    private var captureWindow: SCWindow?
    #endif

    override init() {
        let screen = NSScreen.main ?? NSScreen.screens.first
        let panelWidth = max(420, (screen?.visibleFrame.width ?? 1200) * 0.70)
        let panelHeight: CGFloat = 116
        let frame = NSRect(
            x: (screen?.visibleFrame.midX ?? 600) - panelWidth / 2,
            y: (screen?.visibleFrame.maxY ?? 900) - panelHeight - 58,
            width: panelWidth,
            height: panelHeight)
        window = IslandPanel(
            contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false)
        root = FlippedView(frame: NSRect(origin: .zero, size: frame.size))
        island = IslandView(frame: .zero)
        surface = GlassSurface(content: island)
        super.init()

        window.isOpaque = false
        window.backgroundColor = transparentColor
        window.hasShadow = false
        window.level = .statusBar
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        window.hidesOnDeactivate = false
        window.isMovable = false
        window.ignoresMouseEvents = true
        window.acceptsMouseMovedEvents = true
        window.contentView = root
        root.addSubview(surface)
        island.controller = self
        applyWindowPlacement()
        window.orderFrontRegardless()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        installTimer()
        #if DEBUG
        if ProcessInfo.processInfo.environment["LYRICIFY_GLASS_VISUAL_TEST"] == "1" {
            startVisualTest()
            return
        }
        #endif
        startInput()
        send(["type": "ready", "protocolVersion": glassProtocolVersion])
        log("ready on macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")
    }

    private func installTimer() {
        timer?.invalidate()
        let requested = settings.maxFrameRate
        let displayRate = max(60, window.screen?.maximumFramesPerSecond ?? 60)
        let framesPerSecond = requested > 0 ? min(displayRate, max(10, requested)) : displayRate
        let timer = Timer(timeInterval: 1.0 / Double(framesPerSecond), repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.tick()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func applicationWillTerminate(_ notification: Notification) {
        timer?.invalidate()
        #if DEBUG
        if let directory = ProcessInfo.processInfo.environment["LYRICIFY_GLASS_CAPTURE_DIR"],
           !visualDrawTimes.isEmpty {
            let times = visualDrawTimes.sorted()
            let report: [String: Any] = [
                "frames": times.count, "medianMs": times[times.count / 2],
                "p95Ms": times[min(times.count - 1, Int(Double(times.count) * 0.95))],
                "maxMs": times.last ?? 0, "slowFrames": visualSlowFrames
            ]
            if let data = try? JSONSerialization.data(withJSONObject: report, options: [.sortedKeys]) {
                try? data.write(to: URL(fileURLWithPath: directory).appendingPathComponent("performance.json"))
            }
        }
        #endif
    }

    func applicationDidChangeScreenParameters(_ notification: Notification) {
        applyWindowPlacement(preserveOrigin: true)
        installTimer()
    }

    private func log(_ message: String) {
        guard let data = "[macos-glass] \(message)\n".data(using: .utf8) else { return }
        FileHandle.standardError.write(data)
    }

    private func send(_ object: [String: Any]) {
        guard JSONSerialization.isValidJSONObject(object),
              let data = try? JSONSerialization.data(withJSONObject: object),
              var line = String(data: data, encoding: .utf8) else {
            return
        }
        line.append("\n")
        guard let output = line.data(using: .utf8) else { return }
        FileHandle.standardOutput.write(output)
    }

    private func startInput() {
        let assembler = LineAssembler()
        FileHandle.standardInput.readabilityHandler = { handle in
            let chunk = handle.availableData
            if chunk.isEmpty {
                FileHandle.standardInput.readabilityHandler = nil
                DispatchQueue.main.async {
                    NSApp.terminate(nil)
                }
                return
            }
            for line in assembler.append(chunk) {
                DispatchQueue.main.async {
                    self.receive(line)
                }
            }
        }
    }

    private func receive(_ data: Data) {
        let decoder = JSONDecoder()
        guard let message = try? decoder.decode(GlassMessage.self, from: data) else {
            log("ignored malformed message")
            return
        }
        switch message.type {
        case "hello":
            if message.protocolVersion != glassProtocolVersion {
                log("protocol mismatch from host")
                NSApp.terminate(nil)
            }
        case "appIcon":
            if let icon = decodeImage(message.appIcon) {
                appIcon = icon
                island.needsDisplay = true
            }
        case "track":
            guard let decoded = message.track else {
                track = nil
                island.needsDisplay = true
                return
            }
            let art = decodeImage(decoded.albumArt)
            let artists = decoded.artistImages.compactMap(decodeImage)
            let loaded = LoadedTrack(
                id: decoded.id,
                title: decoded.title,
                artists: decoded.artists,
                album: decoded.album,
                durationMs: decoded.durationMs,
                albumArt: art,
                artistImages: artists,
                lyrics: decoded.lyrics)
            if track?.id != loaded.id {
                trackChangedAt = CACurrentMediaTime()
            }
            track = loaded
            if !didReceiveTrack {
                didReceiveTrack = true
                log("track \(loaded.id), \(loaded.lyrics.count) lyric lines")
                scheduleDebugSnapshotIfRequested()
            }
            island.needsDisplay = true
        case "playback":
            guard let decoded = message.playback else { return }
            let now = CACurrentMediaTime()
            playbackClock.receive(
                trackID: decoded.trackId,
                positionMs: decoded.positionMs,
                playing: decoded.isPlaying,
                at: now)
            playback = LoadedPlayback(
                trackId: decoded.trackId,
                isPlaying: decoded.isPlaying,
                status: decoded.status,
                attention: decoded.attention,
                canPlayPause: decoded.canPlayPause,
                canPrevious: decoded.canPrevious,
                canNext: decoded.canNext,
                canFavorite: decoded.canFavorite,
                isFavorite: decoded.isFavorite,
                busy: decoded.busy)
            scheduleDebugSnapshotIfRequested()
            pauseRetention.receive(trackID: decoded.trackId, playing: decoded.isPlaying)
            island.needsDisplay = true
        case "controlFailed":
            if message.trackId == pauseRetention.trackID { pauseRetention.cancel() }
        case "settings":
            guard let decoded = message.settings else { return }
            let previous = settings
            settings = LoadedSettings(
                showIsland: decoded.showIsland,
                showTranslation: decoded.showTranslation,
                scale: clamp(CGFloat(decoded.scale), 0.5, 2),
                maxWidthPercent: clamp(CGFloat(decoded.maxWidthPercent), 40, 100),
                rememberPosition: decoded.rememberPosition,
                lockPosition: decoded.lockPosition,
                clickThrough: decoded.clickThrough,
                pausedDisplay: decoded.pausedDisplay,
                temporaryHideSeconds: decoded.temporaryHideSeconds,
                leftIconAction: IslandIcon(rawValue: decoded.leftIconAction) ?? .none,
                rightIconAction: IslandIcon(rawValue: decoded.rightIconAction) ?? .none,
                maxFrameRate: decoded.maxFrameRate,
                topOffset: CGFloat(decoded.topOffset),
                windowX: decoded.windowX.map(CGFloat.init),
                windowY: decoded.windowY.map(CGFloat.init),
                displayId: decoded.displayId,
                enableTrackOffsets: decoded.enableTrackOffsets,
                trackOffsetMs: decoded.trackOffsetMs)
            if previous.scale != settings.scale
                || previous.maxWidthPercent != settings.maxWidthPercent
                || previous.windowX != settings.windowX
                || previous.windowY != settings.windowY
                || previous.topOffset != settings.topOffset
                || previous.displayId != settings.displayId
                || previous.rememberPosition != settings.rememberPosition {
                applyWindowPlacement()
            }
            if previous.maxFrameRate != settings.maxFrameRate {
                installTimer()
            }
            island.needsDisplay = true
        case "hide":
            hideTemporarily(seconds: message.seconds ?? settings.temporaryHideSeconds)
        case "quit":
            NSApp.terminate(nil)
        default:
            break
        }
    }

    private func scheduleDebugSnapshotIfRequested() {
        #if DEBUG
        guard !snapshotScheduled,
              let path = ProcessInfo.processInfo.environment["LYRICIFY_GLASS_SNAPSHOT"],
              !path.isEmpty else {
            return
        }
        snapshotScheduled = true
        let delay = Double(
            ProcessInfo.processInfo.environment["LYRICIFY_GLASS_SNAPSHOT_DELAY"] ?? ""
        ) ?? 1.0
        DispatchQueue.main.asyncAfter(deadline: .now() + max(0.1, delay)) { [weak self] in
            guard let self, self.island.bounds.width > 1 else { return }
            let bounds = self.island.bounds
            guard let rep = self.island.bitmapImageRepForCachingDisplay(in: bounds) else {
                self.log("debug snapshot failed to allocate")
                return
            }
            self.island.cacheDisplay(in: bounds, to: rep)
            guard let data = rep.representation(using: .png, properties: [:]) else {
                self.log("debug snapshot failed to encode")
                return
            }
            do {
                try data.write(to: URL(fileURLWithPath: path))
                self.log("debug snapshot \(path)")
                let surfacePath = URL(fileURLWithPath: path)
                    .deletingPathExtension()
                    .appendingPathExtension("surface.png")
                let surfaceBounds = self.surface.bounds
                if let surfaceRep = self.surface.bitmapImageRepForCachingDisplay(in: surfaceBounds) {
                    self.surface.cacheDisplay(in: surfaceBounds, to: surfaceRep)
                    if let surfaceData = surfaceRep.representation(using: .png, properties: [:]) {
                        try surfaceData.write(to: surfacePath)
                        self.log("debug surface snapshot \(surfacePath.path)")
                    }
                }
                let windowPath = URL(fileURLWithPath: path)
                    .deletingPathExtension()
                    .appendingPathExtension("window.png")
                self.captureOwnWindow(to: windowPath)
            } catch {
                self.log("debug snapshot failed: \(error.localizedDescription)")
            }
        }
        #endif
    }

    #if DEBUG
    private func captureOwnWindow(to url: URL) {
        let size = window.frame.size
        let backingScale = window.backingScaleFactor
        Task { @MainActor in
            do {
                if captureWindow == nil {
                    let content = try await SCShareableContent.currentProcess
                    captureWindow = content.windows.first { $0.windowID == CGWindowID(window.windowNumber) }
                }
                guard let captureWindow else {
                    log("own window is unavailable for capture")
                    return
                }
                let filter = SCContentFilter(desktopIndependentWindow: captureWindow)
                let configuration = SCStreamConfiguration()
                configuration.width = Int(size.width * backingScale)
                configuration.height = Int(size.height * backingScale)
                configuration.showsCursor = false
                configuration.ignoreShadowsSingleWindow = true
                let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
                if let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) {
                    try data.write(to: url)
                }
            } catch {
                log("own window capture failed: \(error.localizedDescription)")
            }
        }
    }

    private func visualMessage(_ type: String, _ payload: [String: Any]) {
        if let data = try? JSONSerialization.data(withJSONObject: ["type": type, type: payload]) {
            receive(data)
        }
    }

    private func visualPlayback(_ position: Double, playing: Bool, attention: Bool = false) {
        visualMessage("playback", [
            "trackId": "visual-test", "positionMs": position, "isPlaying": playing,
            "status": attention ? "连接播放器失败，请检查自动化权限后重试" : "",
            "attention": attention, "canPlayPause": true, "canPrevious": true,
            "canNext": true, "canFavorite": true, "isFavorite": false, "busy": false
        ])
    }

    private func startVisualTest() {
        visualTestStartedAt = CACurrentMediaTime()
        NSApp.appearance = NSAppearance(named:
            ProcessInfo.processInfo.environment["LYRICIFY_GLASS_APPEARANCE"] == "light" ? .aqua : .darkAqua)
        let backdrop = IslandPanel(
            contentRect: window.frame.insetBy(dx: -20, dy: -60),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        let canvas = NSView(frame: NSRect(origin: .zero, size: backdrop.frame.size))
        canvas.wantsLayer = true
        let gradient = CAGradientLayer()
        gradient.frame = canvas.bounds
        gradient.colors = [
            NSColor(calibratedRed: 0.24, green: 0.42, blue: 0.72, alpha: 1).cgColor,
            NSColor(calibratedRed: 0.52, green: 0.25, blue: 0.43, alpha: 1).cgColor,
            NSColor(calibratedRed: 0.11, green: 0.22, blue: 0.31, alpha: 1).cgColor
        ]
        gradient.startPoint = CGPoint(x: 0, y: 0)
        gradient.endPoint = CGPoint(x: 1, y: 1)
        canvas.layer?.addSublayer(gradient)
        backdrop.contentView = canvas
        backdrop.ignoresMouseEvents = true
        backdrop.level = .floating
        backdrop.orderFrontRegardless()
        visualTestBackdrop = backdrop
        window.orderFrontRegardless()
        visualMessage("settings", [
            "showIsland": true, "showTranslation": true, "scale": 1,
            "maxWidthPercent": 70, "backgroundOpacityPercent": 67,
            "rememberPosition": false, "lockPosition": false, "clickThrough": false,
            "pausedDisplay": "KeepVisible", "temporaryHideSeconds": 2,
            "leftIconAction": "LyricsWindow", "rightIconAction": "Favorite",
            "maxFrameRate": 60, "topOffset": 58, "enableTrackOffsets": false, "trackOffsetMs": 0
        ])
        let lines: [[String: Any]] = [
            ["text": "One step, one day for a better future", "translation": "一步一个脚印，走向更美好的未来",
             "startMs": 0, "endMs": 2000, "syllables": []],
            ["text": "All our colors 世界中に溢れ出す", "translation": "让所有色彩，向着全世界溢出",
             "startMs": 2000, "endMs": 4500, "syllables": []],
            ["text": "Together", "translation": "相聚在一起，共同将这纯白的画布装点成属于我们的绚烂未来；让每一种色彩和每一段旋律都能够完整地被看见，即使译文远远长于原来的这一句歌词，也应该平稳地沿着文字视口继续滚动",
             "startMs": 4500, "endMs": 9000, "syllables": []]
        ]
        visualMessage("track", [
            "id": "visual-test", "title": "Dream Rig", "artists": ["Crystal Statues"],
            "album": "Native visual regression", "durationMs": 12000, "artistImages": [], "lyrics": lines
        ])
        visualPlayback(0, playing: true)
        for sample in 0..<16 {
            let delay = 1.6 + Double(sample) * 0.05
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
                self?.visualPlayback(delay * 1000 + (sample.isMultiple(of: 2) ? -55 : 55), playing: true)
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            self?.visualPlayback(3000, playing: false)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 4) { [weak self] in
            self?.visualPlayback(3000, playing: true)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 4.7) { [weak self] in
            self?.visualPlayback(4500, playing: true)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 5.5) { [weak self] in
            self?.visualPlayback(1000, playing: true)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 6.5) { [weak self] in
            self?.visualMessage("track", [
                "id": "visual-test", "title": "很长的歌曲标题需要保持居中并在边缘自然截断，而不是越过胶囊的内容区域",
                "artists": ["Crystal Statues"], "album": "", "durationMs": 12000, "artistImages": [], "lyrics": []
            ])
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 7.2) { [weak self] in
            self?.visualPlayback(2700, playing: false, attention: true)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { NSApp.terminate(nil) }
    }

    private func captureVisualTestFrame() {
        guard visualTestStartedAt > 0,
              let directory = ProcessInfo.processInfo.environment["LYRICIFY_GLASS_CAPTURE_DIR"] else { return }
        let times = [0.6, 1.9, 2.02, 2.06, 2.10, 2.16, 2.24, 2.36, 2.7, 3.02, 3.12, 3.38, 3.8,
                     4.02, 4.18, 4.4, 5.02, 5.12, 5.4, 5.54, 5.64, 5.9, 6.6, 6.9, 7.3, 7.7]
        let elapsed = frameTime - visualTestStartedAt
        guard visualTestFrame < times.count, elapsed >= times[visualTestFrame] else { return }
        let number = visualTestFrame
        visualTestFrame += 1
        island.displayIfNeeded()
        if let bitmap = island.bitmapImageRepForCachingDisplay(in: island.bounds) {
            island.cacheDisplay(in: island.bounds, to: bitmap)
        }
        CATransaction.flush()
        let base = URL(fileURLWithPath: directory).appendingPathComponent(String(format: "%02d", number))
        do {
            captureOwnWindow(to: base.appendingPathExtension("window.png"))
            let metadata: [String: Any] = [
                "time": elapsed, "positionMs": framePositionMs, "lineIndex": lineIndex,
                "width": displayWidth, "height": displayHeight, "viewportWidth": lyricViewport.width,
                "sides": sidesProgress, "transition": outgoingLyrics != nil,
                "transitionElapsed": frameTime - lineChangedAt,
                "layoutAnimating": widthMotion.isAnimating(at: frameTime) || sideMotion.isAnimating(at: frameTime),
                "targetWidth": targetDimensions().width
            ]
            try JSONSerialization.data(withJSONObject: metadata, options: [.sortedKeys])
                .write(to: base.appendingPathExtension("json"))
        } catch {
            log("visual test capture failed: \(error.localizedDescription)")
        }
    }
    #endif

    private func screenForPlacement() -> NSScreen? {
        if let displayId = settings.displayId,
           let match = NSScreen.screens.first(where: {
               String(describing: ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0) == displayId
           }) {
            return match
        }
        return screenForCurrentWindow()
    }

    private func screenForCurrentWindow() -> NSScreen? {
        let center = NSPoint(x: window.frame.midX, y: window.frame.midY)
        return NSScreen.screens.first(where: { NSMouseInRect(center, $0.frame, false) })
            ?? NSScreen.main
            ?? NSScreen.screens.first
    }

    private func screenForMouse(_ location: NSPoint) -> NSScreen? {
        NSScreen.screens.first(where: { NSMouseInRect(location, $0.frame, false) })
            ?? screenForCurrentWindow()
    }

    private func applyWindowPlacement(on preferredScreen: NSScreen? = nil, preserveOrigin: Bool = false) {
        guard let screen = preferredScreen
            ?? (preserveOrigin ? screenForCurrentWindow() : screenForPlacement())
            ?? NSScreen.main else { return }
        let available = screen.visibleFrame
        let scale = settings.scale
        let width = min(available.width, max(420, available.width * settings.maxWidthPercent / 100))
        let height = min(available.height, max(80, 116 * scale))
        var origin: NSPoint
        if preserveOrigin {
            origin = window.frame.origin
        } else if settings.rememberPosition,
           let x = settings.windowX,
           let y = settings.windowY {
            origin = NSPoint(x: x, y: y)
        } else {
            origin = NSPoint(
                x: available.midX - width / 2,
                y: available.maxY - height - settings.topOffset)
        }
        let maxX = available.maxX - width
        let maxY = available.maxY - height
        origin.x = clamp(origin.x, available.minX, maxX)
        origin.y = clamp(origin.y, available.minY, maxY)
        let frame = NSRect(x: origin.x, y: origin.y, width: width, height: height)
        window.setFrame(frame, display: true)
    }

    private func constrainedOrigin(_ origin: NSPoint, on screen: NSScreen) -> NSPoint {
        let available = screen.visibleFrame
        let frame = window.frame
        return NSPoint(
            x: clamp(origin.x, available.minX, available.maxX - frame.width),
            y: clamp(origin.y, available.minY, available.maxY - frame.height))
    }

    private func constrainWindowToScreen() {
        guard let screen = screenForCurrentWindow() ?? NSScreen.main else { return }
        let frame = window.frame
        let origin = constrainedOrigin(frame.origin, on: screen)
        let x = origin.x
        let y = origin.y
        if x != frame.origin.x || y != frame.origin.y {
            window.setFrameOrigin(NSPoint(x: x, y: y))
        }
    }

    private var currentPositionMs: Double {
        framePositionMs
    }

    private var isAttention: Bool {
        playback?.attention == true
    }

    private var isPlaying: Bool {
        playback?.isPlaying == true
    }

    private var tempHideActive: Bool {
        temporarilyHiddenUntil > CACurrentMediaTime()
    }

    private var pausedCapsuleVisible: Bool {
        if isAttention || isPlaying || pauseRetention.trackID != nil {
            return true
        }
        switch settings.pausedDisplay {
        case "KeepVisible":
            return true
        case "HideAfterThreeSeconds":
            return CACurrentMediaTime() - inactiveSince < 3
        default:
            return false
        }
    }

    private var capsuleVisible: Bool {
        settings.showIsland && !tempHideActive && pausedCapsuleVisible
    }

    private var showSides: Bool {
        playback == nil || isPlaying || (pauseRetention.trackID != nil && !pauseRetention.collapseSides)
    }

    private var activeLineIndex: Int {
        guard isAttention == false, let track, !track.lyrics.isEmpty else { return -1 }
        let position = currentPositionMs
        var low = 0
        var high = track.lyrics.count - 1
        var found = -1
        while low <= high {
            let mid = (low + high) / 2
            if Double(track.lyrics[mid].startMs) <= position {
                found = mid
                low = mid + 1
            } else {
                high = mid - 1
            }
        }
        return found
    }

    private var reduceMotion: Bool {
        #if DEBUG
        if ProcessInfo.processInfo.environment["LYRICIFY_GLASS_REDUCE_MOTION"] == "1" { return true }
        #endif
        return NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
    }

    private func targetDimensions() -> (width: CGFloat, height: CGFloat) {
        let scale = settings.scale
        let content = lyricRenderer.measure(lyricContent, scale: scale)
        let maxWidth = max(1, window.frame.width - 16 * scale)
        let sides = CGFloat(showSides ? 1 : 0)
        let horizontalPadding = 68 + 96 * sides
        let minimumWidth = min((260 + 100 * sides) * scale, maxWidth)
        let width = clamp(content.width + horizontalPadding * scale, minimumWidth, maxWidth)
        let backing = window.backingScaleFactor
        let alignedWidth = min((width * backing / 2).rounded(), floor(maxWidth * backing / 2)) * 2 / backing
        return (max(1, alignedWidth), (content.height * backing / 2).rounded() * 2 / backing)
    }

    private func tick() {
        let now = CACurrentMediaTime()
        let dt = min(max(now - lastTick, 0), 0.05)
        lastTick = now
        frameTime = now
        let previousPosition = framePositionMs
        framePositionMs = playbackClock.position(at: now)
        if let track, track.durationMs > 0 {
            framePositionMs = min(framePositionMs, Double(track.durationMs))
        }
        updateLyricContent(now, previousPosition: previousPosition)
        if IslandLyricPhase(
            elapsed: now - lineChangedAt,
            reversed: transitionReversed,
            reduceMotion: reduceMotion).complete {
            outgoingLyrics = nil
        }

        if !isPlaying && pauseRetention.trackID == nil && inactiveSince == 0 {
            inactiveSince = now
        } else if isPlaying || pauseRetention.trackID != nil {
            inactiveSince = 0
        }
        let reduceMotion = self.reduceMotion
        let stateDuration = reduceMotion ? 0.0 : 0.34
        sideMotion.move(to: showSides ? 1 : 0, at: now, duration: stateDuration)
        sidesProgress = sideMotion.value(at: now)
        let target = targetDimensions()
        widthMotion.move(to: target.width, at: now, duration: stateDuration)
        heightMotion.move(to: target.height, at: now, duration: stateDuration)
        displayWidth = widthMotion.value(at: now)
        displayHeight = heightMotion.value(at: now)
        visibilityMotion.move(to: capsuleVisible ? 1 : 0, at: now, duration: 0.16)
        capsuleProgress = visibilityMotion.value(at: now)
        let finalInset = (showSides ? 76.0 : 28.0) * settings.scale
        lyricViewport = NSSize(width: max(1, target.width - finalInset * 2), height: max(1, target.height - 14 * settings.scale))
        updateArtwork(now)
        layoutSurface()
        updateMouseInteraction()
        island.advanceHover(dt: dt, reduceMotion: reduceMotion)
        island.needsDisplay = true
        #if DEBUG
        captureVisualTestFrame()
        #endif
    }

    private func updateLyricContent(_ now: CFTimeInterval, previousPosition: Double) {
        let next = activeLineIndex
        let nextContent: IslandLyricContent
        if let track, next >= 0 {
            nextContent = .line(track.lyrics[next], translation: settings.showTranslation)
        } else {
            let status = playback?.status ?? ""
            nextContent = .status(
                title: isAttention ? "播放信息暂不可用" : (track?.title ?? (status.isEmpty ? "等待播放信息" : status)),
                subtitle: statusSubtitle(track: track, status: status))
        }
        guard nextContent != lyricContent else {
            lineIndex = next
            return
        }
        if capsuleProgress > 0.1 {
            window.effectiveAppearance.performAsCurrentDrawingAppearance {
                outgoingLyrics = lyricRenderer.bitmap(
                    viewport: lyricViewport,
                    scale: settings.scale,
                    backingScale: window.backingScaleFactor) { viewport in
                        drawLyrics(in: viewport, positionMs: previousPosition)
                    }
            }
        } else {
            outgoingLyrics = nil
        }
        transitionReversed = next >= 0 && lineIndex >= 0 && next < lineIndex
        lineIndex = next
        lyricContent = nextContent
        lineChangedAt = now
        switch nextContent {
        case let .line(line, _): island.setAccessibilityLabel(line.text)
        case let .status(title, subtitle): island.setAccessibilityLabel("\(title)，\(subtitle)")
        }
    }

    private func layoutSurface() {
        let backing = window.backingScaleFactor
        let width = max(1, (displayWidth * backing / 2).rounded() * 2 / backing)
        let height = max(1, (displayHeight * backing / 2).rounded() * 2 / backing)
        let centerX = (window.frame.width * backing / 2).rounded() / backing
        let centerY = ((window.frame.height / 2 - 2) * backing).rounded() / backing
        let pill = NSRect(
            x: centerX - width / 2,
            y: centerY - height / 2,
            width: width,
            height: height)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        if surface.frame != pill {
            surface.frame = pill
            surface.applyCornerRadius(height / 2)
            surface.layoutSubtreeIfNeeded()
        }
        if surface.alphaValue != capsuleProgress { surface.alphaValue = capsuleProgress }
        let hidden = capsuleProgress < 0.001
        if surface.isHidden != hidden { surface.isHidden = hidden }
        CATransaction.commit()
        island.sidesProgress = sidesProgress
    }

    private func updateMouseInteraction() {
        guard settings.showIsland,
              !settings.clickThrough,
              !tempHideActive,
              capsuleProgress > 0.2 else {
            SetHover(nil)
            if !window.ignoresMouseEvents { window.ignoresMouseEvents = true }
            return
        }
        let mouse = NSEvent.mouseLocation
        let windowPoint = window.convertPoint(fromScreen: mouse)
        let local = island.convert(windowPoint, from: nil)
        let interactive = pressPoint != nil || islandCapsuleContains(local, in: island.bounds)
        if window.ignoresMouseEvents == interactive { window.ignoresMouseEvents = !interactive }
        guard interactive else {
            SetHover(nil)
            return
        }
        if !didShowLogged {
            didShowLogged = true
            log("first interactive frame \(Int(island.bounds.width))x\(Int(island.bounds.height))")
        }
        SetHover(island.side(at: local))
    }

    private func SetHover(_ side: Side?) {
        if side != sideHover {
            sideHover = side
            island.needsDisplay = true
        }
    }

    private func hideTemporarily(seconds: Int? = nil) {
        cancelPendingClick()
        let duration = seconds ?? settings.temporaryHideSeconds
        temporarilyHiddenUntil = CACurrentMediaTime() + Double(max(1, duration))
        window.ignoresMouseEvents = true
        island.needsDisplay = true
    }

    private func sendAction(_ action: String, side: Side? = nil, value: Double? = nil, trackID: String? = nil) {
        var message: [String: Any] = [
            "type": "action",
            "action": action
        ]
        if let side {
            message["side"] = side == .left ? "left" : "right"
        }
        if let trackId = trackID ?? playback?.trackId {
            message["trackId"] = trackId
        }
        if let value {
            message["value"] = value
        }
        send(message)
    }

    private func actionEnabled(_ icon: IslandIcon) -> Bool {
        guard let playback else { return false }
        switch icon {
        case .none:
            return false
        case .lyricsWindow, .temporaryHide:
            return true
        case .favorite:
            return playback.canFavorite && !playback.busy
        case .playPause:
            return playback.canPlayPause && !playback.busy
        case .previous:
            return playback.canPrevious && !playback.busy
        case .next:
            return playback.canNext && !playback.busy
        case .banner:
            return playback.trackId != nil
        }
    }

    private func triggerSide(_ side: Side) {
        let icon = side == .left ? settings.leftIconAction : settings.rightIconAction
        guard actionEnabled(icon) else { return }
        switch icon {
        case .temporaryHide:
            hideTemporarily()
        case .favorite:
            sendAction("side", side: side, value: playback?.isFavorite == true ? 0 : 1)
        case .playPause:
            retainPauseIfNeeded(collapseSides: false)
            sendAction("side", side: side)
        default:
            sendAction("side", side: side)
        }
    }

    private func cancelPendingClick() {
        pendingClick?.cancel()
        pendingClick = nil
    }

    fileprivate func mouseDown(_ event: NSEvent, in view: IslandView) {
        cancelPendingClick()
        let point = view.convert(event.locationInWindow, from: nil)
        guard islandCapsuleContains(point, in: view.bounds) else { return }
        pressPoint = point
        let screenPoint = NSEvent.mouseLocation
        dragPressScreen = screenPoint
        dragMouseOffset = NSPoint(
            x: screenPoint.x - window.frame.origin.x,
            y: screenPoint.y - window.frame.origin.y)
        dragged = false
        dragScreen = nil
        pressedSide = view.side(at: point)
        pressedTrackID = playback?.trackId
        if event.clickCount >= 2, pressedSide == nil {
            cancelPendingClick()
            hideTemporarily()
            pressedSide = nil
            suppressMouseUpClick = true
        }
    }

    fileprivate func mouseDragged(_ event: NSEvent) {
        guard let dragPressScreen, let dragMouseOffset else { return }
        let screenPoint = NSEvent.mouseLocation
        if !dragged,
           hypot(screenPoint.x - dragPressScreen.x, screenPoint.y - dragPressScreen.y) < 4 {
            return
        }
        dragged = true
        pressedSide = nil
        guard !settings.lockPosition else { return }
        let proposed = NSPoint(
            x: screenPoint.x - dragMouseOffset.x,
            y: screenPoint.y - dragMouseOffset.y)
        if let screen = screenForMouse(screenPoint) {
            dragScreen = screen
            window.setFrameOrigin(constrainedOrigin(proposed, on: screen))
        } else {
            window.setFrameOrigin(proposed)
        }
    }

    fileprivate func mouseUp(_ event: NSEvent) {
        let point = island.convert(event.locationInWindow, from: nil)
        let side = pressedSide
        let trackID = pressedTrackID
        pressedSide = nil
        pressedTrackID = nil
        pressPoint = nil
        dragMouseOffset = nil
        dragPressScreen = nil
        if suppressMouseUpClick {
            suppressMouseUpClick = false
            return
        }
        if dragged {
            dragged = false
            applyWindowPlacement(on: dragScreen, preserveOrigin: true)
            dragScreen = nil
            constrainWindowToScreen()
            installTimer()
            savePosition()
            island.needsDisplay = true
            return
        }
        guard capsuleVisible, islandCapsuleContains(point, in: island.bounds), trackID == playback?.trackId else { return }
        if let side {
            if island.side(at: point) == side { triggerSide(side) }
            return
        }
        if island.side(at: point) != nil {
            return
        }
        cancelPendingClick()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.pendingClick = nil
            self.activateCenter(trackID: trackID)
        }
        pendingClick = work
        let delay = NSEvent.doubleClickInterval
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    fileprivate func scroll(_ event: NSEvent) {
        let ended = event.phase.contains(.ended) || event.phase.contains(.cancelled)
        guard abs(event.scrollingDeltaY) > 0.1 || ended else { return }
        guard scrollGate.accept(
            at: CACurrentMediaTime(), phased: !event.phase.isEmpty, ended: ended,
            momentum: !event.momentumPhase.isEmpty), !isAttention else { return }
        cancelPendingClick()
        sendAction(event.scrollingDeltaY > 0 ? "previous" : "next")
    }

    private func savePosition() {
        let origin = window.frame.origin
        let display = screenForCurrentWindow().map {
            String(($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0)
        }
        send([
            "type": "action",
            "action": "position",
            "x": origin.x,
            "y": origin.y,
            "displayId": display ?? ""
        ])
    }

    fileprivate func showMenu(_ event: NSEvent, in view: NSView) {
        cancelPendingClick()
        menuTrackID = playback?.trackId
        defer { menuTrackID = nil }
        let menu = NSMenu(title: "Lyricify Island")
        menu.autoenablesItems = false
        addItem(menu, "打开歌词窗口", #selector(openLyrics))
        menu.addItem(.separator())
        addItem(menu, "隐藏 \(settings.temporaryHideSeconds) 秒", #selector(hideMenu))
        addItem(menu, "复制歌曲分享图…", #selector(openBanner), enabled: menuTrackID != nil)
        if settings.enableTrackOffsets, playback?.trackId != nil {
            menu.addItem(.separator())
            addItem(menu, "歌词提前 100 ms", #selector(offsetEarlier))
            addItem(menu, "歌词延后 100 ms", #selector(offsetLater))
            if settings.trackOffsetMs != 0 {
                let title = settings.trackOffsetMs > 0
                    ? "归零（+\(settings.trackOffsetMs) ms）"
                    : "归零（\(settings.trackOffsetMs) ms）"
                addItem(menu, title, #selector(offsetReset))
            }
        }
        menu.addItem(.separator())
        addItem(menu, "设置…", #selector(openSettings))
        addItem(menu, "退出", #selector(quit))
        menu.popUp(
            positioning: nil,
            at: view.convert(event.locationInWindow, from: nil),
            in: view)
    }

    private func addItem(_ menu: NSMenu, _ title: String, _ action: Selector, enabled: Bool = true) {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        item.isEnabled = enabled
        menu.addItem(item)
    }

    @objc private func openLyrics() { sendAction("lyricsWindow") }
    @objc private func hideMenu() { hideTemporarily() }
    @objc private func openBanner() {
        guard let menuTrackID, menuTrackID == playback?.trackId else { return }
        sendAction("banner", trackID: menuTrackID)
    }
    @objc private func openSettings() { sendAction("settings") }
    @objc private func quit() { sendAction("quit") }
    @objc private func offsetEarlier() { sendOffset(100) }
    @objc private func offsetLater() { sendOffset(-100) }
    @objc private func offsetReset() { sendOffset(-settings.trackOffsetMs) }

    private func sendOffset(_ delta: Int) {
        guard let menuTrackID, menuTrackID == playback?.trackId else { return }
        send([
            "type": "action",
            "action": "offset",
            "delta": delta,
            "trackId": menuTrackID
        ])
    }

    fileprivate func drawIsland(in view: IslandView, rect: NSRect) {
        guard capsuleProgress > 0.002 else { return }
        #if DEBUG
        let drawStarted = CACurrentMediaTime()
        defer {
            if visualTestStartedAt > 0 {
                let duration = (CACurrentMediaTime() - drawStarted) * 1000
                visualDrawTimes.append(duration)
                if duration > 16.7 {
                    visualSlowFrames.append([
                        "time": frameTime - visualTestStartedAt, "durationMs": duration,
                        "lineIndex": lineIndex, "transitionElapsed": frameTime - lineChangedAt
                    ])
                }
            }
        }
        #endif
        let scale = settings.scale
        let sides = CGFloat(sidesProgress)
        let avatar = NSRect(x: 18 * scale, y: rect.midY - 20 * scale, width: 40 * scale, height: 40 * scale)
        let album = NSRect(x: rect.maxX - 58 * scale, y: rect.midY - 20 * scale, width: 40 * scale, height: 40 * scale)

        if sides > 0.01 {
            drawAvatarOrAction(in: view, rect: avatar, side: .left, visibility: sides)
            drawAlbumOrAction(in: view, rect: album, side: .right, visibility: sides)
        }

        let textInset = 28 * scale + 48 * scale * sides
        let clip = NSRect(
            x: rect.minX + textInset,
            y: rect.minY + 7 * scale,
            width: max(1, rect.width - textInset * 2),
            height: rect.height - 14 * scale)
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(rect: clip).addClip()

        let viewport = NSRect(
            x: rect.midX - lyricViewport.width / 2,
            y: rect.midY - lyricViewport.height / 2,
            width: lyricViewport.width,
            height: lyricViewport.height)
        drawLyrics(in: viewport, positionMs: framePositionMs)
        NSGraphicsContext.restoreGraphicsState()
    }

    private func drawLyrics(in viewport: NSRect, positionMs: Double) {
        let scale = settings.scale
        if let outgoingLyrics {
            let phase = IslandLyricPhase(
                elapsed: frameTime - lineChangedAt,
                reversed: transitionReversed,
                reduceMotion: reduceMotion)
            lyricRenderer.draw(
                outgoingLyrics,
                centeredAt: NSPoint(x: viewport.midX, y: viewport.midY + phase.outgoingOffset * scale),
                opacity: phase.outgoingOpacity,
                blur: phase.outgoingBlur * scale)
            if phase.incomingBlur > 0.05,
               let incoming = lyricRenderer.snapshot(
                lyricContent,
                viewport: viewport.size,
                scale: scale,
                positionMs: positionMs,
                backingScale: window.backingScaleFactor) {
                lyricRenderer.draw(
                    incoming,
                    centeredAt: NSPoint(x: viewport.midX, y: viewport.midY + phase.incomingOffset * scale),
                    opacity: phase.incomingOpacity,
                    blur: phase.incomingBlur * scale)
            } else {
                drawCurrentLyrics(
                    in: viewport.offsetBy(dx: 0, dy: phase.incomingOffset * scale),
                    opacity: phase.incomingOpacity,
                    positionMs: positionMs)
            }
        } else {
            drawCurrentLyrics(in: viewport, opacity: 1, positionMs: positionMs)
        }
    }

    private func drawCurrentLyrics(in viewport: NSRect, opacity: CGFloat, positionMs: Double) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.saveGState()
        context.setAlpha(opacity)
        lyricRenderer.draw(lyricContent, in: viewport, scale: settings.scale, positionMs: positionMs)
        context.restoreGState()
    }

    private func updateArtwork(_ now: CFTimeInterval) {
        let candidate = avatarImage()
        if avatar !== candidate {
            previousAvatar = avatar
            avatar = candidate
            avatarChangedAt = now
        }
        if album !== track?.albumArt {
            previousAlbum = album
            album = track?.albumArt
            albumChangedAt = now
        }
        if now - avatarChangedAt >= 0.24 { previousAvatar = nil }
        if now - albumChangedAt >= 0.24 { previousAlbum = nil }
    }

    private func drawArtwork(
        _ image: NSImage?,
        previous: NSImage?,
        changedAt: Double,
        in rect: NSRect,
        alpha: CGFloat
    ) {
        let duration = reduceMotion ? 0.12 : 0.24
        let progress = CGFloat(islandEaseOut((frameTime - changedAt) / duration))
        if let previous, progress < 1 {
            drawAspectFill(previous, in: rect, alpha: (1 - progress) * alpha)
        }
        if let image {
            drawAspectFill(image, in: rect, alpha: progress * alpha)
        } else {
            NSColor.labelColor.withAlphaComponent(0.08 * alpha).setFill()
            rect.fill()
            drawSymbol("music.note", in: rect, color: NSColor.secondaryLabelColor.withAlphaComponent(alpha))
        }
    }

    private func drawAvatarOrAction(
        in view: IslandView,
        rect: NSRect,
        side: Side,
        visibility: CGFloat
    ) {
        let hover = sideHover == side
        let progress = view.hoverProgress(side: side)
        let avatarProgress = CGFloat(1 - progress)
        if avatarProgress > 0.01 {
            let scaled = rect.insetBy(dx: rect.width * (1 - avatarProgress) * 0.11,
                                      dy: rect.height * (1 - avatarProgress) * 0.11)
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(ovalIn: scaled).addClip()
            drawArtwork(
                avatar,
                previous: previousAvatar,
                changedAt: avatarChangedAt,
                in: scaled,
                alpha: avatarProgress * visibility)
            NSGraphicsContext.restoreGraphicsState()
        }
        if progress > 0.01 || hover {
            drawAction(
                in: rect,
                icon: settings.leftIconAction,
                progress: progress,
                side: side,
                visibility: visibility)
        }
    }

    private func drawAlbumOrAction(
        in view: IslandView,
        rect: NSRect,
        side: Side,
        visibility: CGFloat
    ) {
        let hover = sideHover == side
        let progress = view.hoverProgress(side: side)
        let albumProgress = CGFloat(1 - progress)
        if albumProgress > 0.01 {
            let scaled = rect.insetBy(dx: rect.width * (1 - albumProgress) * 0.11,
                                      dy: rect.height * (1 - albumProgress) * 0.11)
            NSGraphicsContext.saveGraphicsState()
            let radius = min(8 * settings.scale, scaled.width / 2)
            NSBezierPath(roundedRect: scaled, xRadius: radius, yRadius: radius).addClip()
            drawArtwork(
                album,
                previous: previousAlbum,
                changedAt: albumChangedAt,
                in: scaled,
                alpha: albumProgress * visibility)
            NSGraphicsContext.restoreGraphicsState()
        }
        if progress > 0.01 || hover {
            drawAction(
                in: rect,
                icon: settings.rightIconAction,
                progress: progress,
                side: side,
                visibility: visibility)
        }
    }

    private func drawAction(
        in rect: NSRect,
        icon: IslandIcon,
        progress: CGFloat,
        side: Side,
        visibility: CGFloat
    ) {
        guard icon != .none else { return }
        let active = icon.isActive(playing: isPlaying, favorite: playback?.isFavorite == true)
        let enabled = actionEnabled(icon)
        NSGraphicsContext.saveGraphicsState()
        let scale = 0.94 + 0.06 * progress
        let target = rect.insetBy(
            dx: rect.width * (1 - scale) / 2,
            dy: rect.height * (1 - scale) / 2)
        NSColor.labelColor.withAlphaComponent((pressedSide == side ? 0.16 : 0.08) * progress * visibility).setFill()
        NSBezierPath(ovalIn: target).fill()
        let alpha = (enabled ? 0.94 : 0.32) * progress * visibility
        let symbol: String
        switch icon {
        case .favorite:
            symbol = active ? "checkmark.circle.fill" : "plus.circle"
        case .playPause:
            symbol = active ? "pause.fill" : "play.fill"
        case .lyricsWindow:
            symbol = "text.alignleft"
        case .previous:
            symbol = "backward.fill"
        case .next:
            symbol = "forward.fill"
        case .banner:
            symbol = "photo"
        case .temporaryHide:
            symbol = "eye.slash"
        case .none:
            symbol = "circle"
        }
        drawSymbol(symbol, in: target, color: NSColor.labelColor.withAlphaComponent(alpha))
        NSGraphicsContext.restoreGraphicsState()
    }

    private func avatarImage() -> NSImage? {
        guard let track else { return appIcon }
        let trackAge = max(0, frameTime - trackChangedAt)
        guard !track.artistImages.isEmpty, trackAge > 1.05 else {
            return appIcon
        }
        let elapsed = trackAge - 1.05
        let slot = Int(elapsed / 3.0)
        if slot >= track.artistImages.count {
            return appIcon
        }
        return track.artistImages[slot]
    }

    private func drawAspectFill(_ image: NSImage, in rect: NSRect, alpha: CGFloat) {
        let imageSize = image.size
        guard imageSize.width > 0, imageSize.height > 0 else { return }
        let targetRatio = rect.width / rect.height
        let imageRatio = imageSize.width / imageSize.height
        var source = NSRect(origin: .zero, size: imageSize)
        if imageRatio > targetRatio {
            source.size.width = imageSize.height * targetRatio
            source.origin.x = (imageSize.width - source.width) / 2
        } else {
            source.size.height = imageSize.width / targetRatio
            source.origin.y = (imageSize.height - source.height) / 2
        }
        image.draw(
            in: rect,
            from: source,
            operation: .sourceOver,
            fraction: alpha,
            respectFlipped: true,
            hints: [.interpolation: NSImageInterpolation.high])
    }

    private func drawSymbol(_ name: String, in rect: NSRect, color: NSColor) {
        guard let image = NSImage(systemSymbolName: name, accessibilityDescription: nil) else {
            return
        }
        let configured = image.withSymbolConfiguration(
            NSImage.SymbolConfiguration(pointSize: rect.height * 0.48, weight: .semibold)
                .applying(.init(paletteColors: [color])))
            ?? image
        let size = configured.size
        let target = NSRect(
            x: rect.midX - size.width / 2,
            y: rect.midY - size.height / 2,
            width: size.width,
            height: size.height)
        configured.draw(
            in: target,
            from: .zero,
            operation: .sourceOver,
            fraction: 1,
            respectFlipped: true,
            hints: [.interpolation: NSImageInterpolation.high])
    }

    fileprivate var currentSettings: LoadedSettings { settings }
    fileprivate var currentPlayback: LoadedPlayback? { playback }
    fileprivate var currentSideHover: Side? { sideHover }

    private func retainPauseIfNeeded(collapseSides: Bool) {
        if isPlaying, let trackID = playback?.trackId {
            pauseRetention.begin(trackID: trackID, collapseSides: collapseSides)
        }
    }

    fileprivate func activateCenter(trackID: String?) {
        guard capsuleVisible, !settings.clickThrough, !isAttention,
              trackID == playback?.trackId, playback?.canPlayPause == true,
              playback?.busy == false else { return }
        retainPauseIfNeeded(collapseSides: true)
        sendAction("playPause", trackID: trackID)
    }
}

private final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

private final class IslandView: NSView {
    weak var controller: AppController?
    var sidesProgress: Double = 1
    private var hoverProgressLeft = 0.0
    private var hoverProgressRight = 0.0

    override var isFlipped: Bool { true }
    override var isOpaque: Bool { false }
    override var acceptsFirstResponder: Bool { false }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func accessibilityRole() -> NSAccessibility.Role? { .button }
    override func isAccessibilityElement() -> Bool { true }
    override func accessibilityHelp() -> String? { "单击播放或暂停，双击临时隐藏，右键打开菜单" }
    override func accessibilityPerformPress() -> Bool {
        guard let controller else { return false }
        controller.activateCenter(trackID: controller.currentPlayback?.trackId)
        return true
    }

    override func hitTest(_ point: NSPoint) -> NSView? {
        islandCapsuleContains(point, in: bounds) ? self : nil
    }

    func side(at point: NSPoint) -> Side? {
        guard sidesProgress > 0.52, bounds.width > 0, bounds.height > 0 else { return nil }
        let scale = controller?.currentSettings.scale ?? 1
        let left = NSRect(x: 12 * scale, y: 0, width: 60 * scale, height: bounds.height)
        let album = NSRect(x: bounds.maxX - 68 * scale, y: 0, width: 62 * scale, height: bounds.height)
        if left.contains(point) { return .left }
        if album.contains(point) { return .right }
        return nil
    }

    func hoverProgress(side: Side) -> Double {
        side == .left ? hoverProgressLeft : hoverProgressRight
    }

    func advanceHover(dt: Double, reduceMotion: Bool) {
        guard let controller else { return }
        let targetHover = controller.currentSideHover
        let blend = 1 - exp(-dt * (reduceMotion ? 30 : 22))
        hoverProgressLeft += ((targetHover == .left ? 1 : 0) - hoverProgressLeft) * blend
        hoverProgressRight += ((targetHover == .right ? 1 : 0) - hoverProgressRight) * blend
        if abs(hoverProgressLeft - (targetHover == .left ? 1 : 0)) < 0.001 {
            hoverProgressLeft = targetHover == .left ? 1 : 0
        }
        if abs(hoverProgressRight - (targetHover == .right ? 1 : 0)) < 0.001 {
            hoverProgressRight = targetHover == .right ? 1 : 0
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        controller?.drawIsland(in: self, rect: bounds)
    }

    override func mouseDown(with event: NSEvent) {
        controller?.mouseDown(event, in: self)
    }

    override func mouseDragged(with event: NSEvent) {
        controller?.mouseDragged(event)
    }

    override func mouseUp(with event: NSEvent) {
        controller?.mouseUp(event)
    }

    override func scrollWheel(with event: NSEvent) {
        controller?.scroll(event)
    }

    override func rightMouseDown(with event: NSEvent) {
        controller?.showMenu(event, in: self)
    }
}

@main
private struct LyricifyIslandGlassMain {
    @MainActor
    static func main() {
        let app = NSApplication.shared
        let controller = AppController()
        app.delegate = controller
        app.setActivationPolicy(.accessory)
        app.run()
    }
}
