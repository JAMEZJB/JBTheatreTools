using System.Runtime.InteropServices;
using System.Text;

namespace JBTheatreTools;

/// <summary>
/// Stores the GitHub PAT in the Windows Credential Manager (generic credential).
/// The token is never written to disk in plaintext and never logged.
/// </summary>
public static class TokenStore
{
    private const string Target = "JBTheatreTools/github-pat";

    public static string? Load()
    {
        if (!CredReadW(Target, CRED_TYPE.GENERIC, 0, out var handle)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            var token = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrEmpty(token) ? null : token;
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static bool Save(string token)
    {
        var blob = Encoding.UTF8.GetBytes(token);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE.GENERIC,
                TargetName = Target,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST.LocalMachine,
                UserName = "github-pat",
            };
            return CredWriteW(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static void Clear() => CredDeleteW(Target, CRED_TYPE.GENERIC, 0);

    private enum CRED_TYPE : uint { GENERIC = 1 }
    private enum CRED_PERSIST : uint { Session = 1, LocalMachine = 2, Enterprise = 3 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public CRED_TYPE Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CRED_PERSIST Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, CRED_TYPE type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr cred);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(string target, CRED_TYPE type, int flags);
}
