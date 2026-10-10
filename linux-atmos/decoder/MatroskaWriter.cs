using System.Buffers.Binary;
using System.Text;

using Cavern.Channels;

namespace AtmosDecode;

/// <summary>
/// Writes interleaved float PCM as a live (unknown length) Matroska stream with timestamps, for
/// players that take the decoded audio as a separate track (mpv's atmos-live.lua).
/// The track is A_MS/ACM with a WAVEFORMATEXTENSIBLE header, because that is the only Matroska PCM
/// mapping that carries a channel mask: plain A_PCM/FLOAT/IEEE (and raw PCM in NUT) loses the
/// layout, so the player wouldn't know which channels are the heights.
/// </summary>
sealed class MatroskaWriter {
    /// <summary>
    /// Timestamp unit in nanoseconds (1 µs).
    /// </summary>
    const long timestampScale = 1000;

    /// <summary>
    /// Samples (per channel) collected into one cluster.
    /// </summary>
    const int clusterSamples = 1024;

    readonly Stream output;
    readonly int channels;
    readonly int sampleRate;
    readonly long startMicroseconds;
    readonly byte[] pending;
    int pendingBytes;
    long samplesWritten;

    /// <summary>
    /// Writes the stream header.
    /// </summary>
    /// <param name="output">Destination</param>
    /// <param name="layout">Channels in WAVEFORMATEXTENSIBLE order</param>
    /// <param name="sampleRate">Sample rate of the PCM</param>
    /// <param name="startSeconds">Timestamp of the first sample</param>
    /// <param name="title">Track name shown by the player</param>
    public MatroskaWriter(Stream output, ReferenceChannel[] layout, int sampleRate, double startSeconds, string title) {
        this.output = output;
        channels = layout.Length;
        this.sampleRate = sampleRate;
        startMicroseconds = (long)Math.Round(startSeconds * 1_000_000);
        pending = new byte[clusterSamples * channels * sizeof(float)];

        MemoryStream header = new();
        Element(header, 0x1A45DFA3, // EBML
            UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
            String(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2));
        WriteId(header, 0x18538067); // Segment, unknown size: a live stream
        header.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        Element(header, 0x1549A966, // Info
            UInt(0x2AD7B1, timestampScale), String(0x4D80, "atmos-decode"), String(0x5741, "atmos-decode"));
        Element(header, 0x1654AE6B, // Tracks
            Bytes(0xAE, Concat( // TrackEntry
                UInt(0xD7, 1), UInt(0x73C5, 1), UInt(0x83, 2), UInt(0x9C, 0),
                String(0x536E, title), String(0x86, "A_MS/ACM"),
                Bytes(0x63A2, WaveFormatExtensible(layout, sampleRate)),
                Bytes(0xE1, Concat(Float(0xB5, sampleRate), UInt(0x9F, (ulong)channels), UInt(0x6264, 32))))));
        output.Write(header.GetBuffer(), 0, (int)header.Length);
    }

    /// <summary>
    /// Adds interleaved samples (a whole number of sample frames).
    /// </summary>
    public void Write(float[] samples, int count) {
        int offset = 0;
        while (offset < count) {
            int bytes = Math.Min((count - offset) * sizeof(float), pending.Length - pendingBytes);
            Buffer.BlockCopy(samples, offset * sizeof(float), pending, pendingBytes, bytes);
            pendingBytes += bytes;
            offset += bytes / sizeof(float);
            if (pendingBytes == pending.Length) {
                Flush();
            }
        }
    }

    /// <summary>
    /// Writes the collected samples as a cluster.
    /// </summary>
    public void Flush() {
        if (pendingBytes == 0) {
            return;
        }
        long timestamp = startMicroseconds + samplesWritten * 1_000_000 / sampleRate;
        byte[] cluster = Concat(UInt(0xE7, (ulong)timestamp), BlockHeader(pendingBytes));
        MemoryStream result = new();
        WriteId(result, 0x1F43B675);
        WriteSize(result, cluster.Length + pendingBytes);
        result.Write(cluster);
        result.Write(pending, 0, pendingBytes);
        output.Write(result.GetBuffer(), 0, (int)result.Length);
        output.Flush();
        samplesWritten += pendingBytes / (channels * sizeof(float));
        pendingBytes = 0;
    }

    /// <summary>
    /// SimpleBlock header for track 1, relative timestamp 0, keyframe, with its element header.
    /// </summary>
    static byte[] BlockHeader(int dataLength) {
        MemoryStream result = new();
        WriteId(result, 0xA3);
        WriteSize(result, 4 + dataLength);
        result.Write(new byte[] { 0x81, 0, 0, 0x80 });
        return result.ToArray();
    }

    static byte[] WaveFormatExtensible(ReferenceChannel[] layout, int sampleRate) {
        uint mask = 0;
        foreach (ReferenceChannel channel in layout) {
            uint bit = WaveMask(channel);
            if (bit <= mask) {
                throw new ArgumentException("The layout is not in WAVEFORMATEXTENSIBLE channel order.");
            }
            mask |= bit;
        }
        byte[] result = new byte[40];
        Span<byte> s = result;
        BinaryPrimitives.WriteUInt16LittleEndian(s, 0xFFFE); // WAVE_FORMAT_EXTENSIBLE
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)layout.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)(sampleRate * layout.Length * sizeof(float)));
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], (ushort)(layout.Length * sizeof(float)));
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], 22); // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(s[18..], 32); // wValidBitsPerSample
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], mask);
        // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        new byte[] { 3, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 }.CopyTo(s[24..]);
        return result;
    }

    static uint WaveMask(ReferenceChannel channel) => channel switch {
        ReferenceChannel.FrontLeft => 0x1,
        ReferenceChannel.FrontRight => 0x2,
        ReferenceChannel.FrontCenter => 0x4,
        ReferenceChannel.ScreenLFE => 0x8,
        ReferenceChannel.RearLeft => 0x10,
        ReferenceChannel.RearRight => 0x20,
        ReferenceChannel.SideLeft => 0x200,
        ReferenceChannel.SideRight => 0x400,
        ReferenceChannel.TopFrontLeft => 0x1000,
        ReferenceChannel.TopFrontRight => 0x4000,
        ReferenceChannel.TopRearLeft => 0x8000,
        ReferenceChannel.TopRearRight => 0x20000,
        _ => throw new ArgumentException($"No WAVEFORMATEXTENSIBLE position for {channel}.")
    };

    // EBML encoding helpers
    static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

    static byte[] UInt(uint id, ulong value) {
        int length = 1;
        while (length < 8 && value >> (length * 8) != 0) {
            length++;
        }
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++) {
            data[length - 1 - i] = (byte)(value >> (i * 8));
        }
        return Bytes(id, data);
    }

    static byte[] Float(uint id, double value) {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(data, value);
        return Bytes(id, data);
    }

    static byte[] String(uint id, string value) => Bytes(id, Encoding.UTF8.GetBytes(value));

    static byte[] Bytes(uint id, byte[] data) {
        MemoryStream result = new();
        WriteId(result, id);
        WriteSize(result, data.Length);
        result.Write(data);
        return result.ToArray();
    }

    static void Element(Stream output, uint id, params byte[][] children) {
        byte[] data = Concat(children);
        WriteId(output, id);
        WriteSize(output, data.Length);
        output.Write(data);
    }

    /// <summary>
    /// Element IDs already contain their length marker: write their significant bytes.
    /// </summary>
    static void WriteId(Stream output, uint id) {
        int shift = 24;
        while (shift > 0 && id >> shift == 0) {
            shift -= 8;
        }
        for (; shift >= 0; shift -= 8) {
            output.WriteByte((byte)(id >> shift));
        }
    }

    /// <summary>
    /// Writes an element size as an 8-byte EBML variable-length integer.
    /// </summary>
    static void WriteSize(Stream output, long size) {
        output.WriteByte(0x01);
        for (int shift = 48; shift >= 0; shift -= 8) {
            output.WriteByte((byte)(size >> shift));
        }
    }
}
