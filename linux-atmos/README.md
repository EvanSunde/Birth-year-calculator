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
`atmos-play`, or pipe `atmos-decode` yourself:

```sh
ffmpeg -i song.m4a -map 0:a:0 -c copy -f eac3 - | atmos-decode - |
  pw-play --raw --target effect_input.atmos_headphones --format f32 --rate 48000 \
          --channels 12 --channel-map "$(atmos-decode --positions)" -
# (PipeWire <= 1.0: drop --raw)
```

### Where do I get Atmos music files?

Streaming services (Tidal, Amazon Music, Apple Music) deliver Dolby Atmos music as E-AC-3 + JOC.
Blu-ray audio discs use TrueHD Atmos. Use files you are entitled to play. This project doesn't
download or decrypt anything.

## How it runs (no daemon needed)

* The **"Atmos Headphones" sink** is part of PipeWire's config, so it starts with PipeWire on
  every login. There's nothing extra to keep running. Anything played to it (7.1.4, 7.1, 5.1 or
  stereo) is virtualised for headphones.
* **The height objects are only decoded when you play through `atmos-play`.** Other players
  (mpv, browsers, Strawberry, …) decode E-AC-3 with FFmpeg, which discards the Atmos objects
  and outputs the 5.1 core. Played to the sink, that still gives surround on headphones, with
  the height sounds folded into the ear-level speakers. Apps hand PipeWire already-decoded PCM,
  so a background service can't intercept the Atmos stream to add the heights back.
* `atmos-play` has no pause/seek: stop with Ctrl+C.

## CPU use

Measured on a 3.6-minute Apple Music Atmos track (E-AC-3 JOC, 5.1 core + 16 objects, 768 kb/s)
on a cloud vCPU:

| | CPU time for the whole track | average load |
|---|---|---|
| `atmos-decode` (5.1 core + 16 objects → 7.1.4) | ~38 s | ~17% of one core |
| FFmpeg, 5.1 core only (no heights), for comparison | ~0.9 s | <1% |
| HRTF sink (11 convolutions in PipeWire) | ~0.3 s per 30 s played | ~1% of one core, 0 when idle |

Nearly all of the cost is Cavern's Joint Object Coding decoder, which runs its QMF filterbanks
as direct matrix products. The renderer quality setting and block size make no measurable
difference. The native (AOT) build has the same throughput as the JIT build but starts in 30 ms
instead of 100 ms. Its output is sample-identical on AVX2, and within float rounding (−136 dBFS)
on the baseline build.

## Tuning

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

* **TrueHD Atmos**: Cavern 2.1 can't decode TrueHD audio yet, so headphone mode plays the TrueHD
  7.1 bed via FFmpeg. Use `--passthrough` with a receiver, or try
  [truehdd](https://github.com/truehdd/truehdd) → `.atmos` → `atmos-play file.atmos`
  (untested).
* PipeWire builds without libmysofa can't load the sink. Arch's package includes it.
* No head tracking, and no Dolby room/EQ model. Objects are rendered to 7.1.4 before
  binauralisation.

## Licences

* [Cavern](https://github.com/VoidXH/Cavern) is downloaded from NuGet at build time and is
  under its own licence, which allows free non-commercial use and forbids selling it.
* The default HRTF (ARI database) is CC BY-SA 3.0, Acoustics Research Institute, Austrian
  Academy of Sciences.
* Dolby, Dolby Atmos and Dolby Access are trademarks of Dolby Laboratories. This project
  contains no Dolby code and is not affiliated with Dolby.
