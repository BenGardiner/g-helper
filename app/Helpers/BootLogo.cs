using System.Diagnostics;

namespace GHelper.Helpers
{
    // Fork feature, from the BIOS 314 boot-animation analysis (AMITSE in the
    // GZ302 firmware contains the hard-coded path EFI\Logo\Logo.bmp and loads
    // a BMP logo from the EFI System Partition when one is present there).
    // This helper installs/removes that override: mount the ESP with
    // mountvol, copy the file in as \EFI\Logo\Logo.bmp, unmount. The caller
    // is responsible for running Install()/Remove() elevated (see the
    // "boot-logo" / "boot-logo-remove" startup actions in Program.cs).
    // Removing the file fully restores the stock firmware logo.
    public static class BootLogo
    {
        public const string StagingFileName = "boot-logo-staged.bmp";

        // Resolutions of the logo assets shipped inside BIOS 314 itself.
        // AMITSE validates the image on load, so other sizes are a gamble;
        // the install flow warns (but does not block) on anything else.
        public static readonly (int Width, int Height)[] NativeSizes =
        {
            (600, 200), (800, 266), (900, 300), (1200, 400)
        };

        public static string GetStagingPath()
        {
            string appPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHelper");
            return Path.Combine(appPath, StagingFileName);
        }

        // Minimal BMP validation: "BM" magic, a BITMAPINFOHEADER-or-later DIB
        // header (>= 40 bytes) and sane dimensions read from the header.
        public static bool IsValidBmp(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] header = new byte[26];
                if (stream.Read(header, 0, header.Length) < header.Length) return false;
                if (header[0] != (byte)'B' || header[1] != (byte)'M') return false;
                if (BitConverter.ToInt32(header, 14) < 40) return false;
                width = BitConverter.ToInt32(header, 18);
                height = Math.Abs(BitConverter.ToInt32(header, 22));
                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Boot logo: not a readable BMP (" + ex.Message + ")");
                return false;
            }
        }

        public static bool IsNativeSize(int width, int height)
        {
            foreach (var (w, h) in NativeSizes)
                if (w == width && h == height) return true;
            return false;
        }

        // Copies the user-picked BMP to a per-user staging file that the
        // elevated instance can read (the picker runs unelevated).
        public static bool Stage(string sourcePath)
        {
            try
            {
                string staging = GetStagingPath();
                Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                File.Copy(sourcePath, staging, true);
                Logger.WriteLine("Boot logo: staged " + sourcePath + " -> " + staging);
                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Boot logo: staging failed (" + ex.Message + ")");
                return false;
            }
        }

        private static string? FindFreeDriveLetter()
        {
            var used = new HashSet<char>();
            foreach (var drive in DriveInfo.GetDrives())
                if (drive.Name.Length > 0) used.Add(char.ToUpperInvariant(drive.Name[0]));

            for (char c = 'S'; c <= 'Z'; c++)
                if (!used.Contains(c)) return c + ":";
            for (char c = 'D'; c < 'S'; c++)
                if (!used.Contains(c)) return c + ":";
            return null;
        }

        private static bool RunMountvol(string arguments)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "mountvol",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process == null) return false;
                if (!process.WaitForExit(15000))
                {
                    Logger.WriteLine("Boot logo: mountvol " + arguments + " timed out");
                    return false;
                }
                Logger.WriteLine("Boot logo: mountvol " + arguments + " -> exit " + process.ExitCode);
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Boot logo: mountvol " + arguments + " failed (" + ex.Message + ")");
                return false;
            }
        }

        private static string? MountEsp()
        {
            string? letter = FindFreeDriveLetter();
            if (letter == null)
            {
                Logger.WriteLine("Boot logo: no free drive letter for the ESP mount");
                return null;
            }
            if (!RunMountvol(letter + " /S") || !Directory.Exists(letter + "\\"))
            {
                RunMountvol(letter + " /D");
                return null;
            }
            return letter;
        }

        // Installs the staged BMP as \EFI\Logo\Logo.bmp on the ESP and only
        // reports success if the destination exists with the staged size.
        public static (bool Ok, string Message) Install()
        {
            string staging = GetStagingPath();
            if (!File.Exists(staging))
                return (false, "Staged boot logo not found (" + staging + "). Pick the BMP again.");

            string? letter = MountEsp();
            if (letter == null)
                return (false, "Could not mount the EFI System Partition.");

            try
            {
                string dir = letter + @"\EFI\Logo";
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, "Logo.bmp");
                File.Copy(staging, dest, true);

                long expected = new FileInfo(staging).Length;
                var written = new FileInfo(dest);
                if (!written.Exists || written.Length != expected)
                    return (false, "Copy verification failed: " + dest + " is missing or has the wrong size.");

                Logger.WriteLine("Boot logo: installed " + dest + " (" + expected + " bytes)");
                return (true, "Boot logo installed to " + dest + ".\n\nReboot to see it at POST. If the firmware ignores it, removing the override restores the stock logo.");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Boot logo: install failed (" + ex.Message + ")");
                return (false, "Install failed: " + ex.Message);
            }
            finally
            {
                RunMountvol(letter + " /D");
            }
        }

        public static (bool Ok, string Message) Remove()
        {
            string? letter = MountEsp();
            if (letter == null)
                return (false, "Could not mount the EFI System Partition.");

            try
            {
                string dest = letter + @"\EFI\Logo\Logo.bmp";
                if (!File.Exists(dest))
                    return (true, "No boot logo override is installed (nothing to remove).");

                File.Delete(dest);
                if (File.Exists(dest))
                    return (false, "Could not delete " + dest + ".");

                Logger.WriteLine("Boot logo: removed " + dest);
                return (true, "Boot logo override removed. The stock firmware logo will be used at POST.");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Boot logo: remove failed (" + ex.Message + ")");
                return (false, "Remove failed: " + ex.Message);
            }
            finally
            {
                RunMountvol(letter + " /D");
            }
        }
    }
}
