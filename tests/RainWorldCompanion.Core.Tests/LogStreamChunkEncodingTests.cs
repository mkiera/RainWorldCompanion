using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.Tests;

public sealed class LogStreamChunkEncodingTests
{
    [Fact]
    public void Compression_preserves_binary_data_and_original_hash()
    {
        byte[] original = Enumerable.Range(0, LogStreamSenderOptions.DefaultChunkSize).Select(index => (byte)index).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(original));
        var message = new LogStreamNetworkMessage { Hash = hash };

        LogStreamChunkEncoding.SetEncodedData(message, original, true);

        Assert.Equal("gzip", message.DataEncoding);
        Assert.True(message.Data.Length <= original.Length - 64);
        Assert.Equal(hash, message.Hash);
        Assert.True(LogStreamChunkEncoding.TryDecode(message, out var restored));
        Assert.Equal(original, restored);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(restored)));
    }

    [Fact]
    public void Unnegotiated_receiver_gets_original_bytes_and_cleared_encoding()
    {
        byte[] original = new byte[4096];
        var message = new LogStreamNetworkMessage { DataEncoding = "gzip", Data = [1, 2, 3] };
        LogStreamChunkEncoding.SetEncodedData(message, original, false);
        Assert.Equal("", message.DataEncoding);
        Assert.Same(original, message.Data);
        Assert.True(LogStreamChunkEncoding.TryDecode(message, out var restored));
        Assert.Equal(original, restored);
    }

    [Fact]
    public void Small_or_incompressible_chunks_remain_raw()
    {
        var random = new Random(379);
        byte[] original = new byte[LogStreamSenderOptions.DefaultChunkSize];
        random.NextBytes(original);
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), new byte[511], original })
        {
            var message = new LogStreamNetworkMessage();
            LogStreamChunkEncoding.SetEncodedData(message, bytes, true);
            Assert.Equal("", message.DataEncoding);
            Assert.Same(bytes, message.Data);
            Assert.True(LogStreamChunkEncoding.TryDecode(message, out var restored));
            Assert.Equal(bytes, restored);
        }
    }

    [Theory]
    [InlineData("br")]
    [InlineData("GZIP")]
    [InlineData(null)]
    public void Unknown_encoding_is_rejected(string? encoding)
    {
        Assert.False(LogStreamChunkEncoding.TryDecode(new() { DataEncoding = encoding!, Data = [1] }, out var raw));
        Assert.Empty(raw);
    }

    [Fact]
    public void Oversized_and_null_payloads_are_rejected_before_processing()
    {
        var oversized = new byte[LogStreamSenderOptions.MaximumChunkSize + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => LogStreamChunkEncoding.SetEncodedData(new(), oversized, true));
        Assert.False(LogStreamChunkEncoding.TryDecode(new() { Data = oversized }, out _));
        Assert.False(LogStreamChunkEncoding.TryDecode(new() { Data = oversized, DataEncoding = "gzip" }, out _));
        Assert.False(LogStreamChunkEncoding.TryDecode(new() { Data = null! }, out _));
    }

    [Fact]
    public void Decompression_bomb_is_rejected_even_when_reported_size_is_forged()
    {
        byte[] bomb = Compress(new byte[1024 * 1024]);
        Assert.True(bomb.Length < LogStreamSenderOptions.DefaultChunkSize);
        Assert.False(LogStreamChunkEncoding.TryDecode(new() { DataEncoding = "gzip", Data = bomb }, out var output));
        Assert.Empty(output);
        BinaryPrimitives.WriteUInt32LittleEndian(bomb.AsSpan(bomb.Length - 4), 1);
        Assert.False(LogStreamChunkEncoding.TryDecode(new() { DataEncoding = "gzip", Data = bomb }, out output));
        Assert.Empty(output);
    }

    [Fact]
    public void Truncated_corrupt_and_trailing_payloads_are_rejected()
    {
        byte[] gzip = Compress(new byte[1024]);
        byte[] corrupt = gzip.ToArray();
        corrupt[^8] ^= 1;
        byte[] badHeader = gzip.ToArray();
        badHeader[0] = 0;
        byte[] missingBody = gzip[..10].Concat(new byte[8]).ToArray();
        foreach (byte[] malformed in new[] { gzip[..^4], gzip[..^8], corrupt, badHeader, missingBody, gzip.Concat(new byte[] { 1, 2, 3 }).ToArray(), new byte[18] })
            Assert.False(LogStreamChunkEncoding.TryDecode(new() { DataEncoding = "gzip", Data = malformed }, out _));
    }

    [Fact]
    public void Older_messages_default_to_raw_and_do_not_advertise_compression()
    {
        var message = LiveJson.Deserialize<LogStreamNetworkMessage>("{\"Data\":[1,2,3]}");
        Assert.NotNull(message);
        Assert.False(message.AcceptsCompressedChunks);
        Assert.Equal("", message.DataEncoding);
        Assert.True(LogStreamChunkEncoding.TryDecode(message, out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public void Legacy_raw_chunks_keep_the_existing_eight_kibibyte_receive_limit()
    {
        byte[] original = new byte[LogStreamSenderOptions.MaximumChunkSize];
        var message = LiveJson.Deserialize<LogStreamNetworkMessage>(LiveJson.Serialize(new LogStreamNetworkMessage { Data = original }));
        Assert.True(LogStreamChunkEncoding.TryDecode(message, out var bytes));
        Assert.Equal(original, bytes);
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(raw);
        return output.ToArray();
    }
}
