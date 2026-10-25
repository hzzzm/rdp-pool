using System.Security.Cryptography;
using System.Text;

namespace RdpPool.Config;

/// <summary>
/// 本机当前用户 DPAPI（ProtectedData CurrentUser）。密文仅同一 Windows 用户可解。
/// </summary>
public static class SecretProtector
{
    public static string ProtectToBase64(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var enc = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public static string UnprotectFromBase64(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return "";
        var enc = Convert.FromBase64String(base64.Trim());
        var plain = ProtectedData.Unprotect(enc, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public static bool TryUnprotectFromBase64(string? base64, out string plaintext, out string? error)
    {
        plaintext = "";
        error = null;
        if (string.IsNullOrWhiteSpace(base64)) return true;
        try
        {
            plaintext = UnprotectFromBase64(base64);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
