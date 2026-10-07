using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// A launcher inside a restricted-token sandbox (codex's Windows sandbox: restricting SIDs =
/// capability SID, logon SID, Everyone) must still reach its own host's pipe. These tests
/// build such a token from the test's own token and connect while impersonating it.
/// </summary>
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public class RestrictedTokenTests
{
    [SkippableFact]
    public void FilterLogonSids_KeepsOnlyLogonSids()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows tokens");
        var sids = new[]
        {
            new SecurityIdentifier("S-1-1-0"),                                   // Everyone
            new SecurityIdentifier("S-1-5-21-804302062-685475890-3788045621-2208550388"), // capability
            new SecurityIdentifier("S-1-5-5-0-1318956606"),                      // logon
            new SecurityIdentifier("S-1-5-11"),                                  // Authenticated Users
        };
        var kept = RestrictedToken.FilterLogonSids(sids);
        Assert.Equal(["S-1-5-5-0-1318956606"], kept.Select(s => s.Value));
    }

    [SkippableFact]
    public void UnrestrictedTestProcess_IsNotRestricted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows tokens");
        Assert.False(RestrictedToken.IsCurrentProcessRestricted());
        Assert.Empty(RestrictedToken.CurrentRestrictingLogonSids());
    }

    [SkippableFact]
    public void OwnerOnlyPipe_DeniesRestrictedClient()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows tokens");
        var user = WindowsIdentity.GetCurrent().User!;
        var ex = Record.Exception(() => ConnectAsRestricted(NamedPipeTransport.BuildPipeSecurity(user, [])));
        Assert.IsType<UnauthorizedAccessException>(ex);
    }

    [SkippableFact]
    public void PipeGrantingLogonSid_AdmitsRestrictedClient()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows tokens");
        var user = WindowsIdentity.GetCurrent().User!;
        ConnectAsRestricted(NamedPipeTransport.BuildPipeSecurity(user, [Native.CurrentLogonSid()]));
    }

    [SkippableFact]
    public void RestrictedHost_CanCreateSecondPipeInstance()
    {
        // Regression: HostServer's accept loop creates the next instance while a connection
        // is live. A restricted host opens that instance against the pipe's DACL, so the
        // logon-SID ACE must grant CreateNewInstance — else every instance after the first
        // fails and concurrent clients from inside the sandbox are locked out.
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows tokens");
        var user = WindowsIdentity.GetCurrent().User!;
        var security = NamedPipeTransport.BuildPipeSecurity(user, [Native.CurrentLogonSid()]);
        var name = "psbash-rtok-" + Guid.NewGuid().ToString("N");

        using var restricted = Native.CreateCodexStyleRestrictedToken();
        WindowsIdentity.RunImpersonated(restricted, () =>
        {
            using var first = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 16,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            using var second = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 16,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        });
    }

    [SupportedOSPlatform("windows")]
    private static void ConnectAsRestricted(PipeSecurity security)
    {
        var name = "psbash-rtok-" + Guid.NewGuid().ToString("N");
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        var accept = server.WaitForConnectionAsync();

        using var restricted = Native.CreateCodexStyleRestrictedToken();
        WindowsIdentity.RunImpersonated(restricted, () =>
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            client.Connect(5000);
        });
        Assert.True(accept.Wait(5000), "server never saw the restricted client");
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        private const uint TokenQuery = 0x0008, TokenDuplicate = 0x0002, TokenImpersonate = 0x0004,
            TokenAssignPrimary = 0x0001;
        private const int TokenGroups = 2;
        private const uint SeGroupLogonId = 0xC0000000;

        /// <summary>The logon SID from the current process token's groups.</summary>
        public static SecurityIdentifier CurrentLogonSid()
        {
            using var token = OpenSelf(TokenQuery);
            GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out var len);
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                Assert.True(GetTokenInformation(token, TokenGroups, buf, len, out _));
                var count = Marshal.ReadInt32(buf);
                var entry = IntPtr.Size * 2;
                for (var i = 0; i < count; i++)
                {
                    var off = IntPtr.Size + i * entry;
                    var attrs = (uint)Marshal.ReadInt32(buf, off + IntPtr.Size);
                    if ((attrs & SeGroupLogonId) == SeGroupLogonId)
                        return new SecurityIdentifier(Marshal.ReadIntPtr(buf, off));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            throw new SkipException("current token has no logon SID");
        }

        /// <summary>A restricted copy of this token: restricting SIDs = logon SID, Everyone, a random SID.</summary>
        public static SafeAccessTokenHandle CreateCodexStyleRestrictedToken()
        {
            using var self = OpenSelf(TokenQuery | TokenDuplicate | TokenImpersonate | TokenAssignPrimary);
            var sids = new[]
            {
                CurrentLogonSid(),
                new SecurityIdentifier("S-1-1-0"),
                new SecurityIdentifier($"S-1-5-21-{Random.Shared.Next()}-{Random.Shared.Next()}-{Random.Shared.Next()}-{Random.Shared.Next()}"),
            };
            var pinned = sids.Select(s => { var b = new byte[s.BinaryLength]; s.GetBinaryForm(b, 0); return GCHandle.Alloc(b, GCHandleType.Pinned); }).ToArray();
            var entries = Marshal.AllocHGlobal(IntPtr.Size * 2 * sids.Length);
            try
            {
                for (var i = 0; i < sids.Length; i++)
                {
                    Marshal.WriteIntPtr(entries, i * IntPtr.Size * 2, pinned[i].AddrOfPinnedObject());
                    Marshal.WriteIntPtr(entries, i * IntPtr.Size * 2 + IntPtr.Size, IntPtr.Zero);
                }
                Assert.True(CreateRestrictedToken(self, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, (uint)sids.Length, entries, out var restricted),
                    $"CreateRestrictedToken failed: {Marshal.GetLastPInvokeError()}");
                return restricted;
            }
            finally
            {
                Marshal.FreeHGlobal(entries);
                foreach (var h in pinned) h.Free();
            }
        }

        private static SafeAccessTokenHandle OpenSelf(uint access)
        {
            Assert.True(OpenProcessToken(GetCurrentProcess(), access, out var token));
            return token;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int cls, IntPtr buf, int len, out int ret);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags,
            uint disableCount, IntPtr disable, uint deleteCount, IntPtr delete,
            uint restrictCount, IntPtr restrict, out SafeAccessTokenHandle newToken);
    }
}
