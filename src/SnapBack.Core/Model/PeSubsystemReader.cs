using System.Buffers.Binary;

namespace SnapBack.Core.Model;

/// <summary>Reads the subsystem (GUI / console) from a PE executable header.</summary>
public static class PeSubsystemReader
{
    private const int MaxHeaderOffset = 1 << 20;

    public static ImageSubsystem Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            Span<byte> dos = stackalloc byte[64];
            if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != 'M' || dos[1] != 'Z')
            {
                return ImageSubsystem.Unknown;
            }

            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
            if (peOffset < 64 || peOffset > MaxHeaderOffset || !stream.CanSeek)
            {
                return ImageSubsystem.Unknown;
            }

            stream.Seek(peOffset, SeekOrigin.Begin);

            // "PE\0\0" + IMAGE_FILE_HEADER (20 bytes) + optional header up to and including Subsystem (offset 68, 2 bytes).
            Span<byte> header = stackalloc byte[4 + 20 + 70];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
                || header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0)
            {
                return ImageSubsystem.Unknown;
            }

            var optional = header[24..];
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
            if (magic is not (0x10B or 0x20B))
            {
                return ImageSubsystem.Unknown;
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(optional[68..]) switch
            {
                2 => ImageSubsystem.Gui,
                3 => ImageSubsystem.Console,
                _ => ImageSubsystem.Other,
            };
        }
        catch (IOException)
        {
            return ImageSubsystem.Unknown;
        }
    }
}
