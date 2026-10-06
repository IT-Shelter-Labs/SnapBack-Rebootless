using System.Runtime.InteropServices;
using SnapBack.Windows.Native;

namespace SnapBack.Windows.Tests;

public unsafe class SystemProcessInformationParserTests
{
    [Fact]
    public void Parses_entries_and_validates_name_pointers()
    {
        var entry = sizeof(SYSTEM_PROCESS_INFORMATION);
        const int nameBytes = 64;
        var length = (entry + nameBytes) * 3;
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)length);
        try
        {
            var first = (SYSTEM_PROCESS_INFORMATION*)buffer;
            first->NextEntryOffset = (uint)(entry + nameBytes);
            first->UniqueProcessId = 0; // Idle: no name

            var second = (SYSTEM_PROCESS_INFORMATION*)(buffer + entry + nameBytes);
            var name = "chrome.exe";
            var nameStart = (char*)((byte*)second + entry);
            name.AsSpan().CopyTo(new Span<char>(nameStart, name.Length));
            second->NextEntryOffset = (uint)(entry + nameBytes);
            second->UniqueProcessId = 1234;
            second->InheritedFromUniqueProcessId = 1100;
            second->CreateTime = 133_000_000_000_000_000;
            second->SessionId = 1;
            second->WorkingSetPrivateSize = 50 * 1024 * 1024;
            second->ImageName = new UNICODE_STRING { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = (nint)nameStart };

            var third = (SYSTEM_PROCESS_INFORMATION*)(buffer + (2 * (entry + nameBytes)));
            third->NextEntryOffset = 0;
            third->UniqueProcessId = 4321;
            // A name pointer outside the buffer must not be dereferenced.
            third->ImageName = new UNICODE_STRING { Length = 20, MaximumLength = 20, Buffer = 0x1000 };

            var result = SystemProcessInformationReader.Parse(buffer, length);

            Assert.Equal(3, result.Count);
            Assert.Equal("Idle", result[0].ImageName);
            Assert.Equal(new RawProcess(1234, 1100, 133_000_000_000_000_000, "chrome.exe", 1, 50 * 1024 * 1024, 0, 0), result[1]);
            Assert.Equal("Unknown", result[2].ImageName);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    [Fact]
    public void Truncated_buffer_does_not_read_past_the_end()
    {
        var entry = sizeof(SYSTEM_PROCESS_INFORMATION);
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)entry);
        try
        {
            var first = (SYSTEM_PROCESS_INFORMATION*)buffer;
            first->NextEntryOffset = (uint)entry; // claims a second entry that does not fit
            first->UniqueProcessId = 8;
            Assert.Single(SystemProcessInformationReader.Parse(buffer, entry));
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }
}
