using System.Runtime.InteropServices;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// <see cref="WindowsHostSpawn.BuildCommandLine"/> must round-trip through Windows'
/// own argv splitter. A mis-quoted endpoint (a temp path with a space, a trailing
/// backslash) would reach the host as the wrong <c>--ipc-endpoint</c> and every
/// spawn would time out.
///
/// Oracle note (Directive 1): no bash oracle — the oracle is
/// <c>CommandLineToArgvW</c>, the splitter the host's CRT/.NET startup mirrors.
/// </summary>
public class WindowsHostSpawnTests
{
    [SkippableTheory]
    [InlineData("--ipc-endpoint=pipe:psbash-abc")]
    [InlineData(@"--ipc-endpoint=unix:C:\Users\Jane Doe\AppData\Local\Temp\ps-bash\host.sock")]
    [InlineData(@"C:\dir with space\")]
    [InlineData(@"a\\b")]
    [InlineData("quote\"inside")]
    [InlineData("back\\\"slash-quote")]
    [InlineData("tab\there")]
    [InlineData("")]
    public void BuildCommandLine_RoundTripsThroughCommandLineToArgvW(string arg)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "CommandLineToArgvW is Windows-only");
        const string exe = @"C:\Program Files\ps-bash\ps-bash-host.exe";

        var line = WindowsHostSpawn.BuildCommandLine(exe, new[] { arg, "--launcher-pid=42" });

        Assert.Equal(new[] { exe, arg, "--launcher-pid=42" }, SplitWithWindows(line));
    }

    private static string[] SplitWithWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
