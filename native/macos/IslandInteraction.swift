import Foundation
import CoreGraphics

func islandCapsuleContains(_ point: CGPoint, in bounds: CGRect) -> Bool {
    guard bounds.width > 0, bounds.height > 0, bounds.contains(point) else { return false }
    let radius = min(bounds.width, bounds.height) / 2
    let centerX = min(max(point.x, bounds.minX + radius), bounds.maxX - radius)
    let centerY = min(max(point.y, bounds.minY + radius), bounds.maxY - radius)
    return hypot(point.x - centerX, point.y - centerY) <= radius
}

enum IslandIcon: String {
    case none = "None"
    case favorite = "Favorite"
    case lyricsWindow = "LyricsWindow"
    case playPause = "PlayPause"
    case previous = "Previous"
    case next = "Next"
    case banner = "Banner"
    case temporaryHide = "TemporaryHide"

    func isActive(playing: Bool, favorite: Bool) -> Bool {
        switch self {
        case .favorite: return favorite
        case .playPause: return playing
        default: return false
        }
    }
}

struct IslandPauseRetention {
    private(set) var trackID: String?
    private(set) var collapseSides = true
    private var observedPause = false

    mutating func begin(trackID: String, collapseSides: Bool) {
        self.trackID = trackID
        self.collapseSides = collapseSides
        observedPause = false
    }

    mutating func receive(trackID: String?, playing: Bool) {
        guard self.trackID != nil else { return }
        if self.trackID != trackID || (playing && observedPause) {
            cancel()
        } else if !playing {
            observedPause = true
        }
    }

    mutating func cancel() {
        trackID = nil
        observedPause = false
        collapseSides = true
    }
}

struct IslandScrollGate {
    private var gestureActive = false
    private var lastAcceptedAt = -Double.infinity

    mutating func accept(at time: Double, phased: Bool, ended: Bool, momentum: Bool) -> Bool {
        if ended {
            gestureActive = false
            return false
        }
        guard !momentum else { return false }
        if phased {
            guard !gestureActive else { return false }
            gestureActive = true
        }
        guard time - lastAcceptedAt >= 0.35 else { return false }
        lastAcceptedAt = time
        return true
    }
}
