using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GHelper.Helpers
{
    // Fork feature, from the BIOS 314 boot-animation analysis. The firmware's
    // "Animation Post Logo" enable setting is byte 0 of the UEFI setup
    // variable "AnimeLogoSetup":
    //   GUID  DCE76117-3F1A-40F2-861A-D9DC8DA70B50 (IFR VarStore, Id 0x1F)
    //   Size  3 bytes
    //   [0]   animation enable: 1 = enabled, 0 = disabled  <- written here
    //   [1]   animation audio enable (BIOS setup page; left untouched)
    //   [2]   animation audio volume, 0-127 (BIOS setup page; left untouched)
    //
    // Evidence (hidden_files/ifr in the boot-animation workspace): IFR
    // extraction of the BIOS 314 Setup module declares the varstore above;
    // the only IFR binding for offset 0 is a prompt-less storage question
    // (Id 0x1AE) used by the SuppressIf that hides the audio question when
    // this byte is 0 - i.e. byte 0 is the feature's master enable, and ASUS
    // ships no setup question for it (the strings "Animation Post Logo" /
    // "Enable/Disable Animation Post Logo." remain in the string package,
    // unreferenced). The factory-default NVAR store in the same image holds
    // AnimeLogoSetup = 01 01 4B, whose bytes 1/2 match the IFR defaults for
    // audio (Enabled) and volume (75) exactly, and byte 0 = 1 matches the
    // animation playing by default. Retail BIOS setup (verified on the
    // machine) exposes ONLY the audio question on the "Animation Post Logo
    // Configuration" page, so this variable write is the only route to the
    // animation toggle.
    //
    // Access discipline: read-modify-write of the whole variable. A
    // successful read (with the variable's live attributes) always precedes
    // a write; a size other than 3 aborts; bytes 1-2 are preserved verbatim;
    // the write reuses the attributes returned by the read; the result is
    // verified by reading back. The caller is responsible for running the
    // write elevated (firmware variable access needs
    // SeSystemEnvironmentPrivilege, enabled on this process below).
    //
    // One narrow exception, proven necessary on the target machine
    // (on-machine probe, 2026-10-22): an elevated read of this variable
    // fails with error 203 (ERROR_ENVVAR_NOT_FOUND) while a control read
    // of BootOrder succeeds with attributes 0x7 - the variable exists in
    // the firmware (the IFR varstore and the factory-default store above)
    // but has no runtime-visible instance until something creates one.
    // Only for that exact error, SetAnimationEnabled falls back to
    // creating the variable with the proven 3-byte layout: byte 0 = the
    // requested state, bytes 1-2 = the factory defaults 01 4B, attributes
    // 0x7 (NV|BS|RT, as the machine's runtime-visible variables carry),
    // followed by the same read-back verification. Every other read error
    // still aborts without writing.
    public static class BootAnimation
    {
        public const string VariableName = "AnimeLogoSetup";
        public const string VariableGuid = "{dce76117-3f1a-40f2-861a-d9dc8da70b50}";
        public const int VariableSize = 3;
        public const int AnimationOffset = 0;

        // EFI_VARIABLE_NON_VOLATILE | EFI_VARIABLE_BOOTSERVICE_ACCESS, the
        // attributes plain HII varstores are created with. Only used if a
        // read somehow reports no attributes; normally the read's live
        // attributes are passed back to the write unchanged.
        private const uint FallbackAttributes = 0x3;

        // ERROR_ENVVAR_NOT_FOUND: the read failed because the variable has
        // no runtime-visible instance - the only read failure that unlocks
        // the create path in SetAnimationEnabled.
        private const int ErrorEnvvarNotFound = 203;

        // EFI_VARIABLE_NON_VOLATILE | EFI_VARIABLE_BOOTSERVICE_ACCESS |
        // EFI_VARIABLE_RUNTIME_ACCESS. Used only by the create-on-203 path;
        // matches the attributes the target machine's runtime-visible
        // variables carry (a control read of BootOrder reports 0x7), so
        // the created instance stays readable/verifiable from Windows.
        private const uint CreateAttributes = 0x7;

        // Factory defaults for bytes 1-2 (audio enable = 1, audio volume
        // = 75), from the factory-default store's AnimeLogoSetup = 01 01 4B.
        // Used only by the create-on-203 path; the read-modify-write path
        // never invents bytes, it preserves whatever is already there.
        private const byte FactoryDefaultByte1 = 0x01;
        private const byte FactoryDefaultByte2 = 0x4B;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFirmwareEnvironmentVariableExW(string lpName, string lpGuid, byte[] pBuffer, uint nSize, out uint pdwAttributes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetFirmwareEnvironmentVariableExW(string lpName, string lpGuid, byte[] pValue, uint nSize, uint dwAttributes);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TokenPrivileges NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public long Luid;
            public uint Attributes;
        }

        private static bool EnableSystemEnvironmentPrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out IntPtr token)) // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
            {
                Logger.WriteLine("Boot animation: OpenProcessToken failed (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
                return false;
            }
            try
            {
                if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out long luid))
                {
                    Logger.WriteLine("Boot animation: LookupPrivilegeValue failed (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
                    return false;
                }
                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    Logger.WriteLine("Boot animation: AdjustTokenPrivileges failed (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
                    return false;
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 0) // ERROR_NOT_ALL_ASSIGNED etc.: privilege not held
                {
                    Logger.WriteLine("Boot animation: privilege not granted (" + new Win32Exception(error).Message + ")");
                    return false;
                }
                return true;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        public static bool TryRead(out byte[] value, out uint attributes)
        {
            return TryRead(out value, out attributes, out _);
        }

        // As above, but also reports the Win32 error behind a failed read
        // (0 when the read itself did not fail), so callers can tell
        // ERROR_ENVVAR_NOT_FOUND (203) apart from any other failure.
        public static bool TryRead(out byte[] value, out uint attributes, out int error)
        {
            value = Array.Empty<byte>();
            attributes = 0;
            error = 0;
            if (!EnableSystemEnvironmentPrivilege()) return false;

            byte[] buffer = new byte[256];
            uint size = GetFirmwareEnvironmentVariableExW(VariableName, VariableGuid, buffer, (uint)buffer.Length, out attributes);
            if (size == 0)
            {
                error = Marshal.GetLastWin32Error();
                Logger.WriteLine("Boot animation: reading " + VariableName + " failed (" + new Win32Exception(error).Message + ")");
                return false;
            }
            value = new byte[size];
            Array.Copy(buffer, value, (int)size);
            return true;
        }

        public static bool TryReadAnimationEnabled(out bool enabled)
        {
            enabled = false;
            if (!TryRead(out byte[] value, out _)) return false;
            if (value.Length != VariableSize)
            {
                Logger.WriteLine("Boot animation: " + VariableName + " has unexpected size " + value.Length + " (expected " + VariableSize + ")");
                return false;
            }
            enabled = value[AnimationOffset] != 0;
            return true;
        }

        // Sets only byte 0 of AnimeLogoSetup, preserving bytes 1-2 and the
        // variable's attributes. Never writes unless a full read succeeded
        // first; verifies by reading back afterwards. Sole exception: a
        // read failing with ERROR_ENVVAR_NOT_FOUND (203) takes the
        // create path below, because on the target machine the variable
        // demonstrably has no runtime-visible instance to read.
        public static (bool Ok, string Message) SetAnimationEnabled(bool enabled)
        {
            if (!TryRead(out byte[] current, out uint attributes, out int readError))
            {
                if (readError == ErrorEnvvarNotFound)
                    return CreateVariable(enabled);
                return (false, "Could not read the " + VariableName + " firmware variable (administrator rights are required). No change was made.");
            }

            if (current.Length != VariableSize)
                return (false, VariableName + " has an unexpected size (" + current.Length + " bytes, expected " + VariableSize + "). No change was made.");

            byte[] next = (byte[])current.Clone();
            next[AnimationOffset] = enabled ? (byte)1 : (byte)0;
            if (next.SequenceEqual(current))
                return (true, "The POST logo animation is already " + (enabled ? "enabled" : "disabled") + ".");

            uint writeAttributes = attributes != 0 ? attributes : FallbackAttributes;
            if (!SetFirmwareEnvironmentVariableExW(VariableName, VariableGuid, next, (uint)next.Length, writeAttributes))
                return (false, "Writing " + VariableName + " failed (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + "). No change was made.");

            if (!TryRead(out byte[] verify, out _) || !verify.SequenceEqual(next))
                return (false, "The write could not be verified by reading " + VariableName + " back. Reboot and check the POST behaviour before toggling again.");

            Logger.WriteLine("Boot animation: updated existing variable " + VariableName + "[0] " + current[AnimationOffset] + " -> " + next[AnimationOffset] + " (bytes 1-2 preserved: " + next[1] + ", " + next[2] + "); read-back verified");
            return (true, "POST logo animation " + (enabled ? "enabled" : "disabled") + " (updated existing variable; write verified by read-back). This takes effect on the next reboot.");
        }

        // Create path, taken only when the read failed with
        // ERROR_ENVVAR_NOT_FOUND (203): the variable has no runtime-visible
        // instance, so there is nothing to read-modify-write. Creates it
        // with the proven 3-byte layout - byte 0 = the requested state,
        // bytes 1-2 = the factory defaults - and runtime-visible
        // attributes, then verifies by reading back: the variable must now
        // read successfully with exactly the bytes written.
        private static (bool Ok, string Message) CreateVariable(bool enabled)
        {
            byte[] next = { enabled ? (byte)1 : (byte)0, FactoryDefaultByte1, FactoryDefaultByte2 };
            if (!SetFirmwareEnvironmentVariableExW(VariableName, VariableGuid, next, (uint)next.Length, CreateAttributes))
                return (false, "The " + VariableName + " firmware variable was not present, and creating it failed (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + "). No change was made.");

            if (!TryRead(out byte[] verify, out _) || !verify.SequenceEqual(next))
                return (false, "The " + VariableName + " variable was created but could not be verified by reading it back. Reboot and check the POST behaviour before toggling again.");

            Logger.WriteLine("Boot animation: created variable " + VariableName + " (was not present, read error 203) = " + next[0] + ", " + next[1] + ", " + next[2] + " with attributes 0x" + CreateAttributes.ToString("X") + "; read-back verified");
            return (true, "POST logo animation " + (enabled ? "enabled" : "disabled") + " (created variable: it was not present; write verified by read-back). This takes effect on the next reboot.");
        }
    }
}
