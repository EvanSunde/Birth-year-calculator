#!/usr/bin/env bash
# Installs linux-atmos for the current user:
#   ~/.local/bin/atmos-decode   Cavern-based Atmos decoder (prebuilt native binary, or built from decoder/)
#   ~/.local/bin/atmos-play     player script
#   ~/.local/share/linux-atmos/ HRTF (SOFA) file + decoder binary
#   ~/.config/pipewire/pipewire.conf.d/atmos-headphones.conf   the binaural sink
# With --mpv, also real-time Atmos in mpv (mpv/scripts/atmos-live.lua):
#   ~/.local/bin/atmos-stream, ~/.local/bin/thd-atmos (TrueHD), ~/.config/mpv/scripts/atmos-live.lua
#   and a block in ~/.config/mpv/mpv.conf that sends mpv to the Atmos Headphones sink
#
# Arch Linux dependencies:  sudo pacman -S --needed pipewire ffmpeg
# Optional (HDMI passthrough to an AV receiver): sudo pacman -S mpv
# Only for --build-from-source: sudo pacman -S dotnet-sdk
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="${HOME}/.local/bin"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/linux-atmos"
PW_CONF_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/pipewire/pipewire.conf.d"
PW_CONF="$PW_CONF_DIR/atmos-headphones.conf"

# Default HRTF: ARI database, subject NH724 (CC BY-SA 3.0, Acoustics Research Institute,
# Austrian Academy of Sciences). Measured at 1.2 m. Any SOFA file works; ones measured on
# your own head (or a closer match) sound better.
SOFA_URL="https://sofacoustics.org/data/database/ari/hrtf%20b_nh724.sofa"
SOFA_FILE=""
HRTF_RADIUS="1.2"
HRTF_GAIN="0.5"
LFE_GAIN="0.5"
RESTART=1
SKIP_DECODER=0
FROM_SOURCE=0
MPV=0
MPV_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/mpv"

# Native atmos-decode builds published by .github/workflows/atmos-decode.yml
RELEASE_URL="${ATMOS_RELEASE_URL:-https://github.com/EvanSunde/Birth-year-calculator/releases/download/atmos-decode-latest}"

usage() {
    cat <<EOF
Usage: ./install.sh [options]

  --sofa <file>        Use this SOFA HRTF file instead of downloading the default one
  --radius <m>         Measurement distance of the SOFA file (default $HRTF_RADIUS)
  --hrtf-gain <x>      Gain of each virtual speaker (default $HRTF_GAIN)
  --lfe-gain <x>       Gain of the LFE channel into both ears (default $LFE_GAIN)
  --mpv                Also install real-time Atmos decoding for mpv (heights in films and music)
  --build-from-source  Build the decoders (dotnet-sdk; cargo for --mpv) instead of downloading them
  --skip-decoder       Don't install atmos-decode (FFmpeg-only playback: no height channels)
  --no-restart         Don't restart PipeWire after installing the sink
  --uninstall          Remove everything this script installed
EOF
}

die() { echo "install: $*" >&2; exit 1; }
note() { echo "==> $*"; }

uninstall() {
    rm -f "$BIN_DIR/atmos-play" "$BIN_DIR/atmos-decode" "$BIN_DIR/atmos-stream" "$BIN_DIR/thd-atmos" "$PW_CONF" \
        "$MPV_DIR/scripts/atmos-live.lua"
    [[ -f $MPV_DIR/mpv.conf ]] && sed -i '/# >>> linux-atmos >>>/,/# <<< linux-atmos <<</d' "$MPV_DIR/mpv.conf"
    rm -rf "$DATA_DIR"
    note "Removed linux-atmos. Restart PipeWire to drop the sink: systemctl --user restart pipewire"
}

while (($#)); do
    case "$1" in
        --sofa) SOFA_FILE="$(realpath -- "${2:?}")"; shift ;;
        --radius) HRTF_RADIUS="${2:?}"; shift ;;
        --hrtf-gain) HRTF_GAIN="${2:?}"; shift ;;
        --lfe-gain) LFE_GAIN="${2:?}"; shift ;;
        --skip-decoder) SKIP_DECODER=1 ;;
        --build-from-source) FROM_SOURCE=1 ;;
        --mpv) MPV=1 ;;
        --no-restart) RESTART=0 ;;
        --uninstall) uninstall; exit 0 ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option: $1" ;;
    esac
    shift
done

for tool in pw-play ffmpeg ffprobe; do
    command -v "$tool" >/dev/null || die "'$tool' missing. On Arch: sudo pacman -S --needed pipewire ffmpeg"
done

# The binaural sink needs PipeWire's SOFA plugin (built with libmysofa; Arch's package has it).
sofa_plugin=$(find /usr/lib /usr/lib64 /usr/lib/x86_64-linux-gnu -maxdepth 3 \
    \( -name 'libspa-filter-graph-plugin-sofa.so' -o -name 'libpipewire-module-filter-chain-sofa.so' \) \
    -print -quit 2>/dev/null || true)
if [[ -z $sofa_plugin ]]; then
    echo "WARNING: PipeWire's SOFA plugin was not found. The 'Atmos Headphones' sink will not load" >&2
    echo "         until PipeWire is built with libmysofa (Arch: pacman -S pipewire libmysofa)." >&2
fi

mkdir -p "$BIN_DIR" "$DATA_DIR" "$PW_CONF_DIR"

# Downloads a prebuilt release asset to <dest> and checks it against SHA256SUMS.
# Returns non-zero if it isn't available.
download_asset() { # <asset name> <dest>
    local name=$1 tmp
    tmp=$(mktemp -d)
    note "Downloading $name"
    if ! curl -fsSL --retry 3 -o "$tmp/$name" "$RELEASE_URL/$name" ||
       ! curl -fsSL --retry 3 -o "$tmp/SHA256SUMS" "$RELEASE_URL/SHA256SUMS"; then
        rm -rf "$tmp"; echo "Download failed." >&2; return 1
    fi
    if ! (cd "$tmp" && grep " $name\$" SHA256SUMS | sha256sum -c --quiet -); then
        rm -rf "$tmp"; echo "Checksum mismatch for $name." >&2; return 1
    fi
    mkdir -p "$(dirname -- "$2")"
    install -m 755 "$tmp/$name" "$2"
    rm -rf "$tmp"
}

# Downloads the native atmos-decode (no .NET needed). Returns non-zero if unavailable.
download_decoder() {
    [[ $(uname -m) == x86_64 ]] || { echo "No prebuilt binary for $(uname -m)." >&2; return 1; }
    local name=atmos-decode-linux-x64
    # x86-64-v3 build when the CPU has AVX2 + FMA + BMI2 (Intel Haswell / AMD Zen and newer)
    if grep -qw avx2 /proc/cpuinfo && grep -qw fma /proc/cpuinfo && grep -qw bmi2 /proc/cpuinfo; then
        name+=-v3
    fi
    rm -rf "$DATA_DIR/decoder"
    download_asset "$name" "$DATA_DIR/decoder/atmos-decode"
}

build_decoder() {
    command -v dotnet >/dev/null || die "'dotnet' missing. On Arch: sudo pacman -S dotnet-sdk (or use --skip-decoder)"
    command -v git >/dev/null || die "'git' missing (needed to fetch Cavern)"
    note "Building atmos-decode (Cavern 2.1 + linux-atmos CPU patch)"
    "$ROOT/decoder/cavern/get-cavern.sh"
    rm -rf "$DATA_DIR/decoder"
    DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        dotnet publish "$ROOT/decoder/AtmosDecode.csproj" -c Release -o "$DATA_DIR/decoder" --nologo -v quiet
    rm -rf "$ROOT/decoder/bin" "$ROOT/decoder/obj"
}

if ((SKIP_DECODER)); then
    note "Skipping atmos-decode"
else
    if ((FROM_SOURCE)); then
        build_decoder
    elif ! download_decoder; then
        command -v dotnet >/dev/null || die "Prebuilt atmos-decode unavailable. Install dotnet-sdk and rerun, or use --skip-decoder."
        note "Falling back to building from source"
        build_decoder
    fi
    ln -sf "$DATA_DIR/decoder/atmos-decode" "$BIN_DIR/atmos-decode"
    "$BIN_DIR/atmos-decode" --positions >/dev/null || die "atmos-decode does not run on this system"
fi

install -m 755 "$ROOT/bin/atmos-play" "$BIN_DIR/atmos-play"

if ((MPV)); then
    command -v mpv >/dev/null || die "'mpv' missing. On Arch: sudo pacman -S mpv"
    ((SKIP_DECODER)) && die "--mpv needs atmos-decode (drop --skip-decoder)"
    install -m 755 "$ROOT/bin/atmos-stream" "$BIN_DIR/atmos-stream"
    # TrueHD Atmos decoder
    if ((FROM_SOURCE)) || [[ $(uname -m) != x86_64 ]] ||
       ! download_asset thd-atmos-linux-x64 "$DATA_DIR/thd-atmos"; then
        command -v cargo >/dev/null || die "Prebuilt thd-atmos unavailable and 'cargo' missing (sudo pacman -S rust)."
        note "Building thd-atmos (needs ~11 GB of RAM)"
        (cd "$ROOT/thd-atmos" && cargo build --release --locked -j 1)
        install -m 755 "$ROOT/thd-atmos/target/release/thd-atmos" "$DATA_DIR/thd-atmos"
    fi
    ln -sf "$DATA_DIR/thd-atmos" "$BIN_DIR/thd-atmos"
    "$BIN_DIR/thd-atmos" --positions >/dev/null || die "thd-atmos does not run on this system"

    mkdir -p "$MPV_DIR/scripts"
    install -m 644 "$ROOT/mpv/scripts/atmos-live.lua" "$MPV_DIR/scripts/atmos-live.lua"
    rm -f "$MPV_DIR/scripts/atmos-auto.lua"   # older background-conversion script
    touch "$MPV_DIR/mpv.conf"
    sed -i '/# >>> linux-atmos >>>/,/# <<< linux-atmos <<</d' "$MPV_DIR/mpv.conf"
    cat >> "$MPV_DIR/mpv.conf" <<'CONF'
# >>> linux-atmos >>>
# mpv -> 12-ch "Atmos Headphones" sink; the file's own layout is passed through
# (7.1.4 live Atmos, 7.1 / 5.1 films, stereo music).
audio-device=pipewire/effect_input.atmos_headphones
audio-channels=auto
# <<< linux-atmos <<<
CONF
    note "mpv: Atmos tracks now play with live height channels (Ctrl+a toggles)"
fi

if [[ -z $SOFA_FILE ]]; then
    SOFA_FILE="$DATA_DIR/hrtf.sofa"
    if [[ ! -s $SOFA_FILE ]]; then
        note "Downloading HRTF set (ARI NH724, CC BY-SA 3.0)"
        curl -fL --retry 3 -o "$SOFA_FILE.part" "$SOFA_URL"
        mv "$SOFA_FILE.part" "$SOFA_FILE"
    fi
fi
[[ -s $SOFA_FILE ]] || die "SOFA file not found: $SOFA_FILE"

note "Installing PipeWire sink -> $PW_CONF"
sed -e "s|@SOFA_FILE@|$SOFA_FILE|g" \
    -e "s|@HRTF_GAIN@|$HRTF_GAIN|g" \
    -e "s|@LFE_GAIN@|$LFE_GAIN|g" \
    -e "s|@HRTF_RADIUS@|$HRTF_RADIUS|g" \
    "$ROOT/pipewire/atmos-headphones.conf.in" > "$PW_CONF"

if ((RESTART)) && command -v systemctl >/dev/null; then
    note "Restarting PipeWire"
    systemctl --user restart pipewire pipewire-pulse 2>/dev/null || systemctl --user restart pipewire || true
fi

case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) echo "NOTE: add $BIN_DIR to your PATH." ;;
esac

cat <<EOF

Done. Plug in headphones, then:
  atmos-play song.m4a              # Dolby Atmos (E-AC-3 JOC) -> 7.1.4 -> binaural
  atmos-play --passthrough movie.mkv   # bitstream to an Atmos receiver over HDMI

"Atmos Headphones (7.1.4 binaural)" also shows up as an output device, so other
multichannel players (mpv, Kodi, games) can use it too.
EOF
