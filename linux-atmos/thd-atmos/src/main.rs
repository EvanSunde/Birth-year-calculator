//! thd-atmos: real-time Dolby TrueHD Atmos -> 7.1.4 renderer.
//!
//! stdin : raw TrueHD elementary stream (e.g. `ffmpeg -i film.mkv -map 0:a:0 -c copy -f truehd -`)
//! stdout: interleaved f32 LE, 12 channels FL FR FC LFE RL RR SL SR TFL TFR TRL TRR
//!         (same order as atmos-decode / the "Atmos Headphones" PipeWire sink)
//!
//! Decoding is done by the `truehd` crate (presentation 3 = up to 16 object channels + OAMD).
//! Each object is panned onto the 7.1.4 layout from its OAMD position, following the
//! metadata's sample offsets and ramps. Streams without Atmos objects are mapped by
//! channel label. Output stays sample-aligned with the input: frames that can't be
//! decoded (before the first major sync, or after an error) are replaced by silence.

use std::collections::VecDeque;
use std::f32::consts::FRAC_PI_2;
use std::io::{self, Read, Write};

use truehd::process::decode::{DecodedAccessUnit, Decoder};
use truehd::process::extract::Extractor;
use truehd::process::parse::Parser;
use truehd::structs::channel::ChannelLabel;
use truehd::structs::oamd::ObjectAudioMetadataPayload;
use truehd::utils::errors::ExtractError;

const OUT: usize = 12;
const MAX_CH: usize = 16;
// Output indices
const FL: usize = 0; const FR: usize = 1; const FC: usize = 2; const LFE: usize = 3;
const RL: usize = 4; const RR: usize = 5; const SL: usize = 6; const SR: usize = 7;
const TFL: usize = 8; const TFR: usize = 9; const TRL: usize = 10; const TRR: usize = 11;
const H: f32 = std::f32::consts::FRAC_1_SQRT_2;
/// Gains are re-evaluated every TICK samples and interpolated in between.
const TICK: u64 = 8;
/// How long to hold audio while waiting for the first object metadata.
const MAX_HOLD_SAMPLES: usize = 48000 * 2;

/// Pans a DAMF-coordinate position (x -1 left..1 right, y -1 back..1 front, z 0 ear..1 top)
/// onto 7.1.4 with power-preserving pairwise panning per axis (cube / dual-balance panner).
fn pan(x: f32, y: f32, z: f32, elevation: bool) -> [f32; OUT] {
    let mut g = [0f32; OUT];
    let x = x.clamp(-1.0, 1.0);
    let y = y.clamp(-1.0, 1.0);
    let z = if elevation { z.clamp(0.0, 1.0) } else { 0.0 };
    let (ear, top) = ((z * FRAC_PI_2).cos(), (z * FRAC_PI_2).sin());
    let lr = |u: f32| ((u * FRAC_PI_2).cos(), (u * FRAC_PI_2).sin()); // u 0 = left .. 1 = right
    let u = (x + 1.0) * 0.5;

    // Ear level: front row (FL FC FR), side row (SL SR), rear row (RL RR)
    let (row_a, row_b, t) = if y >= 0.0 { (0, 1, y) } else { (2, 1, -y) };
    let (w_mid, w_edge) = ((t * FRAC_PI_2).cos(), (t * FRAC_PI_2).sin());
    for (row, w) in [(row_a, w_edge), (row_b, w_mid)] {
        let w = w * ear;
        if w == 0.0 { continue; }
        match row {
            0 => {
                if x < 0.0 {
                    let (a, b) = lr(x + 1.0);
                    g[FL] += w * a; g[FC] += w * b;
                } else {
                    let (a, b) = lr(x);
                    g[FC] += w * a; g[FR] += w * b;
                }
            }
            1 => { let (a, b) = lr(u); g[SL] += w * a; g[SR] += w * b; }
            _ => { let (a, b) = lr(u); g[RL] += w * a; g[RR] += w * b; }
        }
    }
    // Top: front pair / rear pair
    if top > 0.0 {
        let t = (y + 1.0) * 0.5; // 0 back .. 1 front
        let (wb, wf) = ((t * FRAC_PI_2).cos(), (t * FRAC_PI_2).sin());
        let (a, b) = lr(u);
        g[TFL] += top * wf * a; g[TFR] += top * wf * b;
        g[TRL] += top * wb * a; g[TRR] += top * wb * b;
    }
    g
}

/// Bed speaker (OAMD SpeakerLabels index) -> fixed 7.1.4 gains.
fn bed_gains(label: usize) -> [f32; OUT] {
    let mut g = [0f32; OUT];
    match label {
        0 => g[FL] = 1.0, 1 => g[FR] = 1.0, 2 => g[FC] = 1.0, 3 | 16 => g[LFE] = 1.0,
        4 => g[SL] = 1.0, 5 => g[SR] = 1.0, 6 => g[RL] = 1.0, 7 => g[RR] = 1.0,
        8 => g[TFL] = 1.0, 9 => g[TFR] = 1.0,
        10 => { g[TFL] = H; g[TRL] = H; } 11 => { g[TFR] = H; g[TRR] = H; }
        12 => g[TRL] = 1.0, 13 => g[TRR] = 1.0,
        14 => { g[FL] = H; g[SL] = H; } 15 => { g[FR] = H; g[SR] = H; }
        _ => {}
    }
    g
}

/// Channel label (non-object presentations) -> fixed 7.1.4 gains.
fn label_gains(l: &ChannelLabel) -> [f32; OUT] {
    use ChannelLabel::*;
    let mut g = [0f32; OUT];
    match l {
        L => g[FL] = 1.0, R => g[FR] = 1.0, C => g[FC] = 1.0, LFE | LFE2 => g[self::LFE] = 1.0,
        Ls | Lsd => g[SL] = 1.0, Rs | Rsd => g[SR] = 1.0, Lb => g[RL] = 1.0, Rb => g[RR] = 1.0,
        Cb => { g[RL] = H; g[RR] = H; }
        Lsc => { g[FL] = H; g[FC] = H; } Rsc => { g[FR] = H; g[FC] = H; }
        Lw => { g[FL] = H; g[SL] = H; } Rw => { g[FR] = H; g[SR] = H; }
        Tfl => g[TFL] = 1.0, Tfr => g[TFR] = 1.0, Tbl => g[TRL] = 1.0, Tbr => g[TRR] = 1.0,
        Tsl => { g[TFL] = H; g[TRL] = H; } Tsr => { g[TFR] = H; g[TRR] = H; }
        Tfc => { g[TFL] = H; g[TFR] = H; }
        Tc => { g[TFL] = 0.5; g[TFR] = 0.5; g[TRL] = 0.5; g[TRR] = 0.5; }
    }
    g
}

#[derive(Clone, Copy, PartialEq)]
enum Kind { Unused, Bed(usize), Object }

/// x, y, z, linear gain, elevation flag
#[derive(Clone, Copy)]
struct State { p: [f32; 4], elev: bool, kind: Kind }

impl State {
    const SILENT: State = State { p: [0.0, 0.0, 0.0, 0.0], elev: true, kind: Kind::Unused };
    fn gains(&self) -> [f32; OUT] {
        let mut g = match self.kind {
            Kind::Unused => return [0.0; OUT],
            Kind::Bed(l) => bed_gains(l),
            Kind::Object => pan(self.p[0], self.p[1], self.p[2], self.elev),
        };
        for v in &mut g { *v *= self.p[3]; }
        g
    }
}

/// One channel's ramp from `from` to `to` starting at sample `start` over `len` samples.
#[derive(Clone, Copy)]
struct Track { from: State, to: State, start: u64, len: u64 }

impl Track {
    fn at(&self, t: u64) -> State {
        if self.len == 0 || t >= self.start + self.len || self.from.kind != self.to.kind {
            return if t >= self.start { self.to } else { self.from };
        }
        if t <= self.start { return self.from; }
        let f = (t - self.start) as f32 / self.len as f32;
        let mut s = self.to;
        for i in 0..4 { s.p[i] = self.from.p[i] + (self.to.p[i] - self.from.p[i]) * f; }
        s
    }
}

struct Event { pos: u64, ramp: u64, states: Vec<State> }

struct Renderer {
    tracks: [Track; MAX_CH],
    pending: VecDeque<Event>,
    pos: u64,               // output samples rendered so far
    g0: [[f32; OUT]; MAX_CH], // gains at the current tick
    labels: Option<Vec<ChannelLabel>>, // non-object mode
    gain: f32,
}

impl Renderer {
    fn new(gain: f32) -> Self {
        let t = Track { from: State::SILENT, to: State::SILENT, start: 0, len: 0 };
        Renderer { tracks: [t; MAX_CH], pending: VecDeque::new(), pos: 0,
                   g0: [[0.0; OUT]; MAX_CH], labels: None, gain }
    }

    /// Queue an OAMD payload that belongs to the access unit starting at `au_pos`.
    fn push_oamd(&mut self, o: &ObjectAudioMetadataPayload, au_pos: u64) {
        let Some(el) = o.object_element.as_ref() else { return };
        let pa = &o.program_assignment;
        let beds: Vec<usize> = pa.bed_assignment.first().map(|b| b.to_index_vec()).unwrap_or_default();
        let pos3d = o.get_damf_pos();
        let mut states = Vec::with_capacity(o.object_count.min(MAX_CH));
        for i in 0..o.object_count.min(MAX_CH) {
            let Some(blk) = el.object_data.get(i).and_then(|b| b.first()) else {
                states.push(State::SILENT); continue;
            };
            let db = blk.object_basic_info.object_gain;
            let lin = if blk.b_object_not_active || db <= -128 { 0.0 } else { 10f32.powf(db as f32 / 20.0) };
            let kind = if i < beds.len() { Kind::Bed(beds[i]) }
                       else if i < pa.num_bed_objects + pa.num_isf_objects { Kind::Unused }
                       else { Kind::Object };
            let p = pos3d.get(i).and_then(|b| b.first()).copied().unwrap_or([0.0, 1.0, 0.0]);
            states.push(State { p: [p[0] as f32, p[1] as f32, p[2] as f32, lin],
                                elev: blk.object_render_info.b_enable_elevation, kind });
        }
        let ramp = el.md_update_info.block_update_info.first().map(|b| b.ramp_duration as u64).unwrap_or(0);
        let pos = au_pos + o.evo_sample_offset + el.md_update_info.sample_offset as u64;
        let idx = self.pending.iter().position(|e| e.pos > pos).unwrap_or(self.pending.len());
        self.pending.insert(idx, Event { pos, ramp, states });
    }

    fn apply_due(&mut self, t: u64) {
        while self.pending.front().is_some_and(|e| e.pos <= t) {
            let e = self.pending.pop_front().unwrap();
            for ch in 0..MAX_CH {
                let new = e.states.get(ch).copied().unwrap_or(State::SILENT);
                let cur = self.tracks[ch].at(e.pos);
                // A newly appearing object jumps to its position instead of sliding in from nowhere.
                let from = if cur.kind == new.kind { cur } else { State { p: [new.p[0], new.p[1], new.p[2], cur.p[3]], ..new } };
                self.tracks[ch] = Track { from, to: new, start: e.pos, len: e.ramp };
            }
        }
    }

    fn gains_at(&mut self, t: u64) -> [[f32; OUT]; MAX_CH] {
        self.apply_due(t);
        let mut g = [[0f32; OUT]; MAX_CH];
        if let Some(labels) = &self.labels {
            for (ch, l) in labels.iter().enumerate().take(MAX_CH) { g[ch] = label_gains(l); }
        } else {
            for ch in 0..MAX_CH { g[ch] = self.tracks[ch].at(t).gains(); }
        }
        g
    }

    /// Renders `n` samples of `pcm` (sample-major i32 24-bit) with `chans` channels.
    fn render(&mut self, pcm: &[[i32; 16]], n: usize, chans: usize, out: &mut Vec<f32>) {
        if self.pos == 0 { self.g0 = self.gains_at(0); }
        let mut i = 0;
        while i < n {
            let tick_start = self.pos - self.pos % TICK;
            let next = tick_start + TICK;
            let g1 = self.gains_at(next);
            let run = ((next - self.pos) as usize).min(n - i);
            for k in 0..run {
                let f = ((self.pos + k as u64 - tick_start) as f32) / TICK as f32;
                let mut frame = [0f32; OUT];
                let s = &pcm[i + k];
                for ch in 0..chans.min(MAX_CH) {
                    let v = s[ch] as f32 * (self.gain / 8_388_608.0);
                    if v == 0.0 { continue; }
                    let (a, b) = (&self.g0[ch], &g1[ch]);
                    for o in 0..OUT { frame[o] += v * (a[o] + (b[o] - a[o]) * f); }
                }
                out.extend_from_slice(&frame);
            }
            i += run;
            self.pos += run as u64;
            if self.pos == next { self.g0 = g1; }
        }
    }

    fn silence(&mut self, n: usize, out: &mut Vec<f32>) {
        out.resize(out.len() + n * OUT, 0.0);
        self.pos += n as u64;
        self.apply_due(self.pos);
        self.g0 = self.gains_at(self.pos - self.pos % TICK);
    }
}

fn main() {
    let mut gain_db = 0f32;
    let mut args = std::env::args().skip(1);
    while let Some(a) = args.next() {
        match a.as_str() {
            "--positions" => { println!("FL,FR,FC,LFE,RL,RR,SL,SR,TFL,TFR,TRL,TRR"); return; }
            "-g" | "--gain" => gain_db = args.next().and_then(|v| v.parse().ok()).unwrap_or(0.0),
            "-" => {}
            _ => {
                eprintln!("usage: thd-atmos [--gain dB] [--positions] < truehd-stream > f32le-7.1.4");
                std::process::exit(2);
            }
        }
    }
    if let Err(e) = run(10f32.powf(gain_db / 20.0)) {
        if e.kind() != io::ErrorKind::BrokenPipe { eprintln!("thd-atmos: {e}"); std::process::exit(1); }
    }
}

/// Output-side state: holds audio until the stream's layout is known.
struct Sink<W: Write> {
    r: Renderer,
    w: W,
    buf: Vec<f32>,
    held: Vec<DecodedAccessUnit>,
    held_samples: usize,
    decided: bool,
    spa: usize,          // samples per access unit (for silence on skipped frames)
    lost: usize,         // frames skipped before we knew `spa`
    announced: bool,
}

impl<W: Write> Sink<W> {
    fn flush_buf(&mut self) -> io::Result<()> {
        if self.buf.is_empty() { return Ok(()); }
        let bytes: Vec<u8> = self.buf.iter().flat_map(|v| v.to_le_bytes()).collect();
        self.buf.clear();
        self.w.write_all(&bytes)
    }

    fn lost_frame(&mut self) -> io::Result<()> {
        if self.spa == 0 { self.lost += 1; return Ok(()); }
        if self.decided { self.r.silence(self.spa, &mut self.buf); self.flush_buf() }
        else { self.lost += 1; Ok(()) }
    }

    fn emit(&mut self, d: &DecodedAccessUnit) {
        let au_pos = self.r.pos;
        for o in &d.oamd { self.r.push_oamd(o, au_pos); }
        self.r.render(&d.pcm_data, d.sample_length, d.channel_count, &mut self.buf);
    }

    fn decide(&mut self, objects: bool, labels: Vec<ChannelLabel>) -> io::Result<()> {
        self.decided = true;
        if !objects { self.r.labels = Some(labels); }
        if !self.announced {
            eprintln!("thd-atmos: {}", if objects { "Atmos objects -> 7.1.4" } else { "no Atmos metadata, channel-based -> 7.1.4" });
            self.announced = true;
        }
        let lost = std::mem::take(&mut self.lost);
        if lost > 0 { self.r.silence(lost * self.spa, &mut self.buf); }
        for d in std::mem::take(&mut self.held) { self.emit(&d); }
        self.held_samples = 0;
        self.flush_buf()
    }

    fn push(&mut self, d: DecodedAccessUnit) -> io::Result<()> {
        if d.is_duplicate { return Ok(()); }
        if self.spa == 0 { self.spa = d.sample_length.max(1); }
        if self.decided { self.emit(&d); return self.flush_buf(); }
        let has_oamd = !d.oamd.is_empty();
        let labels = d.channel_labels.clone();
        self.held_samples += d.sample_length;
        self.held.push(d);
        if has_oamd { self.decide(true, labels) }
        else if self.held_samples >= MAX_HOLD_SAMPLES { self.decide(false, labels) }
        else { Ok(()) }
    }
}

fn run(gain: f32) -> io::Result<()> {
    let mut sink = Sink { r: Renderer::new(gain), w: io::BufWriter::with_capacity(1 << 16, io::stdout().lock()),
                          buf: Vec::with_capacity(1 << 14), held: Vec::new(), held_samples: 0,
                          decided: false, spa: 0, lost: 0, announced: false };
    let mut extractor = Extractor::default();
    let mut parser = Parser::default();
    let mut decoder = Decoder::default();
    let mut resyncing = false;
    let mut stdin = io::stdin().lock();
    let mut chunk = vec![0u8; 1 << 16];

    loop {
        let n = match stdin.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => n,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
            Err(e) => return Err(e),
        };
        extractor.push_bytes(&chunk[..n]);
        for fr in extractor.by_ref() {
            let frame = match fr {
                Ok(f) => f,
                Err(ExtractError::InsufficientData) => break,
                Err(_) => { sink.lost_frame()?; continue; }
            };
            if resyncing && !frame.is_major_sync() { sink.lost_frame()?; continue; }
            let au = match parser.parse(&frame) {
                Ok(au) => au,
                Err(_) => {
                    if !resyncing { parser.reset_for_next_major_sync(); decoder.reset_for_next_major_sync(); resyncing = true; }
                    sink.lost_frame()?;
                    continue;
                }
            };
            match decoder.decode_presentation(&au, 3) {
                Ok(d) => { resyncing = false; sink.push(d)?; }
                Err(_) => {
                    parser.reset_for_next_major_sync(); decoder.reset_for_next_major_sync(); resyncing = true;
                    sink.lost_frame()?;
                }
            }
        }
    }
    if !sink.decided && !sink.held.is_empty() {
        let labels = sink.held[0].channel_labels.clone();
        sink.decide(false, labels)?;
    }
    sink.flush_buf()?;
    sink.w.flush()
}

#[cfg(test)]
mod tests {
    use super::*;
    fn top(g: &[f32; OUT]) -> Vec<(usize, f32)> {
        g.iter().copied().enumerate().filter(|(_, v)| *v > 0.05).map(|(i, v)| (i, (v * 100.0).round() / 100.0)).collect()
    }
    #[test]
    fn panner() {
        assert_eq!(top(&pan(-1.0, 1.0, 0.0, true)), vec![(FL, 1.0)]);
        assert_eq!(top(&pan(0.0, 1.0, 0.0, true)), vec![(FC, 1.0)]);
        assert_eq!(top(&pan(1.0, 0.0, 0.0, true)), vec![(SR, 1.0)]);
        assert_eq!(top(&pan(-1.0, -1.0, 0.0, true)), vec![(RL, 1.0)]);
        assert_eq!(top(&pan(1.0, 1.0, 1.0, true)), vec![(TFR, 1.0)]);
        assert_eq!(top(&pan(-1.0, -1.0, 1.0, true)), vec![(TRL, 1.0)]);
        assert_eq!(top(&pan(1.0, 1.0, 1.0, false)), vec![(FR, 1.0)]);
        for (x, y, z) in [(0.3, 0.2, 0.4), (-0.7, -0.5, 0.9), (0.0, 0.0, 0.0), (0.1, -0.9, 0.2)] {
            let p: f32 = pan(x, y, z, true).iter().map(|v| v * v).sum();
            assert!((p - 1.0).abs() < 1e-4, "power {p} at {x},{y},{z}");
        }
        println!("mid-top-left: {:?}", top(&pan(-1.0, 0.0, 0.5, true)));
    }
    #[test]
    fn oamd_sample() {
        let o = ObjectAudioMetadataPayload::read(truehd::structs::oamd::TEST_DATA_TRIM).unwrap();
        let mut r = Renderer::new(1.0);
        r.push_oamd(&o, 0);
        r.apply_due(100_000);
        for ch in 0..MAX_CH {
            let s = r.tracks[ch].to;
            let k = match s.kind { Kind::Unused => "-".to_string(), Kind::Bed(l) => format!("bed{l}"), Kind::Object => "obj".into() };
            println!("ch{ch:2} {k:5} pos {:?} gain {:.2} -> {:?}", s.p, s.p[3], top(&s.gains()));
        }
        assert!(matches!(r.tracks[0].to.kind, Kind::Bed(3)));
    }
}
