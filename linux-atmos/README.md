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
sudo pacman -S --needed pipewire wireplumber ffmpeg dotnet-sdk
sudo pacman -S --needed mpv          # optional: HDMI passthrough to an AV receiver
./install.sh
```

The installer:

1. builds `atmos-decode` (pulls the Cavern library from NuGet) into `~/.local/share/linux-atmos`,
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
Plus + Dolby Atmos": 7-ch core, 11 JOC objects + LFE) and synthetic test tones:

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
