using System.Runtime.InteropServices;
using System.Text;

namespace AiStatus.Tests.Ui;

public sealed class AppManifestTests
{
    private const uint LoadLibraryAsDataFile = 0x00000002;

    [Fact]
    public void BuiltExecutable_DeclaresPerMonitorV2DpiAwareness()
    {
        // Break caught: placement assumes target-monitor DPI while Windows virtualizes the app at system DPI.
        string executable = Path.Combine(AppContext.BaseDirectory, "AiStatus.exe");
        Assert.True(File.Exists(executable), $"Application executable not found: {executable}");
        IntPtr module = NativeMethods.LoadLibraryEx(executable, IntPtr.Zero, LoadLibraryAsDataFile);
        Assert.NotEqual(IntPtr.Zero, module);
        try
        {
            IntPtr resource = NativeMethods.FindResource(module, new IntPtr(1), new IntPtr(24));
            Assert.NotEqual(IntPtr.Zero, resource);
            uint size = NativeMethods.SizeofResource(module, resource);
            IntPtr loaded = NativeMethods.LoadResource(module, resource);
            IntPtr bytes = NativeMethods.LockResource(loaded);
            Assert.NotEqual(0u, size);
            Assert.NotEqual(IntPtr.Zero, bytes);

            var buffer = new byte[size];
            Marshal.Copy(bytes, buffer, 0, checked((int)size));
            string manifest = Encoding.UTF8.GetString(buffer).TrimEnd('\0');

            Assert.Contains("<dpiAwareness", manifest, StringComparison.Ordinal);
            Assert.Contains("PerMonitorV2", manifest, StringComparison.Ordinal);
        }
        finally
        {
            NativeMethods.FreeLibrary(module);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
        internal static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LockResource(IntPtr resource);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr module);
    }
}
