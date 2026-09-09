using System.Text;
using Sed.Formats.Gob;
using Xunit;

namespace Sed.Core.Tests;

public class GobBoundaryTests
{
    private static MemoryStream Archive(uint count = 1, uint offset = 152, uint length = 3)
    {
        var s = new MemoryStream();
        using var writer = new BinaryWriter(s, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("GOB ")); writer.Write(20u); writer.Write(12u); writer.Write(count);
        writer.Write(offset); writer.Write(length);
        var name = new byte[128]; Encoding.ASCII.GetBytes("jkl/test.jkl").CopyTo(name, 0); writer.Write(name);
        writer.Write(new byte[] { 1, 2, 3 }); s.Position = 0; return s;
    }

    [Fact]
    public void ValidArchiveReadsExactBytes()
    {
        using var stream = Archive(); using var gob = new GobArchive(stream);
        Assert.Equal(new byte[] { 1, 2, 3 }, gob.ReadBytes(Assert.Single(gob.Entries)));
    }

    [Fact]
    public void DirectoryCountCannotAllocateFromUntrustedHeader()
    {
        using var stream = Archive(uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => new GobArchive(stream));
    }

    [Theory]
    [InlineData(152u, 4u)]
    [InlineData(uint.MaxValue, 100u)]
    public void EntryRangeMustFitArchiveWithoutIntegerOverflow(uint offset, uint length)
    {
        using var stream = Archive(offset: offset, length: length);
        Assert.Throws<InvalidDataException>(() => new GobArchive(stream));
    }

    [Fact]
    public void TruncationAfterOpenCannotReturnZeroPaddedData()
    {
        using var stream = Archive(); using var gob = new GobArchive(stream);
        stream.SetLength(153);
        Assert.Throws<InvalidDataException>(() => gob.ReadBytes(gob.Entries[0]));
    }
}
