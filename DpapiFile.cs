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
    ///
    /// 降级只有一级，且**永不把已加密的文件降级成明文**：DPAPI 不可用时写明文
    /// （好过链接无法持久化），但目标已是加密格式就什么都不写——明文降级是单向、
    /// 不可撤销的损失，而写失败时原子写保证原文件原封不动，"失败就不写"零代价
    /// （第二级"写不进再试明文"的教训见 DESIGN-NOTES.md §7）。降级**必须留痕**：
    /// 所有对外信号都指向"已加密"，静默降级 = 威胁模型失效而无人知晓。
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
        catch (Exception ex)
        {
            // ① DPAPI 不可用：降级明文，别让加密功能连累链接持久化。
            // 但绝不覆盖一份已经加密好的文件——读侧解不开时返回 null，
            // 后果只是"这轮不复用引擎、重新拉一个"，明文降级的后果却是永久的。
            if (IsEncryptedFile(path))
            {
                Log($"目标已加密、DPAPI 本次不可用，保留原有密文不降级：{path}（{ex.GetType().Name}）");
                return;
            }
            Log($"DPAPI 不可用，本次降级为明文：{path}（{ex.GetType().Name}: {ex.Message}）");
            // 保持本方法"写不进也不抛"的旧契约（调用方各有自己的 catch，但契约别变）。
            try { WriteAtomic(path, plainText); } catch { }
            return;
        }
        // 写失败就保持原状：原文件要么是旧的完整内容，要么是这次的完整内容，
        // 绝不会是"半份"——降级写明文只会把这份原子性换来的好处全部作废。
        try { WriteAtomic(path, encoded); }
        catch (Exception ex)
        {
            Log($"加密内容写入失败（保持原文件，未降级）：{path}（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    /// <summary>
    /// 降级/失败留痕。走 startup-log 而不是抛：本类的契约是"永不抛"。
    /// 直接调 <see cref="HarnessForm.AppendStartupLog"/> 而不是 Swallow.Quiet——
    /// 后者带每小时节流，而降级是**持续状态**（DPAPI 一直被禁用），每小时一条
    /// 正好够用；而这里传的是已经格式化好的字符串，不需要 Swallow 的去重。
    /// </summary>
    private static void Log(string message)
    {
        try { HarnessForm.AppendStartupLog("[dpapi] " + message); } catch { }
    }

    /// <summary>该路径上现有的文件是否已经是 dpapi 格式。读不出/不存在一律当"否"。</summary>
    internal static bool IsEncryptedFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var head = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Magic.Length];
            var read = head.Read(buf, 0, buf.Length);
            return read == Magic.Length &&
                   string.Equals(Encoding.ASCII.GetString(buf), Magic, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>
    /// 原子写入：先写同目录临时文件，再同卷 Move 覆盖。File.WriteAllText 是原地
    /// 覆写——进程被杀/断电落在写入中途，文件就停在半截 dpapi:base64，读侧解不开
    /// → 复用静默失效、走完整重启。同卷 Move 在 NTFS 上是原子的：任何时刻目标要么
    /// 是旧的完整内容，要么是新的完整内容（与 ConfigBackup.Restore 同一条纪律）。
    ///
    /// 临时文件名**必须每次唯一**：固定 ".tmp" 在两个写入者落到同一路径时会互相
    /// 覆盖、再互相 Move——一份内容变成孤儿，另一份被 Move 走之后留下的残骸
    /// 还会被后者的 finally 删掉。写同一个文件的路径确实不止一条
    /// （urlFile 由启动路径与复用路径各写一次）。
    /// </summary>
    private static void WriteAtomic(string path, string contents)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
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
                // 旧版明文：读出来，原地转成加密格式。
                // 转不成（DPAPI 被禁用）时**必须留痕**——否则这个文件会**永远**是
                // 明文，而所有对外信号都显示"已加密"。降级是持续状态，不是瞬时事件。
                Log($"读到旧版明文文件，正在原地升级为加密格式：{path}");
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
