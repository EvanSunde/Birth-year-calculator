# linux-atmos: Dolby Atmos music on Arch Linux + PipeWire

An open-source equivalent of the **Dolby Access** Windows app, built from what that app
actually does (see [docs/how-dolby-access-works.md](docs/how-dolby-access-works.md)):

```
 song.m4a (E-AC-3 + JOC "Dolby Atmos")
   │  ffmpeg -c copy            demux only, the bitstream is untouched
   ▼
 atmos-decode  (Cavern)          decode the core + JOC → objects (up to 15 + LFE) and their positions,
   │                             render the objects onto a 7.1.4 bed (Dolby reference angles)
   ▼  12 ch float PCM
 "Atmos Headphones" PipeWire sink   11 HRTF convolutions (SOFA) + LFE → 2 ch binaural
   ▼
 your headphones
```

| Dolby Access on Windows | Here |
|---|---|
| Windows' built-in DD+ / Atmos decoder | `atmos-decode` (uses [Cavern](https://github.com/VoidXH/Cavern)) |
| Dolby Atmos for Headphones (`DolbyHrtfEnc.dll`) | `pipewire/atmos-headphones.conf` (filter-chain + SOFA HRTF) |
| Dolby Atmos for Home Theater (`DolbyMatEnc.dll`) | `atmos-play --passthrough` (bitstream to your receiver) |

## Install (Arch Linux)

```sh
sudo pacman -S --needed pipewire wireplumber ffmpeg
sudo pacman -S --needed mpv          # optional: HDMI passthrough to an AV receiver
./install.sh
```

The installer:

1. downloads a prebuilt native `atmos-decode` (8.7 MB, no .NET needed, checksum-verified) from
   this repo's `atmos-decode-latest` release into `~/.local/share/linux-atmos`. It picks the
   AVX2 build on CPUs from about 2015 onwards. `--build-from-source` builds it with `dotnet-sdk`
   instead, which is also the automatic fallback if the download fails,
2. downloads a default HRTF set (ARI NH724, CC BY-SA 3.0) unless you pass `--sofa <file>`,
3. writes the sink to `~/.config/pipewire/pipewire.conf.d/atmos-headphones.conf` and restarts
   PipeWire,
4. installs `atmos-play` to `~/.local/bin`.

`./install.sh --uninstall` removes everything.

## Use

```sh
atmos-play song.m4a                # Atmos (E-AC-3 JOC) → 7.1.4 → binaural headphones
atmos-play *.m4a *.flac            # non-Atmos files play through the same sink (FFmpeg decode)
atmos-play --passthrough song.m4a  # bitstream to an Atmos AV receiver (docs/home-theater.md)
atmos-play -n song.m4a             # print the pipeline without running it
```

The sink also appears as a normal output device, **Atmos Headphones (7.1.4 binaural)**.
Anything that outputs 5.1/7.1/7.1.4 PCM to it (mpv, Kodi, games, browsers) gets virtualised too:

```sh
mpv --audio-device=pipewire/effect_input.atmos_headphones --audio-channels=7.1 movie.mkv
```

Plain mpv/FFmpeg only decode the 5.1/7.1 core of an Atmos stream. For the height objects use
mpv with live decoding (below), `atmos-play`, or pipe `atmos-decode` yourself:

```sh
ffmpeg -i song.m4a -map 0:a:0 -c copy -f eac3 - | atmos-decode - |
  pw-play --raw --target effect_input.atmos_headphones --format f32 --rate 48000 \
          --channels 12 --channel-map "$(atmos-decode --positions)" -
# (PipeWire <= 1.0: drop --raw)
```

### mpv with live Atmos decoding

`./install.sh --mpv` adds `mpv/scripts/atmos-live.lua`. When the selected audio track is Atmos
(E-AC-3 + JOC, or TrueHD Atmos), it decodes the heights in real time from the current position and
plays them as an extra audio track, so pause, seek and speed all keep working:

```
mpv film.mkv → Atmos track? → atmos-stream <file> <track> <time>     (restarted on every seek)
   ffmpeg -c copy (cut at the exact frame) → atmos-decode --mka       E-AC-3 + JOC (Cavern)
                                          → thd-atmos | atmos-decode --wrap-pcm   TrueHD Atmos
   → live Matroska track, 7.1.4 float PCM with film timestamps → mpv → Atmos Headphones sink
```

* After a seek, the core 5.1/7.1 track plays for about half a second while the decoder restarts.
* **Ctrl+a** switches between the Atmos heights and the core audio.
* Options go in `~/.config/mpv/script-opts/atmos_live.conf`: `enabled=yes|no`, `force=yes`
  (treat every E-AC-3/TrueHD track as Atmos), `preroll=0.3`.
* If anything fails, mpv keeps playing the core audio and logs `atmos-stream: ...` (`mpv -v`).
* The installer's `mpv.conf` block sends **all** mpv audio to the Atmos Headphones sink. Remove
  `audio-device=` there if you also use mpv with speakers.

### Where do I get Atmos music files?

Streaming services (Tidal, Amazon Music, Apple Music) deliver Dolby Atmos music as E-AC-3 + JOC.
Blu-ray audio discs use TrueHD Atmos. Use files you are entitled to play. This project doesn't
download or decrypt anything.

## How it runs (no daemon needed)

* The **"Atmos Headphones" sink** is part of PipeWire's config, so it starts with PipeWire on
  every login. There's nothing extra to keep running. Anything played to it (7.1.4, 7.1, 5.1 or
  stereo) is virtualised for headphones.
* **The height objects are only decoded through `atmos-play` or mpv with `--mpv`.** Other
  players (browsers, Strawberry, …) decode E-AC-3 with FFmpeg, which discards the Atmos objects
  and outputs the 5.1 core. Played to the sink, that still gives surround on headphones, with
  the height sounds folded into the ear-level speakers. Apps hand PipeWire already-decoded PCM,
  so a background service can't intercept the Atmos stream to add the heights back.
* `atmos-play` has no pause/seek: stop with Ctrl+C.

## CPU use

Measured on a 3.6-minute Apple Music Atmos track (E-AC-3 JOC, 5.1 core + 16 objects, 768 kb/s)
on a 4-vCPU cloud machine (a laptop core is usually faster):

| | before | now |
|---|---|---|
| `atmos-decode`, 40 s of audio (CPU time) | ~12.2 s | ~3.8 s (3.2× less) |
| mpv live Atmos, whole pipeline incl. mpv, while playing | ~40% of one core | ~17% of one core |
| `thd-atmos` (TrueHD), 60 s of 5.1 TrueHD | 8.8 s | 2.1 s (4.1× less) |
| HRTF sink (11 convolutions in PipeWire) | ~1% of one core, 0 when idle | same |

What changed (all output is bit-identical to before, checked over whole tracks):

* **Cavern's JOC decoder** ran each 64-sample time slot's transforms as ~22 thread-pool jobs,
  about 16,000 hand-offs per second; the synchronisation cost more CPU than the maths. It now
  runs them sequentially, skips objects that are silent (4 of 16 in the track above are unused
  for the whole song), and no longer repeats a matrix copy 24 times per frame.
  See [decoder/cavern/joc-cpu.patch](decoder/cavern/joc-cpu.patch).
* **mpv pipeline:** `atmos-decode` writes the timestamped Matroska track itself (A_MS/ACM, which
  carries the 7.1.4 channel mask). Previously a second FFmpeg re-encoded the decoder output to
  WavPack and mpv decoded it again, which cost about as much as the decoder.
* **Native decoder** (.NET NativeAOT) instead of the .NET runtime: no JIT on every seek restart,
  30 ms start-up.
* **Decoder timing:** JOC objects come out of Cavern 576 samples (12 ms) late. The live track's
  timestamps now compensate for it, so the heights line up with the picture and the core audio.
* **thd-atmos** is built with the `truehd` crate optimised (opt-level 1) instead of opt-level 0.

## Tuning## Tuning

* **HRTF**: externalisation depends on how well the HRTF matches your ears. Try other SOFA files
  (the [SOFA database list](https://www.sofaconventions.org/mediawiki/index.php/Files) has
  ARI, CIPIC, LISTEN, SADIE II, HUTUBS and more):
  `./install.sh --sofa ~/Downloads/other.sofa --radius <measurement distance in m>`
* **Level**: `--hrtf-gain` (default 0.5) and `--lfe-gain` (default 0.5) when installing, or
  `atmos-play --gain -3` per playback.
* **Speaker angles**: edit `tools/gen-pipewire-conf.py` and regenerate
  (`python3 tools/gen-pipewire-conf.py pipewire/atmos-headphones.conf.in`). Keep them in sync
  with `CreateLayout()` in `decoder/Program.cs`.

## Status / what was verified

Tested on PipeWire 1.0.5 with FFmpeg's `the_great_wall_7.1.eac3` sample (profile "Dolby Digital
Plus + Dolby Atmos": 7-ch core, 11 JOC objects + LFE), a full Apple Music Atmos track (5.1 core,
16 objects, with real content on all four height channels) and synthetic test tones:

* `atmos-decode` decodes the JOC objects and OAMD positions. Bed channel levels match FFmpeg's
  core decode, and file input and piped input give byte-identical output.
* Channel routing is 1:1 for 5.1/7.1 beds, and 7.1.4 height channels reach TFL/TFR/TRL/TRR.
  (The 5-second movie sample itself has no height content.)
* The binaural sink loads, and left/right/centre/height speakers produce the expected
  interaural level differences. The full `atmos-play` pipeline produces binaural output.

Known limits:

* **TrueHD Atmos**: Cavern can't decode TrueHD audio, so `atmos-play` plays the TrueHD 7.1 bed via
  FFmpeg. mpv with `--mpv` decodes TrueHD Atmos objects with `thd-atmos` (the experimental
  `truehd` crate; tested on FFmpeg-encoded streams only, no real Atmos TrueHD). Use
  `--passthrough` with a receiver for the reference decode.
* PipeWire builds without libmysofa can't load the sink. Arch's package includes it.
* No head tracking, and no Dolby room/EQ model. Objects are rendered to 7.1.4 before
  binauralisation.

## Licences

* [Cavern](https://github.com/VoidXH/Cavern) by VoidX (http://en.sbence.hu) is fetched at build
  time (tag 2.1) and patched with [decoder/cavern/joc-cpu.patch](decoder/cavern/joc-cpu.patch).
  Its licence allows free non-commercial use and modified versions released for free with a
  link to the original, and forbids selling it.
* `thd-atmos` uses the [truehd](https://github.com/truehdd/truehdd) crate (Apache-2.0).
* The default HRTF (ARI database) is CC BY-SA 3.0, Acoustics Research Institute, Austrian
  Academy of Sciences.
* Dolby, Dolby Atmos and Dolby Access are trademarks of Dolby Laboratories. This project
  contains no Dolby code and is not affiliated with Dolby.
