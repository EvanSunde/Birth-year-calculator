// atmos-decode: decode a Dolby Atmos stream with Cavern and write speaker-rendered
// PCM (interleaved 32-bit float, little endian) to stdout.
//
// This is the Linux replacement for the part of the Windows pipeline that Dolby Access
// does NOT ship itself: on Windows the E-AC-3 JOC decoder lives in Media
// Foundation and hands audio objects to the Spatial Sound engine. Here Cavern decodes
// the objects and renders them onto a fixed 7.1.4 speaker bed, which the PipeWire
// "Atmos Headphones" filter-chain then binauralises (the job DolbyHrtfEnc.dll does on Windows).

using Cavern;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Renderers;

namespace AtmosDecode;

static class Program {
    static readonly Dictionary<string, ReferenceChannel[]> layouts = new() {
        ["7.1.4"] = ChannelPrototype.ref714,
        ["7.1.2"] = ChannelPrototype.ref712,
        ["5.1.4"] = ChannelPrototype.ref514,
        ["7.1"] = ChannelPrototype.ref710,
        ["5.1"] = ChannelPrototype.ref510,
        ["2.0"] = ChannelPrototype.ref200,
    };

    // Cavern channel -> PipeWire/SPA channel position name.
    static readonly Dictionary<ReferenceChannel, string> pipewireNames = new() {
        [ReferenceChannel.FrontLeft] = "FL",
        [ReferenceChannel.FrontRight] = "FR",
        [ReferenceChannel.FrontCenter] = "FC",
        [ReferenceChannel.ScreenLFE] = "LFE",
        [ReferenceChannel.RearLeft] = "RL",
        [ReferenceChannel.RearRight] = "RR",
        [ReferenceChannel.SideLeft] = "SL",
        [ReferenceChannel.SideRight] = "SR",
        [ReferenceChannel.TopFrontLeft] = "TFL",
        [ReferenceChannel.TopFrontRight] = "TFR",
        [ReferenceChannel.TopRearLeft] = "TRL",
        [ReferenceChannel.TopRearRight] = "TRR",
    };

    /// <summary>
    /// Delay of the JOC quadrature mirror filter bank (640-tap prototype, 64 bands): 640 - 64 samples.
    /// </summary>
    const int jocLatency = 576;

    const string usage = """
        Usage: atmos-decode [options] <input|->

        Decodes Dolby Atmos (E-AC-3 + JOC, Dolby Atmos Master Format .atmos) and writes
        rendered PCM as interleaved float32 LE to stdout. Containers: MKV, MP4 (not all
        muxers), raw .eac3/.ec3. Use "-" to read a raw E-AC-3 elementary stream from stdin,
        e.g.  ffmpeg -i song.m4a -map 0:a:0 -c copy -f eac3 - | atmos-decode -

        Options:
          -l, --layout <name>   Output speaker layout: 7.1.4 (default), 7.1.2, 5.1.4, 7.1, 5.1, 2.0
          -i, --info            Print stream information to stdout and exit
              --positions       Print the PipeWire channel map of --layout and exit
          -g, --gain <dB>       Output gain in dB (default 0)
              --mka <seconds>   Write a live Matroska stream (float PCM with its channel layout)
                                whose first sample has this timestamp, instead of raw PCM. Lets
                                players such as mpv take it as an extra, time-aligned audio track
              --title <name>    Track name in --mka mode (default "Atmos <layout> (live)")
              --wrap-pcm <rate> Don't decode: wrap raw float PCM of --layout from stdin into the
                                --mka stream (for other decoders, e.g. thd-atmos)
          -h, --help            Show this help

        Decoding by Cavern, (c) VoidX - http://en.sbence.hu - https://github.com/VoidXH/Cavern
        """;

    static int Main(string[] args) {
        string layoutName = "7.1.4", input = null;
        bool infoOnly = false, positionsOnly = false;
        float gainDb = 0;
        double? mkaStart = null;
        string title = null;
        int wrapRate = 0;

        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "-l":
                case "--layout":
                    layoutName = NextArg(args, ref i);
                    break;
                case "-i":
                case "--info":
                    infoOnly = true;
                    break;
                case "--positions":
                    positionsOnly = true;
                    break;
                case "-g":
                case "--gain":
                    gainDb = float.Parse(NextArg(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--mka":
                    mkaStart = double.Parse(NextArg(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--title":
                    title = NextArg(args, ref i);
                    break;
                case "--wrap-pcm":
                    wrapRate = int.Parse(NextArg(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "-h":
                case "--help":
                    Console.WriteLine(usage);
                    return 0;
                default:
                    if (input != null) {
                        return Fail($"Unexpected argument: {args[i]}");
                    }
                    input = args[i];
                    break;
            }
        }

        if (!layouts.TryGetValue(layoutName, out ReferenceChannel[] layout)) {
            return Fail($"Unknown layout '{layoutName}'. Valid: {string.Join(", ", layouts.Keys)}");
        }
        if (positionsOnly) {
            Console.WriteLine(string.Join(",", layout.Select(c => pipewireNames[c])));
            return 0;
        }
        if (wrapRate > 0) {
            return mkaStart.HasValue ? WrapPcm(layout, wrapRate, mkaStart.Value, title ?? $"Atmos {layoutName} (live)")
                : Fail("--wrap-pcm needs --mka");
        }
        if (input == null) {
            Console.Error.WriteLine(usage);
            return 2;
        }

        try {
            return Decode(input, layout, infoOnly, MathF.Pow(10, gainDb / 20), mkaStart, title ?? $"Atmos {layoutName} (live)");
        } catch (Exception e) {
            return Fail(e.Message);
        }
    }

    static int Decode(string input, ReferenceChannel[] layout, bool infoOnly, float gain, double? mkaStart, string title) {
        // Never load the global Cavern config (~/.config/Cavern/Save.dat): the output
        // layout must be exactly what the PipeWire sink expects.
        Listener.ReplaceChannels(CreateLayout(layout));

        LiveInput live = input == "-" ? new LiveInput(Console.OpenStandardInput()) : null;
        using Stream source = live ?? (Stream)
            new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        AudioReader reader = AudioReader.Open(source);
        Renderer renderer = reader.GetRenderer();

        if (infoOnly) {
            Console.WriteLine($"format={reader.GetType().Name.Replace("Reader", "")}");
            Console.WriteLine($"sample_rate={reader.SampleRate}");
            Console.WriteLine($"coded_channels={reader.ChannelCount}");
            Console.WriteLine($"has_objects={renderer.HasObjects.ToString().ToLowerInvariant()}");
            Console.WriteLine($"sources={renderer.Objects.Count}");
            Console.WriteLine($"length_samples={reader.Length}");
            return 0;
        }

        Listener listener = new Listener(false) {
            SampleRate = reader.SampleRate,
            UpdateRate = 256,
            AudioQuality = QualityModes.Perfect,
            // Decode, don't bass-manage: only LFE-tagged content goes to the LFE channel, unfiltered.
            // (Cavern's default lowpasses every source into the LFE for a subwoofer.)
            LFESeparation = true,
            DirectLFE = true,
            // Same rule Cavern's own pipe server uses: without 4 overhead channels,
            // leave headroom so folded-down height objects don't clip.
            Volume = gain * (renderer.HasObjects && Listener.Channels.Count(c => c.X < 0) < 4 ? .707f : 1),
        };
        listener.AttachSources(renderer.Objects);

        Console.Error.WriteLine($"atmos-decode: {reader.GetType().Name.Replace("Reader", "")}, " +
            $"{reader.SampleRate} Hz, {renderer.Objects.Count} sources" +
            (renderer.HasObjects ? " (object-based Atmos)" : " (channel-based)") +
            $" -> {Listener.GetLayoutName()}");

        using Stream stdout = Console.OpenStandardOutput();
        MatroskaWriter mka = null;
        // Piped input is decoded while it arrives (real time). Its length is only known once the
        // pipe has ended: LiveInput counts the E-AC-3 frames that went through it.
        long remaining = live == null && reader.Length > 0 ? reader.Length : long.MaxValue;
        long written = 0;
        int tailBlocks = 2 * 1536 / listener.UpdateRate + 2; // fallback if frames can't be counted
        byte[] bytes = null;
        while (remaining > 0) {
            float[] frame = listener.Render();
            int samples = frame.Length;
            if (remaining < listener.UpdateRate) { // Trim the padding of the last block
                samples = (int)remaining * layout.Length;
            }
            if (live != null && live.Ended) {
                if (live.Samples >= 0) {
                    long left = live.Samples - written;
                    if (left <= 0) {
                        break;
                    }
                    if (left < listener.UpdateRate) {
                        samples = (int)left * layout.Length;
                    }
                } else if (--tailBlocks < 0) {
                    break;
                }
            }
            try {
                if (mkaStart.HasValue) {
                    // Joint Object Coding goes through a QMF bank whose analysis + synthesis delays the
                    // audio by 576 samples (Cavern doesn't compensate it): shift the timestamps so the
                    // decoded objects line up with the film and the core audio.
                    double latency = renderer is EnhancedAC3Renderer && renderer.HasObjects ? jocLatency / (double)reader.SampleRate : 0;
                    mka ??= new MatroskaWriter(stdout, layout, reader.SampleRate, mkaStart.Value - latency, title);
                    mka.Write(frame, samples);
                } else {
                    bytes ??= new byte[frame.Length * sizeof(float)];
                    Buffer.BlockCopy(frame, 0, bytes, 0, samples * sizeof(float));
                    stdout.Write(bytes, 0, samples * sizeof(float));
                }
            } catch (IOException) {
                return 0; // Downstream closed the pipe (player stopped) - normal termination
            }
            remaining -= listener.UpdateRate;
            written += samples / layout.Length;
        }
        try {
            mka?.Flush();
        } catch (IOException) {
        }
        return 0;
    }

    /// <summary>
    /// Packs raw interleaved float PCM from stdin into a live Matroska stream.
    /// </summary>
    static int WrapPcm(ReferenceChannel[] layout, int sampleRate, double start, string title) {
        using Stream stdin = Console.OpenStandardInput(), stdout = Console.OpenStandardOutput();
        MatroskaWriter mka = new(stdout, layout, sampleRate, start, title);
        int frameBytes = layout.Length * sizeof(float);
        byte[] buffer = new byte[frameBytes * 1024];
        float[] samples = new float[layout.Length * 1024];
        int filled = 0, read;
        try {
            while ((read = stdin.Read(buffer, filled, buffer.Length - filled)) > 0) {
                filled += read;
                int whole = filled / frameBytes * frameBytes;
                Buffer.BlockCopy(buffer, 0, samples, 0, whole);
                mka.Write(samples, whole / sizeof(float));
                Buffer.BlockCopy(buffer, whole, buffer, 0, filled - whole);
                filled -= whole;
            }
            mka.Flush();
        } catch (IOException) {
            // Downstream closed the pipe (player stopped or seeked)
        }
        return 0;
    }

    /// <summary>
    /// Speaker positions of the output. Atmos content places side surrounds on the side walls (90°)
    /// and rear surrounds in the back corners, while Cavern's default 7.1 puts the side speakers at 110°,
    /// which would pan side content partly into the fronts. Using the Dolby 7.1.4 reference angles makes
    /// bed channels land 1:1 on their own output channel. The PipeWire sink uses the same angles.
    /// </summary>
    static Channel[] CreateLayout(ReferenceChannel[] layout) {
        Channel[] result = ChannelPrototype.ToLayout(layout);
        for (int i = 0; i < layout.Length; i++) {
            result[i] = layout[i] switch {
                ReferenceChannel.SideLeft => new Channel(0, -90),
                ReferenceChannel.SideRight => new Channel(0, 90),
                ReferenceChannel.RearLeft => new Channel(0, -135),
                ReferenceChannel.RearRight => new Channel(0, 135),
                _ => result[i]
            };
        }
        return result;
    }

    static string NextArg(string[] args, ref int i) {
        if (++i >= args.Length) {
            throw new ArgumentException($"Missing value after {args[i - 1]}");
        }
        return args[i];
    }

    static int Fail(string message) {
        Console.Error.WriteLine($"atmos-decode: {message}");
        return 1;
    }
}

/// <summary>
/// Standard input for Cavern, decoded while it is still arriving instead of after the whole
/// stream was read. Reports itself as non-seekable (so Cavern doesn't need a length) but keeps
/// a position and the last 4 MiB, which Cavern's format detection reads. Cavern zero-fills
/// reads past the end, so the end of the pipe is reported through <see cref="Ended"/>.
/// </summary>
sealed class LiveInput : Stream {
    const int window = 4 << 20;
    readonly Stream source;
    byte[] buffer = new byte[window * 2];
    long bufferStart; // stream offset of buffer[0]
    int bufferLength;
    long position;

    public LiveInput(Stream source) => this.source = source;

    /// <summary>The pipe was closed and everything in it has been read.</summary>
    public bool Ended { get; private set; }

    /// <summary>
    /// Audio samples (per channel) in the E-AC-3 frames that came through the pipe, or -1 if the
    /// stream couldn't be parsed. Final once <see cref="Ended"/> is set.
    /// </summary>
    public long Samples { get; private set; }

    long nextFrame; // stream offset of the next E-AC-3 sync frame to count

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin) {
        long target = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => throw new NotSupportedException()
        };
        if (target < bufferStart) {
            throw new NotSupportedException("Seek before the live input window");
        }
        return position = target;
    }

    public override int Read(byte[] destination, int offset, int count) {
        Fill(position + count);
        long available = bufferStart + bufferLength - position;
        int n = (int)Math.Clamp(available, 0, count);
        if (n > 0) {
            Buffer.BlockCopy(buffer, (int)(position - bufferStart), destination, offset, n);
            position += n;
        }
        return n;
    }

    /// <summary>Reads from the pipe until the buffer reaches <paramref name="end"/> or the pipe ends.</summary>
    void Fill(long end) {
        while (!Ended && bufferStart + bufferLength < end) {
            if (bufferLength == buffer.Length) {
                // Drop what is older than the seek-back window, or grow if nothing can go
                long keepFrom = Math.Max(bufferStart, Math.Min(Math.Min(position, nextFrame), bufferStart + bufferLength) - window);
                int drop = (int)(keepFrom - bufferStart);
                if (drop == 0) {
                    Array.Resize(ref buffer, buffer.Length * 2);
                } else {
                    Buffer.BlockCopy(buffer, drop, buffer, 0, bufferLength - drop);
                    bufferLength -= drop;
                    bufferStart += drop;
                }
                continue;
            }
            int read = source.Read(buffer, bufferLength, buffer.Length - bufferLength);
            if (read <= 0) {
                Ended = true;
            } else {
                bufferLength += read;
                CountFrames();
            }
        }
    }

    /// <summary>
    /// Walks the E-AC-3 sync frames received so far and adds up the samples of each independent
    /// frame (dependent substreams carry the same audio time).
    /// </summary>
    void CountFrames() {
        while (Samples >= 0 && nextFrame + 5 <= bufferStart + bufferLength) {
            int i = (int)(nextFrame - bufferStart);
            if (buffer[i] != 0x0B || buffer[i + 1] != 0x77) {
                Samples = -1; // not a plain E-AC-3 stream
                return;
            }
            int streamType = buffer[i + 2] >> 6;
            int frameBytes = ((((buffer[i + 2] & 7) << 8) | buffer[i + 3]) + 1) * 2;
            int fscod = buffer[i + 4] >> 6;
            int blocks = fscod == 3 ? 6 : new[] { 1, 2, 3, 6 }[(buffer[i + 4] >> 4) & 3];
            if (streamType != 1) {
                Samples += blocks * 256;
            }
            nextFrame += frameBytes;
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] source, int offset, int count) => throw new NotSupportedException();
}
