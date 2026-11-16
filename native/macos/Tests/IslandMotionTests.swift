import Foundation
import CoreGraphics

@main
struct IslandMotionTests {
    static func main() {
        testClockJitter()
        testSeekAndPause()
        testFiniteLayout()
        testTransitionContinuity()
        testPauseRetention()
        testIconStates()
        testGlyphLift()
        testScrollGate()
        testCapsuleHitTesting()
        print("native motion tests: ok")
    }

    static func testClockJitter() {
        var clock = IslandPlaybackClock()
        clock.receive(trackID: "track", positionMs: 0, playing: true, at: 0)
        var previous = 0.0
        var boundaryCrossings = 0
        for frame in 1...600 {
            let time = Double(frame) / 60
            if frame.isMultiple(of: 3) {
                clock.receive(
                    trackID: "track", positionMs: time * 1000 + (frame.isMultiple(of: 2) ? -90 : 90),
                    playing: true, at: time)
            }
            let position = clock.position(at: time)
            precondition(position >= previous, "Small timing corrections must never rewind lyrics")
            precondition(position - previous <= 20, "Timing corrections must not jump between frames")
            if previous < 2000 && position >= 2000 { boundaryCrossings += 1 }
            previous = position
        }
        precondition(boundaryCrossings == 1, "A lyric boundary must be entered exactly once")
        precondition(clock.discontinuity == 1, "Jitter must not be treated as seeking")
    }

    static func testSeekAndPause() {
        var clock = IslandPlaybackClock()
        clock.receive(trackID: "first", positionMs: 5000, playing: true, at: 1)
        clock.receive(trackID: "first", positionMs: 1200, playing: true, at: 2)
        precondition(clock.position(at: 2) == 1200, "Backward seeks must be immediate")
        clock.receive(trackID: "first", positionMs: 1600, playing: false, at: 2.4)
        precondition(clock.position(at: 100) == 1600, "Paused lyrics must remain stationary")
        clock.receive(trackID: "second", positionMs: 0, playing: true, at: 101)
        precondition(clock.position(at: 101) == 0, "A new track must reset its clock")
        let valid = clock.position(at: 102)
        clock.receive(trackID: "second", positionMs: .nan, playing: true, at: 102)
        precondition(clock.position(at: 102) == valid, "Invalid samples must not corrupt the clock")
    }

    static func testFiniteLayout() {
        var width = IslandTween(600)
        width.move(to: 360, at: 1, duration: 0.34)
        let middle = width.value(at: 1.12)
        width.move(to: 360, at: 1.12, duration: 0.34)
        precondition(width.value(at: 1.12) == middle, "Duplicate targets must not restart layout")
        precondition(width.value(at: 1.34) == 360 && width.value(at: 100) == 360,
                     "Layout must settle exactly instead of drifting indefinitely")
        let interrupted = width.value(at: 1.2)
        width.move(to: 620, at: 1.2, duration: 0.34)
        precondition(width.value(at: 1.2) == interrupted, "Interrupted layout must remain continuous")
        width.move(to: 400, at: 2, duration: 0)
        precondition(width.value(at: 2) == 400, "Reduce Motion layout must settle immediately")
        width.move(to: 600, at: 3, duration: 0.34)
        width.move(to: 600, at: 3.1, duration: 0)
        precondition(width.value(at: 3.1) == 600, "Enabling Reduce Motion must cancel an in-flight layout")
    }

    static func testTransitionContinuity() {
        for frame in 0...34 {
            let time = Double(frame) / 100
            let normal = IslandLyricPhase(elapsed: time, reversed: false, reduceMotion: false)
            let reversed = IslandLyricPhase(elapsed: time, reversed: true, reduceMotion: false)
            precondition(normal.outgoingOpacity + normal.incomingOpacity >= 0.78,
                         "Crossfading lyrics must not leave a blank frame")
            precondition(normal.incomingOffset == -reversed.incomingOffset, "Backward seeks must reverse motion")
        }
        let final = IslandLyricPhase(elapsed: 0.34, reversed: false, reduceMotion: false)
        precondition(final.complete && final.incomingOffset == 0 && final.incomingBlur == 0)
        let reduced = IslandLyricPhase(elapsed: 0.06, reversed: false, reduceMotion: true)
        precondition(reduced.outgoingBlur == 0 && reduced.incomingBlur == 0
                     && reduced.outgoingOffset == 0 && reduced.incomingOffset == 0)
    }

    static func testPauseRetention() {
        var retention = IslandPauseRetention()
        retention.begin(trackID: "first", collapseSides: true)
        retention.receive(trackID: "first", playing: true)
        precondition(retention.trackID == "first", "Busy feedback before pausing must preserve retention")
        retention.receive(trackID: "first", playing: false)
        precondition(retention.trackID == "first" && retention.collapseSides, "Center pause must retain a narrow island")
        retention.receive(trackID: "first", playing: true)
        precondition(retention.trackID == nil, "Resuming must clear retention")
        retention.begin(trackID: "first", collapseSides: false)
        retention.receive(trackID: "first", playing: false)
        precondition(!retention.collapseSides, "Side-button pause must keep side actions available")
        retention.receive(trackID: "second", playing: false)
        precondition(retention.trackID == nil, "Changing tracks must clear retention")
        retention.begin(trackID: "second", collapseSides: true)
        retention.cancel()
        precondition(retention.trackID == nil, "Failed commands must clear retention")
    }

    static func testIconStates() {
        precondition(!IslandIcon.favorite.isActive(playing: true, favorite: false))
        precondition(IslandIcon.favorite.isActive(playing: false, favorite: true))
        precondition(IslandIcon.playPause.isActive(playing: true, favorite: false))
        precondition(!IslandIcon.playPause.isActive(playing: false, favorite: true))
        precondition(!IslandIcon.lyricsWindow.isActive(playing: true, favorite: true))
        precondition(IslandIcon(rawValue: "CopyLink") == .copyLink)
    }

    static func testGlyphLift() {
        func lift(_ position: Double, wave: Bool) -> Double {
            islandGlyphLift(position: position, start: 100, end: 500, syllableDuration: 400, wave: wave)
        }
        precondition(lift(100, wave: false) == 0 && lift(100, wave: true) == 0)
        precondition(abs(lift(750, wave: false) - (1 - exp(-1))) < 0.000001,
                     "Stay mode must match the reference renderer's 650 ms rise")
        precondition(lift(500, wave: true) == 1 && lift(640, wave: true) == 1,
                     "Wave mode must hold at the end of the sung syllable")
        precondition(lift(1020, wave: true) == 0 && lift(2000, wave: true) == 0,
                     "Wave mode must return exactly to the baseline")
        var previous = 0.0
        for position in 101...2000 {
            let current = lift(Double(position), wave: true)
            precondition(current >= 0 && current <= 1 && abs(current - previous) < 0.01,
                         "Glyph lift must remain bounded and continuous")
            previous = current
        }
    }

    static func testScrollGate() {
        var gate = IslandScrollGate()
        precondition(gate.accept(at: 0, phased: true, ended: false, momentum: false))
        precondition(!gate.accept(at: 0.5, phased: true, ended: false, momentum: false), "One trackpad gesture must skip once")
        precondition(!gate.accept(at: 0.6, phased: true, ended: true, momentum: false))
        precondition(!gate.accept(at: 0.8, phased: false, ended: false, momentum: true), "Momentum must not skip tracks")
        precondition(gate.accept(at: 1, phased: true, ended: false, momentum: false))
        precondition(!gate.accept(at: 1.1, phased: true, ended: true, momentum: false))
        precondition(!gate.accept(at: 1.2, phased: false, ended: false, momentum: false))
        precondition(gate.accept(at: 1.5, phased: false, ended: false, momentum: false))
    }

    static func testCapsuleHitTesting() {
        let bounds = CGRect(x: 0, y: 0, width: 400, height: 80)
        precondition(islandCapsuleContains(CGPoint(x: 200, y: 40), in: bounds))
        precondition(islandCapsuleContains(CGPoint(x: 1, y: 40), in: bounds))
        precondition(!islandCapsuleContains(CGPoint(x: 1, y: 1), in: bounds), "Transparent capsule corners must pass clicks through")
        precondition(!islandCapsuleContains(CGPoint(x: 399, y: 1), in: bounds))
        precondition(!islandCapsuleContains(CGPoint(x: -1, y: 40), in: bounds))
        precondition(!islandCapsuleContains(.zero, in: .zero))
    }
}
