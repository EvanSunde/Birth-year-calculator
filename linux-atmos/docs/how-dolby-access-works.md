# How Dolby Access works (and what Linux needs instead)

Findings from unpacking `DolbyLaboratories.DolbyAccess_3.0.2204.0_x64__rz1tebttyb220.Msix`
(an MSIX is a signed zip). Only the manifest, file layout, PE import/export tables and
embedded strings were inspected. No Dolby code or data is redistributed here.

## TL;DR

Dolby Access does **not** contain a Dolby Atmos decoder or player. It is a store/licensing app
that installs three **Windows Spatial Sound encoder plugins**. Windows itself decodes Atmos
streams and hands the audio objects to whichever spatial encoder is active. So to "port" it you
need three separate parts, and Linux has open replacements for each:

| Stage | Windows (with Dolby Access) | Linux (this project) |
|---|---|---|
| 1. Decode E-AC-3 + JOC → audio objects | Windows Media Foundation's Dolby Digital Plus decoder (part of Windows, not the app) | **Cavern** (`atmos-decode`) |
| 2. Mix objects onto a layout | Windows Spatial Sound engine (`ISpatialAudioClient`) | Cavern renderer → fixed 7.1.4 bed |
| 3a. Headphones: objects → binaural | `x64/DolbyHrtfEnc.dll` ("Dolby Atmos for Headphones") | PipeWire filter-chain + SOFA HRTF (`atmos-headphones.conf`) |
| 3b. Home theater: objects → AVR | `x64/DolbyMatEnc.dll` (Dolby MAT 2.0 over HDMI) | Bitstream passthrough of the original E-AC-3/TrueHD (`atmos-play --passthrough`) |
| Licensing / UI | `DolbyUWP.exe` / `DolbyUWP.dll` (.NET Native) | not needed |

## What is in the package

```
DolbyUWP.exe, DolbyUWP.dll (32 MB)   UI, store purchase/trial logic (.NET Native, UWP)
Dolby.SpatialAudioEndpoints.dll      WinRT helper: queries MMDevAPI for spatial-capable endpoints
Dolby.SpatialCodecs.winmd            App service + background task for licence refresh
x64/DolbyHrtfEnc.dll  (770 KB)       Spatial audio encoder: Atmos for Headphones / built-in speakers
x64/DolbyMatEnc.dll   (140 KB)       Spatial audio encoder: Atmos for Home Theater (Dolby MAT)
Data/Configuration/*.json            Marketing content endpoints (AWS API Gateway), analytics IDs
Assets/, winsdkfb.dll, e_sqlite3.dll Images, fonts, Facebook SDK, SQLite
```

### AppxManifest.xml: the important part

The app registers three `windows.mediaCodec` extensions of category **`audioEncoder`**:

| Activatable class | DLL | Input subtype | Output subtype |
|---|---|---|---|
| `Dolby.DolbyAtmosForHeadphonesEncoder` | `x64\DolbyHrtfEnc.dll` | `00000003-0000-0010-8000-00AA00389B71` (`KSDATAFORMAT_SUBTYPE_IEEE_FLOAT`) | `3fe444e2-…` (binaural stereo) |
| `Dolby.DolbyAtmosForSpeakersEncoder` | `x64\DolbyHrtfEnc.dll` | IEEE float | `658c9a86-…` (virtualised speakers) |
| `Dolby.DolbyMatEncoder` | `x64\DolbyMATEnc.dll` | IEEE float | `AACA92BE-…` (MAT / IEC 61937) |

These are the plugins Windows lists under *Sound settings → Spatial sound*. Their input is plain
32-bit float PCM (objects and beds) from the OS spatial audio engine, **not** a Dolby bitstream.

The manifest's `windows.mediaPlayback` extension only *references* decoders that Windows
provides (`atmosDecodeDDPlus`, `atmosDolbyDigitalPlusDecoder`, `{8f3bbd02-…}` "Dolby Digital Plus
decoder"). The app doesn't ship them.

### x64/DolbyHrtfEnc.dll

* Exports only `DllGetActivationFactory` / `DllCanUnloadNow` (a WinRT component).
* Version resource: *"Dolby Audio Processing Encoder DLL"*, built by Microsoft as part of
  Windows (`10.0.18303.1001`). The file is Microsoft's, wrapping Dolby's library.
* Class names in its strings: `CDolbyHrtfEncoderEngine` (`Process`, `EncodeBlocks`,
  `MixChannelBed`, `FoldDownAndMixChannelBed`, `MixStaticObject`, `SetAudioObjectWithMetadataReader`,
  `CreateSampleRateConverter`), `CDefaultDAPModule`, `AudioObject::*`.
* It uses the **Dolby Audio Processing (DAP) SDK, headphone profile** (`dahp_v2_1_1_init_time_params`,
  `dahp_v2_1_1_run_time_params`, `dap_vr_state_s`, *"spatial configurator"*).
* ETW providers: `Microsoft.Windows.Audio.DapEncoder`, `…Spatial.Metadata`, `…Spatial.Renderer`.

So each audio block arrives as a channel bed plus static/dynamic objects with metadata. Static
objects and the bed are mixed into a fixed speaker layout, dynamic objects keep their positions,
and the DAP headphone virtualiser renders everything to two channels with Dolby's proprietary
HRTF and room model.

### x64/DolbyMatEnc.dll

* *"Dolby MAT Encoder DLL"*, `MAT::DolbyMatEncoder` (`AddObjectData`, `MixChannelBed`,
  `GenerateIec61937Frame`), `mat_enc_open` / `mat_enc_process_chunk`.
* Repackages the spatial mix as **Dolby MAT 2.0**, wrapped in IEC 61937 frames, so an Atmos AV
  receiver can decode it over HDMI. This is how Windows sends *game and system* audio as Atmos to
  an AVR.

### DolbyUWP.dll (the app itself)

Strings show what the 32 MB app actually does:

* Store licensing: products `DolbyAtmosforHeadphones`, `…-2YearLic`, `DolbyAtmosforGaming`,
  trial states, *"Acquiring DAHP license…"*, *"Device listed as DAX3 … Dolby Atmos entitled"*
  (OEM laptops with Dolby Audio X3 get the headphone licence free).
* Selecting the active encoder through `Windows.Media.Audio.SpatialAudioDeviceConfiguration`
  `SetDefaultSpatialAudioFormatAsync` / `IsSpatialAudioFormatSupported`.
* Marketing pages, demo videos, ratings reminders, Google Analytics.

## Why the Windows binaries can't simply be reused on Linux

* They are WinRT/COM components bound to the Windows audio engine (Spatial Sound, MMDevAPI,
  Media Foundation). Wine implements none of that pipeline.
* The decoder, the most important part, isn't in the package at all.
* The code is proprietary and licence-gated. Redistributing or patching it isn't an option.

## The open replacement

* **Decoding**: [Cavern](https://github.com/VoidXH/Cavern) by VoidX is an independent
  open-source implementation that decodes E-AC-3 with Joint Object Coding and the Object Audio
  Metadata (OAMD). `atmos-decode` uses it to render the objects onto a 7.1.4 bed at the Dolby
  reference speaker angles.
* **Headphones**: PipeWire's filter-chain `sofa` spatializer convolves each of the 11
  directional speakers with an HRTF from an open SOFA database and sums them to stereo, which
  replaces `DolbyHrtfEnc.dll`. You pick the HRTF, and a personalised one can sound better than
  Dolby's generic one.
* **Home theater**: Linux can't make MAT 2.0, and doesn't need to for music. Passing the
  original E-AC-3/TrueHD bitstream through to the receiver delivers the Atmos stream bit-exact.
  See [home-theater.md](home-theater.md).

## Remaining gaps

* **TrueHD Atmos objects**: Cavern 2.1 parses TrueHD but doesn't decode its audio, so
  `atmos-play` plays the TrueHD 7.1 bed via FFmpeg. [truehdd](https://github.com/truehdd/truehdd)
  (experimental, Rust) decodes TrueHD Atmos to the Dolby Atmos Master Format (`.atmos`), which
  `atmos-play`/`atmos-decode` can then render. This route hasn't been tested here.
* **Dynamic objects are quantised to 7.1.4**: each object is rendered to the bed first and then
  binauralised, instead of being binauralised at its exact position. At 7.1.4 resolution
  this is the same approach Windows uses for static objects and beds.
* **Head tracking** and Dolby's room/EQ model aren't replicated.
