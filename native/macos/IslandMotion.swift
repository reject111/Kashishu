import Foundation

func islandEaseOut(_ value: Double) -> Double {
    let progress = min(max(value, 0), 1)
    return 1 - pow(1 - progress, 3)
}

// Same timing as the Linux reference renderer, in milliseconds.
func islandGlyphLift(position: Double, start: Double, end: Double, syllableDuration: Double, wave: Bool) -> Double {
    guard position > start else { return 0 }
    if !wave { return 1 - exp(-(position - start) / 650) }
    guard end > start else { return 0 }
    if position <= end {
        return 1 - pow(1 - min(max((position - start) / (end - start), 0), 1), 2.2)
    }
    let duration = syllableDuration > 0 ? syllableDuration : end - start
    let hold = min(max(duration * 0.35, 90), 200)
    let release = min(max(duration * 0.95, 320), 560)
    let elapsed = position - end - hold
    if elapsed <= 0 { return 1 }
    if elapsed >= release { return 0 }
    return 0.5 * (1 + cos(elapsed / release * .pi))
}

struct IslandPlaybackClock {
    private var trackID: String?
    private var anchorMs = 0.0
    private var anchoredAt = 0.0
    private var correctionMs = 0.0
    private var correctionDuration = 0.35
    private var playing = false
    private var initialized = false
    private(set) var discontinuity = 0

    mutating func receive(trackID: String?, positionMs: Double, playing: Bool, at time: Double) {
        guard positionMs.isFinite, time.isFinite else { return }
        let position = max(0, positionMs)
        let predicted = self.position(at: time)
        let error = position - predicted
        let reset = !initialized || self.trackID != trackID || self.playing != playing
            || abs(error) > 600
        if reset {
            anchorMs = position
            correctionMs = 0
            discontinuity += 1
        } else if playing {
            anchorMs = predicted
            correctionMs = error
            correctionDuration = max(0.35, abs(error) / 120)
        } else {
            anchorMs = position
            correctionMs = 0
        }
        self.trackID = trackID
        self.playing = playing
        anchoredAt = time
        initialized = true
    }

    func position(at time: Double) -> Double {
        guard initialized else { return 0 }
        let elapsed = max(0, time - anchoredAt)
        let correction = correctionMs * min(elapsed / correctionDuration, 1)
        return max(0, anchorMs + (playing ? elapsed * 1000 : 0) + correction)
    }
}

struct IslandTween {
    private var origin: Double
    private var target: Double
    private var startedAt = 0.0
    private var duration = 0.0

    init(_ value: Double) {
        origin = value
        target = value
    }

    mutating func move(to value: Double, at time: Double, duration: Double) {
        guard value != target else {
            if duration <= 0 {
                origin = target
                self.duration = 0
            }
            return
        }
        origin = self.value(at: time)
        target = value
        startedAt = time
        self.duration = max(0, duration)
    }

    func value(at time: Double) -> Double {
        guard duration > 0, time < startedAt + duration else { return target }
        let progress = islandEaseOut((time - startedAt) / duration)
        return origin + (target - origin) * progress
    }

    func isAnimating(at time: Double) -> Bool {
        duration > 0 && time < startedAt + duration
    }
}

struct IslandLyricPhase {
    let outgoingOpacity: Double
    let incomingOpacity: Double
    let outgoingOffset: Double
    let incomingOffset: Double
    let outgoingBlur: Double
    let incomingBlur: Double
    let complete: Bool

    init(elapsed: Double, reversed: Bool, reduceMotion: Bool) {
        let duration = reduceMotion ? 0.12 : 0.34
        let direction = reversed ? -1.0 : 1.0
        let outgoing = min(max(elapsed / (reduceMotion ? duration : 0.20), 0), 1)
        let incoming = islandEaseOut(elapsed / (reduceMotion ? duration : 0.24))
        let movement = islandEaseOut(elapsed / duration)
        outgoingOpacity = 1 - outgoing
        incomingOpacity = incoming
        outgoingOffset = reduceMotion ? 0 : -10 * islandEaseOut(outgoing) * direction
        incomingOffset = reduceMotion ? 0 : 12 * (1 - movement) * direction
        outgoingBlur = reduceMotion ? 0 : 3.4 * outgoing
        incomingBlur = reduceMotion ? 0 : 2.4 * (1 - islandEaseOut(elapsed / 0.16))
        complete = elapsed >= duration
    }
}
