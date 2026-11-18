namespace PsBash.Core.Platform;

/// <summary>
/// The longest AF_UNIX socket path .NET accepts here. <c>sun_path</c> is a 108-byte buffer including the terminating
/// NUL on Linux and Windows (107 chars) and a 104-byte one on macOS and FreeBSD (103 chars). A long macOS
/// <c>$TMPDIR</c> (<c>/var/folders/…/T/</c>, ~50 chars) leaves little room, which is why executor slot names are
/// hashed when they would not fit (<c>IpcTransportFactory.ResolveSlotEndpoint</c>). Spec:
/// <c>docs/specs/platform-dependencies.md</c>.
/// </summary>
public static class UnixSocketPath
{
    /// <summary>Linux/Windows: <c>sizeof(sun_path)</c> 108 minus the NUL.</summary>
    public const int LinuxMaxChars = 107;

    /// <summary>macOS/FreeBSD: <c>sizeof(sun_path)</c> 104 minus the NUL.</summary>
    public const int BsdMaxChars = 103;

    /// <summary>The budget on this OS.</summary>
    public static int MaxChars { get; } =
        OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsFreeBSD() ? BsdMaxChars : LinuxMaxChars;
}
