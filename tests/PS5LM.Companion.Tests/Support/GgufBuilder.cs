using System.Buffers.Binary;
using System.Text;

namespace PS5LM.Companion.Tests.Support;

/// <summary>A tiny GGUF header, string and uint32 metadata only, matching PS5LM's model_plan_test.</summary>
internal sealed class GgufBuilder
{
    private readonly MemoryStream _values = new();

    private ulong _count;

    public GgufBuilder Kv(string key, string value)
    {
        WriteString(_values, key);
        WriteU32(_values, 8);
        WriteString(_values, value);
        _count++;
        return this;
    }

    public GgufBuilder Kv(string key, uint value)
    {
        WriteString(_values, key);
        WriteU32(_values, 4);
        WriteU32(_values, value);
        _count++;
        return this;
    }

    public GgufBuilder Kv(string key, bool[] values)
    {
        WriteString(_values, key);
        WriteU32(_values, 9);
        WriteU32(_values, 7);
        WriteU64(_values, (ulong)values.Length);
        foreach (var value in values)
        {
            _values.WriteByte(value ? (byte)1 : (byte)0);
        }

        _count++;
        return this;
    }

    public byte[] Bytes()
    {
        var stream = new MemoryStream();
        stream.Write("GGUF"u8);
        WriteU32(stream, 3);
        WriteU64(stream, 0);
        WriteU64(stream, _count);
        _values.Position = 0;
        _values.CopyTo(stream);
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteU64(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteU64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
