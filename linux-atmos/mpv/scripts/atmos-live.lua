-- atmos-live.lua: real-time Dolby Atmos height decoding for mpv.
--
-- When the selected audio track is Atmos (E-AC-3 + JOC, or TrueHD Atmos), this starts
-- `atmos-stream` from the current position and plays its 7.1.4 output as the audio track.
-- On every seek it restarts the decoder at the new position. Pause, seek and speed work as
-- normal. If anything fails, mpv simply keeps playing the normal 5.1/7.1 core.
--
-- Options (~/.config/mpv/script-opts/atmos_live.conf or --script-opts=atmos_live-<opt>=...):
--   enabled=yes    turn the script on/off
--   force=no       treat every E-AC-3/TrueHD track as Atmos (for testing)
--   preroll=0.3    seconds decoded before the seek point (absorbs decoder start-up)
-- Keys: Ctrl+a toggles live Atmos decoding (input.conf: <key> script-binding atmos_live/toggle)
local mp = require "mp"
local utils = require "mp.utils"
local options = require "mp.options"

local o = { enabled = true, force = false, preroll = 0.3 }
options.read_options(o, "atmos_live")

local fifo_dir = mp.command_native({ "expand-path", "~~cache/atmos-live" })
local cur = nil   -- { path, ff_index, codec, orig_id }
local live = nil  -- { id, fifo, job, gen }
local gen = 0
local restarting = false

local function is_live_id(id)
    for _, tr in ipairs(mp.get_property_native("track-list", {})) do
        if tr.id == id and tr.type == "audio" then return tr.title == "Atmos 7.1.4 (live)" end
    end
    return false
end

local function run(args)
    return mp.command_native({ name = "subprocess", args = args, playback_only = false,
                               capture_stdout = true, capture_stderr = true })
end

-- mpv started from the desktop (file manager, launcher) usually doesn't have ~/.local/bin in
-- PATH, so every command this script runs gets it added.
local PATH_FIX = 'PATH="$HOME/.local/bin:$PATH"; export PATH; '

-- "ok", "missing" or "broken" (installed but doesn't start, e.g. no .NET runtime)
local function check(cmd)
    local r = run({ "sh", "-c", PATH_FIX .. 'command -v "$1" >/dev/null || exit 3; "$1" --positions', "sh", cmd })
    if r.status == 0 then return "ok" end
    if r.status == 3 then return "missing" end
    mp.msg.warn(cmd .. " --positions failed: " .. (r.stderr or "") .. (r.stdout or ""))
    return "broken"
end

local function close(l)
    if not l then return end
    -- The pipeline runs in its own process group: stop all of it (ffmpeg, decoder, ffmpeg).
    local f = io.open(l.fifo .. ".pid")
    local pgid = f and tonumber(f:read("*l") or "")
    if f then f:close() end
    if pgid then run({ "kill", "-TERM", "--", "-" .. pgid }) end
    os.remove(l.fifo .. ".pid")
    if l.job then mp.abort_async_command(l.job) end
    if l.id then mp.commandv("audio-remove", l.id) end
    os.remove(l.fifo)
    -- keep the last pipeline error for troubleshooting (~/.cache/mpv/atmos-live/last-error.log)
    local f = io.open(l.fifo .. ".log")
    local log = f and f:read("*a") or ""
    if f then f:close() end
    if log:find("%S") and not log:find("^atmos%-decode: EnhancedAC3[^\n]*\n?$") and not log:find("^thd%-atmos: [^\n]*\n?$") then
        local e = io.open(utils.join_path(fifo_dir, "last-error.log"), "w")
        if e then e:write(log) e:close() end
    end
    os.remove(l.fifo .. ".log")
end

-- The live track mpv created from this FIFO (matched by path: track ids are only unique per
-- type, so a cover.jpg loaded as video track 2 has the same id as a new audio track 2).
local function find_track(fifo)
    for _, tr in ipairs(mp.get_property_native("track-list", {})) do
        if tr.type == "audio" and tr.external and tr["external-filename"] == fifo then return tr.id end
    end
end

local function stop()
    local l = live
    live = nil
    close(l)
end

local function start(t)
    if not cur then return end
    local old = live   -- closed only after the new track is playing, so seeks have no gap
    gen = gen + 1
    local fifo = utils.join_path(fifo_dir, ("live-%d-%d.mka"):format(utils.getpid(), gen))
    os.remove(fifo)
    if run({ "mkfifo", fifo }).status ~= 0 then return end
    t = math.max(0, t - o.preroll)
    -- atmos-stream works in the file's own timestamps; mpv's timeline starts at 0 (rebased)
    local shift = cur.start_time
    local l = { fifo = fifo, gen = gen }
    live = l
    -- The shell opens the FIFO for writing, so atmos-stream starts as soon as mpv opens it.
    -- setsid: own process group (killed as a whole in close()); errors go to <fifo>.log so mpv
    -- never waits on the pipeline's output pipes.
    l.job = mp.command_native_async({ name = "subprocess", playback_only = false,
        args = { "setsid", "sh", "-c",
                 PATH_FIX .. '[ -p "$4" ] || exit 0; echo $$ > "$4.pid"; exec atmos-stream "$1" "$2" "$3" "$5" > "$4" 2> "$4.log"',
                 "atmos-stream", cur.path, tostring(cur.ff_index), ("%.3f"):format(t + shift), fifo,
                 ("%.6f"):format(shift) } },
        function(_, res)
            if live == l and res and res.status ~= 0 and not res.killed_by_us then
                local f = io.open(fifo .. ".log")
                mp.msg.warn("atmos-stream: " .. (f and f:read("*a") or ""))
                if f then f:close() end
                mp.osd_message("Dolby Atmos: live decode failed, playing core audio", 4)
                if cur then mp.set_property_number("aid", cur.orig_id) end
                stop()
            end
        end)
    mp.commandv("audio-add", fifo, "select", "Atmos 7.1.4 (live)")
    l.id = find_track(fifo)
    if l.id then close(old) return end

    -- Not there (yet): some mpv versions finish adding the track a moment later.
    local tries = 0
    local timer
    timer = mp.add_periodic_timer(0.1, function()
        tries = tries + 1
        if live ~= l then timer:kill() return end
        l.id = find_track(fifo)
        if l.id then
            timer:kill()
            mp.set_property_number("aid", l.id)
            close(old)
        elseif tries >= 30 then
            timer:kill()
            mp.msg.warn("could not open the live Atmos track (details: ~/.cache/mpv/atmos-live/last-error.log)")
            mp.osd_message("Dolby Atmos: live track failed, playing core audio", 4)
            close(old)
            if cur then mp.set_property_number("aid", cur.orig_id) end
            stop()
        end
    end)
end

local function is_atmos(path, track)
    if o.force then return true end
    local r = run({ "ffprobe", "-v", "error", "-select_streams", tostring(track["ff-index"]),
                    "-show_entries", "stream=profile", "-of", "default=nw=1:nk=1", path })
    return r.status == 0 and (r.stdout or ""):find("Atmos") ~= nil
end

mp.register_event("file-loaded", function()
    stop()
    cur = nil
    if not o.enabled then return end
    local path = mp.get_property("path")
    if not path or path:find("^%a[%w+.-]*://") then return end
    path = utils.join_path(mp.get_property("working-directory"), path)

    local sel
    for _, tr in ipairs(mp.get_property_native("track-list", {})) do
        if tr.type == "audio" and tr.selected and not tr.external then sel = tr end
    end
    if not sel or sel["ff-index"] == nil then return end
    local dec = ({ eac3 = "atmos-decode", truehd = "thd-atmos" })[sel.codec]
    if not dec or not is_atmos(path, sel) then return end
    for _, cmd in ipairs({ "atmos-stream", dec }) do
        local st = cmd == "atmos-stream" and
            (run({ "sh", "-c", PATH_FIX .. "command -v atmos-stream" }).status == 0 and "ok" or "missing")
            or check(cmd)
        if st ~= "ok" then
            local why = st == "missing" and (cmd .. " is not installed (run linux-atmos/install.sh --mpv)")
                or (cmd .. " can't start (see mpv -v)")
            mp.osd_message("Dolby Atmos found, but " .. why, 6)
            mp.msg.warn(why)
            return
        end
    end
    run({ "mkdir", "-p", fifo_dir })
    -- Remove leftovers of mpv instances that were killed
    run({ "sh", "-c", 'for f in "$1"/live-*; do [ -e "$f" ] || continue; p=${f##*/live-}; p=${p%%-*};' ..
          ' kill -0 "$p" 2>/dev/null || rm -f "$f"; done', "sh", fifo_dir })
    -- mpv shifts every file's first timestamp to 0. The live track carries the film's real
    -- timestamps, so it must not be shifted again (it starts mid-film).
    -- The main file was opened with the user's setting: if it was rebased, mpv's time 0 is the
    -- file's start time.
    local start = 0
    if mp.get_property("rebase-start-time") ~= "no" then
        start = mp.get_property_number("demuxer-start-time", 0) or 0
    end
    mp.set_property("file-local-options/rebase-start-time", "no")
    cur = { path = path, ff_index = sel["ff-index"], codec = sel.codec, orig_id = sel.id,
            start_time = start }
    cur.pending = true   -- started at the first playback-restart, once --start/resume seeks are done
    mp.osd_message("Dolby Atmos: height channels ON (" .. sel.codec .. ")", 3)
end)

-- Restart the decoder after every seek (the live track can't seek itself).
-- While the new decoder starts, play the normal core track (which can seek) instead of the
-- old live track (which can't, and would play audio from before the seek).
mp.register_event("seek", function()
    if live then
        restarting = true
        -- Switch first, then remove: removing the selected track would end playback of
        -- audio-only files (music with cover art).
        mp.set_property_number("aid", cur.orig_id)
        stop()
    end
end)
mp.register_event("playback-restart", function()
    if cur and cur.pending then
        cur.pending = false
        restarting = false
        start(mp.get_property_number("time-pos", 0))
    elseif restarting and cur then
        restarting = false
        start(mp.get_property_number("time-pos", 0))
    end
end)

-- Picking any other audio track turns live decoding off; Ctrl+a toggles it back on.
mp.observe_property("aid", "native", function(_, aid)
    if live and live.id and type(aid) == "number" and aid ~= live.id and not is_live_id(aid) then
        stop()
    end
end)

mp.add_key_binding("ctrl+a", "toggle", function()
    if not cur then mp.osd_message("No Dolby Atmos track", 2) return end
    if live then
        mp.set_property_number("aid", cur.orig_id)
        stop()
        mp.osd_message("Dolby Atmos heights: OFF (core audio)", 2)
    else
        mp.osd_message("Dolby Atmos heights: ON", 2)
        start(mp.get_property_number("time-pos", 0))
    end
end)

mp.register_event("end-file", function() stop(); cur = nil end)
mp.register_event("shutdown", stop)
