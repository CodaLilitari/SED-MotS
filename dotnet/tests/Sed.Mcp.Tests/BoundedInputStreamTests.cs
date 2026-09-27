using System.Text;

namespace Sed.Mcp.Tests;

public sealed class BoundedInputStreamTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public async Task ExactBoundaryAndMultipleFramesAreAcceptedAcrossReadBoundaries(int chunk)
    {
        const string text = "12345678\nabcdefgh\n\nlast";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
        using var bounded = new BoundedInputStream(source, maxLineBytes: 8);
        Assert.Equal(text, Encoding.UTF8.GetString(await ReadAll(bounded, chunk)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public async Task OverlongSecondFrameIsRefusedEvenWhenPreviousNewlineSharedARead(int chunk)
    {
        using var source = new MemoryStream("short\n123456789\n"u8.ToArray());
        using var bounded = new BoundedInputStream(source, maxLineBytes: 8);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(bounded, chunk));
    }

    [Fact]
    public async Task CountsUtf8BytesRatherThanCharacters()
    {
        using var acceptedSource = new MemoryStream(Encoding.UTF8.GetBytes("éééé\n"));
        using var accepted = new BoundedInputStream(acceptedSource, maxLineBytes: 8);
        Assert.Equal("éééé\n", Encoding.UTF8.GetString(await ReadAll(accepted, 3)));
        using var refusedSource = new MemoryStream(Encoding.UTF8.GetBytes("ééééé\n"));
        using var refused = new BoundedInputStream(refusedSource, maxLineBytes: 8);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(refused, 3));
    }

    [Fact]
    public async Task CrLfResetsOnlyAtLfAndCrCountsTowardByteBudget()
    {
        using var acceptedSource = new MemoryStream("1234567\r\nabcdefg\r\n"u8.ToArray());
        using var accepted = new BoundedInputStream(acceptedSource, maxLineBytes: 8);
        Assert.Equal("1234567\r\nabcdefg\r\n", Encoding.UTF8.GetString(await ReadAll(accepted, 1)));
        using var refusedSource = new MemoryStream("12345678\r\n"u8.ToArray());
        using var refused = new BoundedInputStream(refusedSource, maxLineBytes: 8);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(refused, 1));
    }

    [Fact]
    public async Task BareCrDoesNotResetTheFrame()
    {
        using var source = new MemoryStream("1234\r5678"u8.ToArray());
        using var bounded = new BoundedInputStream(source, maxLineBytes: 8);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(bounded, 4));
    }

    [Fact]
    public void SynchronousArrayReadCountsOnlyReturnedSliceAtNonzeroOffset()
    {
        using var source = new MemoryStream("12345678\nabc"u8.ToArray());
        using var bounded = new BoundedInputStream(source, maxLineBytes: 8);
        byte[] buffer = Enumerable.Repeat((byte)'x', 64).ToArray();
        Assert.Equal(6, bounded.Read(buffer, 17, 6));
        Assert.Equal("123456", Encoding.UTF8.GetString(buffer, 17, 6));
        Assert.Equal(6, bounded.Read(buffer, 17, 6));
        Assert.Equal("78\nabc", Encoding.UTF8.GetString(buffer, 17, 6));
        Assert.Equal((byte)'x', buffer[16]);
        Assert.Equal((byte)'x', buffer[23]);
        Assert.Equal(0, bounded.Read(buffer, 17, 6));
    }

    [Fact]
    public async Task LegacyAsyncArrayOverloadEnforcesLimitAcrossCalls()
    {
        using var source = new MemoryStream("123456789"u8.ToArray());
        using var bounded = new BoundedInputStream(source, maxLineBytes: 8);
        byte[] buffer = new byte[64];
        Assert.Equal(8, await bounded.ReadAsync(buffer, 13, 8, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.ReadAsync(buffer, 13, 1, CancellationToken.None));
    }

    [Fact]
    public void EmptyReadCannotResetAccumulatedFrameSize()
    {
        using var source = new MemoryStream("123456789"u8.ToArray());
        using var bounded = new BoundedInputStream(source, maxLineBytes: 8);
        byte[] buffer = new byte[8];
        Assert.Equal(8, bounded.Read(buffer, 0, 8));
        Assert.Equal(0, bounded.Read(buffer, 0, 0));
        Assert.Throws<InvalidDataException>(() => bounded.Read(buffer, 0, 1));
    }

    [Fact]
    public async Task DefaultTwoMiBBoundaryAllowsExactFrameAndRejectsNextByte()
    {
        byte[] content = Enumerable.Repeat((byte)'x', 2 * 1024 * 1024 + 1).ToArray();
        using var source = new MemoryStream(content);
        using var bounded = new BoundedInputStream(source);
        byte[] buffer = new byte[65536];
        for (int i = 0; i < 32; i++) Assert.Equal(buffer.Length, await bounded.ReadAsync(buffer.AsMemory()));
        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.ReadAsync(buffer.AsMemory()).AsTask());
    }

    [Fact]
    public async Task CancellationReachesUnderlyingReadWithoutConsumingInput()
    {
        using var source = new MemoryStream("{}\n"u8.ToArray());
        using var bounded = new BoundedInputStream(source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bounded.ReadAsync(new byte[8].AsMemory(), new CancellationToken(true)).AsTask());
        Assert.Equal(0, source.Position);
        Assert.Equal("{}\n", Encoding.UTF8.GetString(await ReadAll(bounded, 1)));
    }

    private static async Task<byte[]> ReadAll(Stream stream, int chunk)
    {
        using var destination = new MemoryStream();
        byte[] buffer = new byte[chunk];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory())) != 0) destination.Write(buffer, 0, count);
        return destination.ToArray();
    }
}
