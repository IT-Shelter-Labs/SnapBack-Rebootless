using System.ComponentModel;
using System.Runtime.InteropServices;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>A process as reported by the kernel, before any handle-based enrichment.</summary>
internal sealed record RawProcess(
    int Pid,
    int ParentPid,
    long CreateTime,
    string ImageName,
    int SessionId,
    long PrivateWorkingSet,
    long WorkingSet,
    long PrivateBytes);

/// <summary>
/// Enumerates all processes with one NtQuerySystemInformation call. This needs no process handles,
/// so it also covers processes SnapBack cannot open, and it yields an internally consistent snapshot
/// including creation times (the second half of every process identity).
/// </summary>
internal static unsafe class SystemProcessInformationReader
{
    public static List<RawProcess> Query()
    {
        var size = 1 << 20;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = NativeMemory.Alloc((nuint)size);
            try
            {
                var status = Ntdll.NtQuerySystemInformation(Ntdll.SystemProcessInformation, buffer, (uint)size, out var needed);
                if (status == Ntdll.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, (int)needed + (64 * 1024));
                    continue;
                }

                if (status < 0)
                {
                    throw new Win32Exception(Ntdll.RtlNtStatusToDosError(status));
                }

                return Parse((byte*)buffer, size);
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        throw new InvalidOperationException("The process list kept growing faster than it could be read.");
    }

    /// <summary>Parses a SystemProcessInformation buffer, validating every offset and pointer.</summary>
    internal static List<RawProcess> Parse(byte* buffer, int length)
    {
        var result = new List<RawProcess>(capacity: 512);
        var offset = 0L;
        var entrySize = sizeof(SYSTEM_PROCESS_INFORMATION);

        while (offset >= 0 && offset + entrySize <= length)
        {
            var spi = (SYSTEM_PROCESS_INFORMATION*)(buffer + offset);
            var pid = (int)spi->UniqueProcessId;

            result.Add(new RawProcess(
                Pid: pid,
                ParentPid: (int)spi->InheritedFromUniqueProcessId,
                CreateTime: spi->CreateTime,
                ImageName: ReadName(spi, buffer, length, pid),
                SessionId: (int)spi->SessionId,
                PrivateWorkingSet: spi->WorkingSetPrivateSize,
                WorkingSet: (long)spi->WorkingSetSize,
                PrivateBytes: (long)spi->PrivatePageCount));

            if (spi->NextEntryOffset == 0)
            {
                break;
            }

            offset += spi->NextEntryOffset;
        }

        return result;
    }

    private static string ReadName(SYSTEM_PROCESS_INFORMATION* spi, byte* buffer, int length, int pid)
    {
        var name = spi->ImageName;
        var data = (byte*)name.Buffer;
        if (name.Buffer != 0 && name.Length > 0 && data >= buffer && data + name.Length <= buffer + length)
        {
            return new string((char*)data, 0, name.Length / 2);
        }

        return pid == 0 ? "Idle" : "Unknown";
    }
}
