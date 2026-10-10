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
          -h, --help            Show this help
        """;

    static int Main(string[] args) {
        string layoutName = "7.1.4", input = null;
        bool infoOnly = false, positionsOnly = false;
        float gainDb = 0;

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
        if (input == null) {
            Console.Error.WriteLine(usage);
            return 2;
        }

        try {
            return Decode(input, layout, infoOnly, MathF.Pow(10, gainDb / 20));
        } catch (Exception e) {
            return Fail(e.Message);
        }
    }

    static int Decode(string input, ReferenceChannel[] layout, bool infoOnly, float gain) {
        // Never load the global Cavern config (~/.config/Cavern/Save.dat): the output
        // layout must be exactly what the PipeWire sink expects.
        Listener.ReplaceChannels(CreateLayout(layout));

        using Stream source = input == "-" ? ReadAllStdin() :
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
        long remaining = reader.Length > 0 ? reader.Length : long.MaxValue;
        byte[] bytes = null;
        while (remaining > 0) {
            float[] frame = listener.Render();
            int samples = frame.Length;
            if (remaining < listener.UpdateRate) { // Trim the padding of the last block
                samples = (int)remaining * layout.Length;
            }
            bytes ??= new byte[frame.Length * sizeof(float)];
            Buffer.BlockCopy(frame, 0, bytes, 0, samples * sizeof(float));
            try {
                stdout.Write(bytes, 0, samples * sizeof(float));
            } catch (IOException) {
                return 0; // Downstream closed the pipe (player stopped) - normal termination
            }
            remaining -= listener.UpdateRate;
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

    /// <summary>
    /// Cavern needs a seekable stream to know where a raw E-AC-3 stream ends (it zero-fills reads
    /// past the end instead of reporting it), so piped input is buffered. Even a long Atmos music
    /// track is only a few tens of megabytes.
    /// </summary>
    static MemoryStream ReadAllStdin() {
        MemoryStream buffer = new();
        using (Stream stdin = Console.OpenStandardInput()) {
            stdin.CopyTo(buffer);
        }
        buffer.Position = 0;
        return buffer;
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
