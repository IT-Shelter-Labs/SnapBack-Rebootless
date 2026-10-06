using SnapBack.Core.Model;

namespace SnapBack.Core.Tests;

public class PeSubsystemReaderTests
{
    private static byte[] Image(ushort magic, ushort subsystem, int peOffset = 0x80)
    {
        var bytes = new byte[peOffset + 4 + 20 + 96];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3C);
        bytes[peOffset] = (byte)'P';
        bytes[peOffset + 1] = (byte)'E';
        BitConverter.GetBytes(magic).CopyTo(bytes, peOffset + 24);
        BitConverter.GetBytes(subsystem).CopyTo(bytes, peOffset + 24 + 68);
        return bytes;
    }

    [Theory]
    [InlineData(0x20B, 2, ImageSubsystem.Gui)]
    [InlineData(0x20B, 3, ImageSubsystem.Console)]
    [InlineData(0x10B, 3, ImageSubsystem.Console)]
    [InlineData(0x10B, 10, ImageSubsystem.Other)]
    [InlineData(0x999, 2, ImageSubsystem.Unknown)]
    public void Reads_subsystem(ushort magic, ushort subsystem, ImageSubsystem expected) =>
        Assert.Equal(expected, PeSubsystemReader.Read(new MemoryStream(Image(magic, subsystem))));

    [Fact]
    public void Garbage_is_unknown()
    {
        Assert.Equal(ImageSubsystem.Unknown, PeSubsystemReader.Read(new MemoryStream([1, 2, 3])));
        var bad = Image(0x20B, 2);
        BitConverter.GetBytes(int.MaxValue).CopyTo(bad, 0x3C);
        Assert.Equal(ImageSubsystem.Unknown, PeSubsystemReader.Read(new MemoryStream(bad)));
    }
}
