using System.Security.Cryptography;
using System.Text;

namespace CodeInsights.VisualStudio.Auth;

/// <summary>
/// Personal access token kept under DPAPI for the current Windows user. Settings
/// roam and appear in support bundles; this file does not.
/// </summary>
internal sealed class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("codeinsights.visualstudio.pat.v1");

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodeInsights",
        "visualstudio",
        "token.bin");

    public string? Get()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(bytes).Trim();
            return token.Length == 0 ? null : token;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Set(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, bytes);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
