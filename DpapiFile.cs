using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarness;

/// <summary>
/// DPAPI（CurrentUser 作用域）文件加密/解密。
///
/// 为什么需要它：web-url.txt 含完整认证 token，任何能读 %LOCALAPPDATA% 的进程
/// 都能拿到。DPAPI 加密后，文件内容只有**同一个 Windows 用户**才能解开——
/// 其他用户、其他机器、甚至同一台机器上被提权的进程都读不出明文。
///
/// 格式：文件首行是 "dpapi:"（4 字节 ASCII 标记 + 冒号），后续是 Base64 密文。
/// 旧版本写入的纯明文文件会被自动识别并**原地升级**为加密格式（读时检测，写时转换）。
/// 零配置、零依赖（.NET 自带），用户完全无感。
/// </summary>
internal static class DpapiFile
{
    private const string Magic = "dpapi:";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// 写入：先 DPAPI 加密，再 Base64 编码落盘。
    /// </summary>
    internal static void WriteAllText(string path, string plainText)
    {
        var plainBytes = Utf8NoBom.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        var encoded = Magic + Convert.ToBase64String(encrypted);
        // 写文件失败时静默降级：明文也比不写强（认证链接对用户可见，只是暴露面回到旧版）
        try { File.WriteAllText(path, encoded, Utf8NoBom); }
        catch { }
    }

    /// <summary>
    /// 读取：检测到加密头则 DPAPI 解密；否则视为旧版明文，读取后**自动升级**为加密格式。
    /// </summary>
    internal static string? ReadAllText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path, Utf8NoBom).Trim();
            if (raw.Length == 0) return null;

            if (!raw.StartsWith(Magic, StringComparison.Ordinal))
            {
                // 旧版明文：读出来，原地转成加密格式
                try { WriteAllText(path, raw); } catch { }
                return raw;
            }

            var base64 = raw[Magic.Length..];
            var encrypted = Convert.FromBase64String(base64);
            var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Utf8NoBom.GetString(plainBytes);
        }
        catch { return null; }
    }
}
