using System.Buffers.Binary;
using System.IO.Compression;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.LogStreaming;

internal static class LogStreamChunkEncoding
{
    private const int MaximumSize = LogStreamSenderOptions.DefaultChunkSize;
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static void SetEncodedData(LogStreamNetworkMessage message, byte[] raw, bool receiverSupportsCompression)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length > MaximumSize) throw new ArgumentOutOfRangeException(nameof(raw));
        message.Data = raw;
        message.DataEncoding = "";
        if (!receiverSupportsCompression || raw.Length < 512) return;
        using var output = new MemoryStream(raw.Length + 128);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(raw);
        if (output.Length > raw.Length - 64) return;
        message.Data = output.ToArray();
        message.DataEncoding = "gzip";
    }

    public static bool TryDecode(LogStreamNetworkMessage message, out byte[] raw)
    {
        raw = [];
        if (message.Data is not { Length: <= LogStreamSenderOptions.MaximumChunkSize } data) return false;
        if (message.DataEncoding == "")
        {
            raw = data;
            return true;
        }
        if (message.DataEncoding != "gzip" || data.Length < 20 || data.Length > MaximumSize
            || data[0] != 0x1f || data[1] != 0x8b || data[2] != 8 || data[3] != 0) return false;
        uint expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4));
        if (expectedSize > MaximumSize) return false;
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var buffer = new byte[MaximumSize + 1];
            int length = 0;
            while (length <= MaximumSize)
            {
                int read = gzip.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumSize || length != expectedSize) return false;
            // Some truncated gzip members reach end of stream without throwing.
            uint checksum = uint.MaxValue;
            for (int index = 0; index < length; index++)
                checksum = CrcTable[(checksum ^ buffer[index]) & 0xff] ^ (checksum >> 8);
            if (~checksum != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 8))) return false;
            raw = buffer.AsSpan(0, length).ToArray();
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[index] = value;
        }
        return table;
    }
}
