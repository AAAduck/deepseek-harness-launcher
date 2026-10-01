using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarness;

/// <summary>
/// DPAPI（CurrentUser 作用域）文件加密/解密。
///
/// 为什么需要它：web-url.txt 含完整认证 token，任何能读 %LOCALAPPDATA% 的进程
/// 都能拿到。DPAPI 加密后，文件内容只有**同一个 Windows 用户**才能解开——
/// 其他用户、其他机器都读不出明文。
///
/// 边界要说准：CurrentUser 作用域认的是用户 SID，不是权限级别，所以**同一用户的
/// 任何进程**（包括以管理员身份启动的那些）都能 Unprotect。它挡住的是"别的用户
/// 或别的机器拿到这个文件"，不是"本机上更高级的代码"。写成一个更弱的断言
/// 只会让人误以为还有一层保护。
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
    /// 两级降级，均与注释承诺的"明文也比不写强"对齐：
    ///   ① DPAPI 不可用（企业策略禁用等）→ 写明文。暴露面回到旧版，好过
    ///      "链接无法持久化"——那会让引擎复用功能静默失效（读取方拿 null）。
    ///   ② 加密成功但加密格式写不进（路径暂时不可写）→ 再试一次明文。
    ///   ③ 明文也写不进（磁盘满/权限被撤）→ 才什么都不留；此时读取方本来
    ///      也会拿到 null，行为与旧版"写不进"一致，不算新增损失。
    /// </summary>
    internal static void WriteAllText(string path, string plainText)
    {
        var plainBytes = Utf8NoBom.GetBytes(plainText);
        string encoded;
        try
        {
            var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            encoded = Magic + Convert.ToBase64String(encrypted);
        }
        catch
        {
            // ① DPAPI 不可用：降级明文，别让加密功能连累链接持久化。
            // 保持本方法"写不进也不抛"的旧契约（调用方各有自己的 catch，但契约别变）。
            try { WriteAtomic(path, plainText); } catch { }
            return;
        }
        try { WriteAtomic(path, encoded); }
        catch
        {
            // ② 加密格式写不进：降级明文再试一次。
            try { WriteAtomic(path, plainText); } catch { }
        }
    }

    /// <summary>
    /// 原子写入：先写同目录临时文件，再同卷 Move 覆盖。File.WriteAllText 是原地
    /// 覆写——进程被杀/断电落在写入中途，文件就停在半截 dpapi:base64，读侧解不开
    /// → 复用静默失效、走完整重启。同卷 Move 在 NTFS 上是原子的：任何时刻目标要么
    /// 是旧的完整内容，要么是新的完整内容（与 ConfigBackup.Restore 同一条纪律）。
    /// </summary>
    private static void WriteAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents, Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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
