using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using LabelPrinter.Agent.Core.Runtime;
using Microsoft.Win32;

namespace LabelPrinter.Agent.Windows;

/// <summary>
/// DPAPI with the CurrentUser scope: the credential can only be decrypted by the Windows account that runs the
/// agent, on this machine. No key is stored by the agent. Not protection against code running as that user.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiProtector : ISecretProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private static readonly byte[] Entropy = "LabelPrinterAgent/credential/v1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    public byte[] Protect(ReadOnlySpan<byte> plain) => Transform(plain, protect: true);
    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => Transform(protectedBytes, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input.ToArray(), GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = default(Blob);
        try
        {
            var dataIn = new Blob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new Blob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), protect ? "DPAPI protect failed" : "DPAPI unprotect failed (different user or machine?)");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            Array.Clear((byte[])inHandle.Target!);
            inHandle.Free();
            entropyHandle.Free();
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class Acl
{
    /// <summary>Removes inherited access and leaves the file to the current user, SYSTEM and Administrators.</summary>
    public static void RestrictToCurrentUser(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public static void RestrictDirectoryToCurrentUser(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}

/// <summary>Opt-in start at logon for the current user (HKCU Run). Nothing is registered by installation.</summary>
[SupportedOSPlatform("windows")]
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "LabelPrinterAgent";

    public static string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public static void Enable(string backgroundExe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, "\"" + backgroundExe + "\"", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
