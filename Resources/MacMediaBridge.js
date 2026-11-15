// Persistent AppleScript bridge used by MacMediaService.
//
// macOS blocks the private MediaRemote "now playing" API on current releases, so the local
// playback source talks to the two scriptable players instead: Spotify and Music. Running
// osascript once per poll costs a process launch every time, so this script stays alive and
// reads one request per line from stdin while answering with one JSON line on stdout.
//
// Requests:
//   poll
//   cmd {"source":"spotify"|"music","action":"playpause|previous|next|seek|shuffle|repeat|volume|favorite","value":...}
//
// The script is embedded in the app and passed to `osascript -l JavaScript -e`.

ObjC.import('Foundation');

var stdin = $.NSFileHandle.fileHandleWithStandardInput;
var stdout = $.NSFileHandle.fileHandleWithStandardOutput;
var buffer = '';

function readLine() {
    while (true) {
        var index = buffer.indexOf('\n');
        if (index >= 0) {
            var line = buffer.slice(0, index);
            buffer = buffer.slice(index + 1);
            return line;
        }
        var data = stdin.availableData;
        if (!data || data.length === 0) return null;
        var text = $.NSString.alloc.initWithDataEncoding(data, $.NSUTF8StringEncoding).js;
        buffer += text;
    }
}

function writeLine(text) {
    var data = $.NSString.alloc.initWithString(text + '\n').dataUsingEncoding($.NSUTF8StringEncoding);
    stdout.writeData(data);
}

function errorText(error) {
    return String(error);
}

function isDenied(error) {
    return /-1743|Not authorized|not authorized|未获授权|不允许/.test(errorText(error));
}

function base64Bytes(value) {
    try {
        if (value && typeof value.base64EncodedStringWithOptions === 'function')
            return value.base64EncodedStringWithOptions(0);
    } catch (error) {
    }
    try {
        if (value && typeof value === 'string' && value.length > 100) {
            var alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
            var output = '';
            for (var index = 0; index < value.length; index += 3) {
                var a = value.charCodeAt(index) & 0xff;
                var b = index + 1 < value.length ? value.charCodeAt(index + 1) & 0xff : 0;
                var c = index + 2 < value.length ? value.charCodeAt(index + 2) & 0xff : 0;
                output += alphabet[a >> 2];
                output += alphabet[((a & 3) << 4) | (b >> 4)];
                output += index + 1 < value.length ? alphabet[((b & 15) << 2) | (c >> 6)] : '=';
                output += index + 2 < value.length ? alphabet[c & 63] : '=';
            }
            return output;
        }
    } catch (error) {
    }
    return null;
}

function artworkData(track) {
    try {
        var artworks = track.artworks();
        if (!artworks || artworks.length === 0) return null;
        var raw = artworks[0].rawData();
        return base64Bytes(raw);
    } catch (error) {
        return null;
    }
}

function readPlayer(name) {
    var source = name.toLowerCase();
    var result = { source: source, running: false };
    var app;
    try {
        app = Application(name);
        result.running = app.running();
    } catch (error) {
        if (isDenied(error)) result.error = errorText(error);
        return result;
    }
    if (!result.running) return result;

    try {
        result.state = String(app.playerState());
    } catch (error) {
        result.error = errorText(error);
        return result;
    }
    if (result.state === 'stopped' || result.state === '') return result;

    try {
        var track = app.currentTrack();
        result.title = track.name();
        result.artist = track.artist();
        result.album = track.album();
        var duration = track.duration();
        // Spotify reports milliseconds, Music reports seconds.
        result.durationMs = source === 'spotify'
            ? Math.round(duration)
            : Math.round(duration * 1000);
        if (source === 'spotify') {
            try { result.artworkUrl = track.artworkUrl(); } catch (error) { }
            result.trackId = track.id();
        } else {
            try { result.trackId = String(track.persistentID()); } catch (error) { }
            if (!result.trackId || /^0+$/.test(result.trackId)) {
                try { result.trackId = String(track.databaseID()); } catch (error) { }
            }
            if (result.trackId && /^0+$/.test(result.trackId)) result.trackId = null;
            try {
                result.favorite = track.favorited();
                result.canFavorite = true;
            } catch (error) {
                result.canFavorite = false;
            }
            result.artworkBase64 = artworkData(track);
        }
    } catch (error) {
        result.error = errorText(error);
        return result;
    }

    try {
        var position = app.playerPosition();
        result.positionMs = Math.max(0, Math.round(position * 1000));
    } catch (error) {
        result.positionMs = 0;
    }
    try {
        result.shuffle = source === 'spotify' ? app.shuffling() : app.shuffleEnabled();
    } catch (error) {
    }
    try {
        if (source === 'spotify') {
            result.repeat = app.repeating() ? 'context' : 'off';
        } else {
            var repeat = String(app.songRepeat());
            result.repeat = repeat === 'one' ? 'track' : repeat === 'all' ? 'context' : 'off';
        }
    } catch (error) {
    }
    try {
        result.volume = Math.min(1, Math.max(0, Number(app.soundVolume()) / 100));
    } catch (error) {
    }
    return result;
}

function readAll() {
    return { spotify: readPlayer('Spotify'), music: readPlayer('Music') };
}

function runCommand(command) {
    var source = command.source === 'music' ? 'music' : 'spotify';
    var app = Application(source === 'music' ? 'Music' : 'Spotify');
    var value = command.value;
    switch (command.action) {
        case 'playpause': app.playpause(); break;
        case 'previous': app.previousTrack(); break;
        case 'next': app.nextTrack(); break;
        case 'seek': app.playerPosition = Math.max(0, Number(value) || 0); break;
        case 'shuffle':
            if (source === 'music') app.shuffleEnabled = Boolean(value);
            else app.shuffling = Boolean(value);
            break;
        case 'repeat':
            if (source === 'music') app.songRepeat = value === 'track' ? 'one' : value === 'context' ? 'all' : 'off';
            else app.repeating = value !== 'off';
            break;
        case 'volume': app.soundVolume = Math.round(Math.min(1, Math.max(0, Number(value) || 0)) * 100); break;
        case 'favorite':
            if (source !== 'music') return { ok: false };
            app.currentTrack().favorited = Boolean(value);
            break;
        default:
            return { ok: false };
    }
    return { ok: true };
}

writeLine('ready');

var line;
while ((line = readLine()) !== null) {
    line = line.trim();
    if (line === 'quit') break;
    if (line === '') continue;
    try {
        if (line === 'poll') {
            writeLine(JSON.stringify(readAll()));
        } else if (line.indexOf('cmd ') === 0) {
            writeLine(JSON.stringify(runCommand(JSON.parse(line.slice(4)))));
        } else {
            writeLine(JSON.stringify({ ok: false }));
        }
    } catch (error) {
        writeLine(JSON.stringify({ ok: false, error: errorText(error) }));
    }
}

void 0;
