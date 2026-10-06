using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SnapBack.Core.Model;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>Queries on an open process handle. Every method returns null/false on failure instead of throwing.</summary>
internal static unsafe class ProcessQueries
{
    public static SafeProcessHandle? Open(int pid, uint access)
    {
        var h = Kernel32.OpenProcess(access, false, (uint)pid);
        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }

        return h;
    }

    public static long? CreationTime(SafeProcessHandle h) =>
        Kernel32.GetProcessTimes(h, out var creation, out _, out _, out _) ? creation : null;

    public static bool HasExited(SafeProcessHandle h) => Kernel32.WaitForSingleObject(h, 0) == Kernel32.WAIT_OBJECT_0;

    public static string? ImagePath(SafeProcessHandle h)
    {
        var size = 1024u;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new char[size];
            fixed (char* p = buffer)
            {
                var len = size;
                if (Kernel32.QueryFullProcessImageName(h, 0, p, ref len))
                {
                    return new string(p, 0, (int)len);
                }
            }

            if (Marshal.GetLastPInvokeError() != Kernel32.ERROR_INSUFFICIENT_BUFFER)
            {
                return null;
            }

            size *= 4;
        }

        return null;
    }

    public static bool? IsCritical(SafeProcessHandle h) =>
        Kernel32.IsProcessCritical(h, out var critical) ? critical : null;

    public static bool? IsProtected(SafeProcessHandle h)
    {
        uint level;
        return Kernel32.GetProcessInformation(h, Kernel32.ProcessProtectionLevelInfo, &level, sizeof(uint))
            ? level != Kernel32.PROTECTION_LEVEL_NONE
            : null;
    }

    public static string? PackageFullName(SafeProcessHandle h)
    {
        const int capacity = 256;
        var buffer = stackalloc char[capacity];
        var len = (uint)capacity;
        var rc = Kernel32.GetPackageFullName(h, ref len, buffer);
        return rc == 0 && len > 1 ? new string(buffer, 0, (int)len - 1) : null;
    }

    public static int? SessionOf(int pid) => Kernel32.ProcessIdToSessionId((uint)pid, out var s) ? (int)s : null;

    public readonly record struct TokenFacts(string? OwnerSid, bool? IsElevated, IntegrityLevel Integrity);

    public static TokenFacts Token(SafeProcessHandle h)
    {
        if (!Advapi32.OpenProcessToken(h, Advapi32.TOKEN_QUERY, out var token))
        {
            token.Dispose();
            return new TokenFacts(null, null, IntegrityLevel.Unknown);
        }

        using (token)
        {
            return new TokenFacts(TokenUser(token), TokenElevated(token), TokenIntegrity(token));
        }
    }

    private static string? TokenUser(SafeKernelHandle token)
    {
        var buffer = stackalloc byte[256];
        if (!Advapi32.GetTokenInformation(token, Advapi32.TokenUser, buffer, 256, out _))
        {
            return null;
        }

        // TOKEN_USER { SID_AND_ATTRIBUTES User { PSID Sid; DWORD Attributes; } }
        var sid = *(nint*)buffer;
        return sid == 0 ? null : new SecurityIdentifier(sid).Value;
    }

    private static bool? TokenElevated(SafeKernelHandle token)
    {
        uint elevated;
        return Advapi32.GetTokenInformation(token, Advapi32.TokenElevation, &elevated, sizeof(uint), out _) ? elevated != 0 : null;
    }

    private static IntegrityLevel TokenIntegrity(SafeKernelHandle token)
    {
        var buffer = stackalloc byte[128];
        if (!Advapi32.GetTokenInformation(token, Advapi32.TokenIntegrityLevel, buffer, 128, out _))
        {
            return IntegrityLevel.Unknown;
        }

        // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; } - the RID of the label SID is the level.
        var sid = *(nint*)buffer;
        if (sid == 0)
        {
            return IntegrityLevel.Unknown;
        }

        var value = new SecurityIdentifier(sid).Value;
        var lastDash = value.LastIndexOf('-');
        if (lastDash < 0 || !uint.TryParse(value[(lastDash + 1)..], out var rid))
        {
            return IntegrityLevel.Unknown;
        }

        return rid switch
        {
            < 0x1000 => IntegrityLevel.Untrusted,
            < 0x2000 => IntegrityLevel.Low,
            < 0x2100 => IntegrityLevel.Medium,
            < 0x3000 => IntegrityLevel.MediumPlus,
            < 0x4000 => IntegrityLevel.High,
            < 0x5000 => IntegrityLevel.System,
            _ => IntegrityLevel.Protected,
        };
    }

    /// <summary>The process command line (Windows 8.1+). Used only to derive a short display hint.</summary>
    public static string? CommandLine(SafeProcessHandle h)
    {
        var size = 4096u;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = NativeMemory.Alloc(size);
            try
            {
                var status = Ntdll.NtQueryInformationProcess(h, Ntdll.ProcessCommandLineInformation, buffer, size, out var needed);
                if (status is Ntdll.STATUS_INFO_LENGTH_MISMATCH or Ntdll.STATUS_BUFFER_TOO_SMALL or Ntdll.STATUS_BUFFER_OVERFLOW)
                {
                    size = Math.Max(needed, size * 2);
                    if (size > 1 << 20)
                    {
                        return null;
                    }

                    continue;
                }

                if (status < 0)
                {
                    return null;
                }

                var us = (UNICODE_STRING*)buffer;
                var start = (byte*)buffer;
                var data = (byte*)us->Buffer;
                if (us->Buffer == 0 || data < start || data + us->Length > start + size)
                {
                    return null;
                }

                return new string((char*)data, 0, us->Length / 2);
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    }

    /// <summary>Executable file name of a PID, or null when unavailable.</summary>
    public static string? ImageNameOf(int pid)
    {
        using var h = Open(pid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION);
        var path = h is null ? null : ImagePath(h);
        return path is null ? null : Path.GetFileName(path);
    }
}
