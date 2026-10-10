# Atmos on an AV receiver (HDMI passthrough)

On Windows, "Dolby Atmos for Home Theater" (`DolbyMatEnc.dll`) re-encodes the Windows spatial
mix to Dolby MAT 2.0 so a receiver can decode it. For playing Atmos *files* on Linux you don't
need to re-encode anything: send the original bitstream and the receiver's licensed decoder does
the rest. This is bit-exact and includes all objects, for both E-AC-3 JOC and TrueHD.

## 1. Let the HDMI output accept compressed formats

PipeWire marks which IEC 61937 formats an S/PDIF or HDMI sink accepts. With `pipewire-pulse`:

```sh
pactl list short sinks                     # find your HDMI sink name
pactl set-sink-formats <hdmi-sink-name> 'pcm; ac3-iec61937; eac3-iec61937; truehd-iec61937; dts-iec61937'
```

Or in `pavucontrol`: *Output Devices → your HDMI device → Advanced → tick AC-3, E-AC-3,
Dolby TrueHD*. Make the HDMI device the default output, using a **Digital Surround / HDMI**
profile under *Configuration*.

TrueHD needs an HBR-capable HDMI link (HDMI 1.3+ and a GPU driver that supports HBR audio;
recent AMD, Intel and NVIDIA drivers do).

## 2. Play

```sh
atmos-play --passthrough song.m4a      # E-AC-3 JOC (streaming-service Atmos music)
atmos-play --passthrough film.mkv      # TrueHD Atmos
```

This runs `mpv --audio-spdif=eac3,truehd,ac3,dts-hd,dts`. The receiver's display should show
*Dolby Atmos*.

## Limits

* Passthrough is exclusive: only one bitstream at a time, and no system sounds mixed in.
* Without passthrough, `atmos-play` (headphone mode) can also be pointed at a real multichannel
  sink, e.g. a 7.1 HDMI PCM output: `atmos-play --sink <hdmi-sink-node> --layout 7.1 song.m4a`.
  That gives Cavern-rendered PCM, but the receiver won't show "Atmos" because it's PCM.
