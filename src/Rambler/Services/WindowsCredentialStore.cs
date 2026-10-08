using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Rambler.Core.Settings;
using static Rambler.Interop.NativeMethods;

namespace Rambler.Services;

/// <summary>
/// Stores the Gemini API key in Windows Credential Manager (generic credential, per user, this machine only).
/// If nothing is stored, the GEMINI_API_KEY / GOOGLE_API_KEY environment variables are used read-only.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const string Target = "Rambler/GeminiApiKey";

    public bool IsFromEnvironment { get; private set; }

    public string? GetApiKey()
    {
        IsFromEnvironment = false;
        if (CredRead(Target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == 0) return null;
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                var key = Encoding.Unicode.GetString(bytes);
                Array.Clear(bytes);
                return key;
            }
            finally
            {
                CredFree(ptr);
            }
        }

        var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            IsFromEnvironment = true;
            return env.Trim();
        }
        return null;
    }

    public void SetApiKey(string apiKey)
    {
        var bytes = Encoding.Unicode.GetBytes(apiKey.Trim());
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "Gemini API key",
                Comment = "Used by Rambler for Gemini transcription and cleanup.",
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't save the API key to Credential Manager.");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public void DeleteApiKey()
    {
        if (!CredDelete(Target, CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_NOT_FOUND) throw new Win32Exception(error, "Couldn't remove the API key.");
        }
    }
}
