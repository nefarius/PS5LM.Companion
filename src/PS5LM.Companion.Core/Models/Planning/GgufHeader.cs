using System.Buffers.Binary;
using System.Text;

namespace PS5LM.Companion.Core.Models.Planning;

/// <summary>
/// Reads a GGUF metadata section from a stream and stops before the tensor info.
/// A port of <c>parse_model_info</c> in PS5LM's <c>ps5/app/model_plan.cpp</c>
/// at <c>707768548dae7e4c9de29fb94a72ae03c383a46f</c>. The stream does not need to be seekable.
/// </summary>
public static class GgufHeader
{
    private const ulong MaxStringBytes = 1u << 24;

    private const ulong MaxCountedArray = 4096;

    public static async Task<GgufModelInfo> ReadAsync(Stream stream, string name, ulong fileBytes, CancellationToken cancellationToken)
    {
        var fileName = FileNameOf(name);
        var reader = new Reader(stream);
        if (!await reader.ReadExactAsync(reader.Scratch.AsMemory(0, 4), cancellationToken)
            || reader.Scratch[0] != (byte)'G'
            || reader.Scratch[1] != (byte)'G'
            || reader.Scratch[2] != (byte)'U'
            || reader.Scratch[3] != (byte)'F')
        {
            return Failed(fileName, fileBytes, "not a GGUF file");
        }

        _ = await reader.U32Async(cancellationToken);
        _ = await reader.U64Async(cancellationToken);
        var count = await reader.U64Async(cancellationToken);
        var ints = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var sums = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (ulong i = 0; i < count && !reader.Bad; i++)
        {
            var key = await reader.StringAsync(cancellationToken);
            var type = await reader.U32Async(cancellationToken);
            if (reader.Bad)
            {
                break;
            }

            if (type == (uint)ValueType.String)
            {
                strings[key] = await reader.StringAsync(cancellationToken);
            }
            else if (type == (uint)ValueType.Array)
            {
                await ReadArrayAsync(reader, key, ints, sums, cancellationToken);
            }
            else
            {
                ints[key] = await reader.ScalarAsync(type, cancellationToken);
            }
        }

        if (reader.Bad)
        {
            return Failed(fileName, fileBytes, "metadata could not be read");
        }

        var architecture = strings.GetValueOrDefault("general.architecture") ?? string.Empty;
        var modelName = strings.GetValueOrDefault("general.name");
        if (string.IsNullOrEmpty(modelName))
        {
            modelName = fileName;
        }

        if (ints.ContainsKey("prism.hadamard.version") && ints.TryGetValue("general.file_type", out var fileType) && fileType is 141 or 143)
        {
            var error = fileType == 141
                ? "PrismML's PQ2_0: convert it to Q2_0 on a PC with scripts/bonsai-pq2-to-q2.py"
                : "PrismML's PTQ1_0, which llama.cpp cannot load; get its Q2_0 file";
            return Failed(fileName, fileBytes, error, modelName, architecture);
        }

        var prefix = architecture + ".";
        ulong Num(string key, ulong fallback = 0) => ints.TryGetValue(prefix + key, out var value) ? value : fallback;

        var layers = (uint)(Num("block_count") - Num("nextn_predict_layers"));
        var heads = Num("attention.head_count");
        var embedding = Num("embedding_length");
        var headDim = heads == 0 ? 0 : embedding / heads;
        var interval = (uint)Math.Max(1UL, Num("full_attention_interval", 1));
        var kvLayers = interval > 1 ? layers / interval : layers;
        var swaLayers = 0u;
        var swaWindow = 0u;
        if (sums.TryGetValue(prefix + "attention.sliding_window_pattern", out var pattern) && Num("attention.sliding_window") > 0)
        {
            swaLayers = (uint)Math.Min(pattern, kvLayers);
            swaWindow = (uint)Num("attention.sliding_window");
        }

        var inner = Num("ssm.inner_size");
        var state = Num("ssm.state_size");
        var stateBytes = 0ul;
        if (inner > 0 && state > 0)
        {
            var groups = Num("ssm.group_count", 1);
            var conv = Num("ssm.conv_kernel", 4);
            var recurrent = (ulong)layers - kvLayers;
            stateBytes = recurrent * (inner * state + (conv - 1) * (inner + (2 * groups * state))) * 4;
        }

        var info = new GgufModelInfo
        {
            FileName = fileName,
            Name = modelName,
            Architecture = architecture,
            Decision = strings.GetValueOrDefault(prefix + "decision.type") ?? string.Empty,
            FileBytes = fileBytes,
            LayerCount = layers,
            FeedForwardLength = (uint)Num("feed_forward_length"),
            KeyValueHeads = (uint)Num("attention.head_count_kv", heads),
            KeyLength = (uint)Num("attention.key_length", headDim),
            ValueLength = (uint)Num("attention.value_length", headDim),
            FullAttentionInterval = interval,
            SlidingWindowLayers = swaLayers,
            SlidingWindow = swaWindow,
            TrainedContext = (uint)Num("context_length"),
            ExpertCount = (uint)Num("expert_count"),
            StateBytes = stateBytes,
            Ok = layers > 0,
            Error = layers > 0 ? null : "no layer count for architecture '" + architecture + "'",
        };
        return info;
    }

    private static async Task ReadArrayAsync(
        Reader reader,
        string key,
        Dictionary<string, ulong> ints,
        Dictionary<string, ulong> sums,
        CancellationToken cancellationToken)
    {
        var element = await reader.U32Async(cancellationToken);
        var count = await reader.U64Async(cancellationToken);
        if (reader.Bad)
        {
            return;
        }

        if (element == (uint)ValueType.String)
        {
            for (ulong i = 0; i < count && !reader.Bad; i++)
            {
                var length = await reader.U64Async(cancellationToken);
                await reader.SkipAsync(length, cancellationToken);
            }

            return;
        }

        if (count <= MaxCountedArray)
        {
            ulong max = 0;
            ulong sum = 0;
            for (ulong i = 0; i < count && !reader.Bad; i++)
            {
                var value = await reader.ScalarAsync(element, cancellationToken);
                if (value > max)
                {
                    max = value;
                }

                sum += value;
            }

            ints[key] = max;
            sums[key] = sum;
            return;
        }

        var scalar = ScalarSize(element);
        if (scalar == 0 || count > ulong.MaxValue / scalar)
        {
            reader.Bad = true;
            return;
        }

        await reader.SkipAsync(count * scalar, cancellationToken);
    }

    private static ulong ScalarSize(uint type) => type switch
    {
        (uint)ValueType.U8 or (uint)ValueType.I8 or (uint)ValueType.Bool => 1,
        (uint)ValueType.U16 or (uint)ValueType.I16 => 2,
        (uint)ValueType.U32 or (uint)ValueType.I32 or (uint)ValueType.F32 => 4,
        (uint)ValueType.U64 or (uint)ValueType.I64 or (uint)ValueType.F64 => 8,
        _ => 0,
    };

    private static string FileNameOf(string name)
    {
        var slash = name.LastIndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }

    private static GgufModelInfo Failed(string fileName, ulong fileBytes, string error, string? name = null, string? architecture = null) =>
        new()
        {
            FileName = fileName,
            Name = string.IsNullOrEmpty(name) ? fileName : name,
            Architecture = architecture ?? string.Empty,
            FileBytes = fileBytes,
            Error = error,
        };

    /// <summary>GGUF value types, in the order llama.cpp numbers them.</summary>
    private enum ValueType : uint
    {
        U8 = 0,
        I8 = 1,
        U16 = 2,
        I16 = 3,
        U32 = 4,
        I32 = 5,
        F32 = 6,
        Bool = 7,
        String = 8,
        Array = 9,
        U64 = 10,
        I64 = 11,
        F64 = 12,
    }

    private sealed class Reader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[81920];

        private int _filled;

        private int _used;

        public byte[] Scratch { get; } = new byte[8];

        public bool Bad { get; set; }

        public async Task<uint> U32Async(CancellationToken cancellationToken)
        {
            if (!await ReadExactAsync(Scratch.AsMemory(0, 4), cancellationToken))
            {
                Bad = true;
                return 0;
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(Scratch);
        }

        public async Task<ulong> U64Async(CancellationToken cancellationToken)
        {
            if (!await ReadExactAsync(Scratch.AsMemory(0, 8), cancellationToken))
            {
                Bad = true;
                return 0;
            }

            return BinaryPrimitives.ReadUInt64LittleEndian(Scratch);
        }

        public async Task<string> StringAsync(CancellationToken cancellationToken)
        {
            var length = await U64Async(cancellationToken);
            if (Bad || length > MaxStringBytes)
            {
                Bad = true;
                return string.Empty;
            }

            if (length == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[length];
            if (!await ReadExactAsync(bytes, cancellationToken))
            {
                Bad = true;
                return string.Empty;
            }

            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>A scalar as an integer. Floats are truncated toward zero. Strings and arrays are 0.</summary>
        public async Task<ulong> ScalarAsync(uint type, CancellationToken cancellationToken)
        {
            var size = ScalarSize(type);
            if (size == 0 || !await ReadExactAsync(Scratch.AsMemory(0, (int)size), cancellationToken))
            {
                Bad = true;
                return 0;
            }

            return type switch
            {
                (uint)ValueType.U8 or (uint)ValueType.Bool => Scratch[0],
                (uint)ValueType.I8 => unchecked((ulong)(sbyte)Scratch[0]),
                (uint)ValueType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(Scratch),
                (uint)ValueType.I16 => unchecked((ulong)BinaryPrimitives.ReadInt16LittleEndian(Scratch)),
                (uint)ValueType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(Scratch),
                (uint)ValueType.I32 => unchecked((ulong)BinaryPrimitives.ReadInt32LittleEndian(Scratch)),
                (uint)ValueType.U64 => BinaryPrimitives.ReadUInt64LittleEndian(Scratch),
                (uint)ValueType.I64 => unchecked((ulong)BinaryPrimitives.ReadInt64LittleEndian(Scratch)),
                (uint)ValueType.F32 => FloatToUInt(BinaryPrimitives.ReadSingleLittleEndian(Scratch)),
                (uint)ValueType.F64 => FloatToUInt(BinaryPrimitives.ReadDoubleLittleEndian(Scratch)),
                _ => 0,
            };
        }

        public async Task SkipAsync(ulong count, CancellationToken cancellationToken)
        {
            while (count > 0 && !Bad)
            {
                if (_used == _filled)
                {
                    _filled = await stream.ReadAsync(_buffer, cancellationToken);
                    _used = 0;
                    if (_filled == 0)
                    {
                        Bad = true;
                        return;
                    }
                }

                var step = (int)Math.Min(count, (ulong)(_filled - _used));
                _used += step;
                count -= (ulong)step;
            }
        }

        public async Task<bool> ReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var written = 0;
            while (written < destination.Length)
            {
                if (_used == _filled)
                {
                    _filled = await stream.ReadAsync(_buffer, cancellationToken);
                    _used = 0;
                    if (_filled == 0)
                    {
                        return false;
                    }
                }

                var step = Math.Min(destination.Length - written, _filled - _used);
                _buffer.AsMemory(_used, step).CopyTo(destination[written..]);
                _used += step;
                written += step;
            }

            return true;
        }

        private static ulong FloatToUInt(double value)
        {
            if (double.IsNaN(value) || value <= 0)
            {
                return 0;
            }

            return value >= ulong.MaxValue ? ulong.MaxValue : (ulong)value;
        }
    }
}
