using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HyperVControl.Services;

internal sealed record GuestCredential(string UserName, string Password);

internal sealed class CredentialStore
{
    private static string Target(Guid vmId, string profile) => $"HyperVControl/{vmId:D}/{ValidateProfile(profile)}";

    internal static string ValidateProfile(string profile) => Regex.IsMatch(profile, "^[a-zA-Z][a-zA-Z0-9_]{0,31}$")
        ? profile : throw new ArgumentException("credentialProfile must contain 1-32 letters, digits or underscores and start with a letter.");

    public GuestCredential Resolve(Guid vmId, string profile = "default")
    {
        if (CredRead(Target(vmId, profile), 1, 0, out var ptr))
        {
            try
            {
                var c = Marshal.PtrToStructure<Credential>(ptr);
                var bytes = new byte[c.BlobSize];
                Marshal.Copy(c.Blob, bytes, 0, bytes.Length);
                try { return new(c.UserName ?? "", Encoding.Unicode.GetString(bytes)); }
                finally { Array.Clear(bytes); }
            }
            finally { CredFree(ptr); }
        }
        var prefix = profile == "default" ? "HYPERV_CONTROL_GUEST" : "HYPERV_CONTROL_" + profile.ToUpperInvariant();
        var user = Environment.GetEnvironmentVariable(prefix + "_USERNAME");
        var pass = Environment.GetEnvironmentVariable(prefix + "_PASSWORD");
        if (profile == "default")
        {
            user ??= Environment.GetEnvironmentVariable("HYPERV_GUEST_USERNAME");
            pass ??= Environment.GetEnvironmentVariable("HYPERV_GUEST_PASSWORD");
        }
        if (!string.IsNullOrEmpty(user) && pass != null) return new(user, pass);
        throw new InvalidOperationException($"No guest credential for VM {vmId:D}, profile '{profile}'. Use hyperv_credential (Windows Credential Manager) or {prefix}_USERNAME / {prefix}_PASSWORD environment variables.");
    }

    public void Save(Guid id, string profile, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("A guest username is required.");
        var bytes = Encoding.Unicode.GetBytes(password);
        if (bytes.Length > 2560) throw new ArgumentException("Credential exceeds Windows Credential Manager's size limit.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new Credential { Type = 1, TargetName = Target(id, profile), UserName = user,
                Blob = blob, BlobSize = (uint)bytes.Length, Persist = 2 };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Array.Clear(bytes); Marshal.Copy(bytes, 0, blob, bytes.Length); Marshal.FreeHGlobal(blob); }
    }

    public void Remove(Guid id, string profile)
    {
        if (!CredDelete(Target(id, profile), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32")] private static extern void CredFree(nint buffer);
}
