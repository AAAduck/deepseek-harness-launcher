using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 版本槽目录名识别的单测。
///
/// <c>engine.&lt;这一段&gt;</c> 算不算一个「版本管理」里该出现的条目，判错是
/// **双向**的：把中转目录列出来，用户会看到一个叫 "migrating.1a2b3c4d" 的怪条目
/// （点下去是"切换/删除"，而它其实是一份正在被归档的数据）；
/// 反过来把真版本槽漏掉，那份 214 MB 的引擎就成了用户看不见、也管不了的东西
/// ——"引擎的反面是用户机器上静静躺着一份谁也动不了的东西"。
///
/// 排除清单里的三种形状都是**中转目录**，不是版本：
/// <c>engine.old</c>（升级换下来的上一版本，升级路径仍在用）、
/// <c>engine.tmp</c>（安装暂存）、<c>engine.migrating</c> 及其认领副本
/// <c>engine.migrating.&lt;guid 前 8 位&gt;</c>（归档期间独占那份数据，瞬时状态）。
///
/// 排除清单里的三种判据**一律不区分大小写**。本机这三个目录都由本程序以小写创建，
/// 现实里走不到 "OLD"/"TMP" 这类形状；但既然这个函数的职责就是把不像版本槽的挡在门外，
/// 三条判据就该用同一套规则。曾经 migrating 走 OrdinalIgnoreCase 而 old/tmp 走区分
/// 大小的常量模式，于是 "OLD" 会被当成版本槽放行——下面这组用例把它钉死。
/// </summary>
public class EngineSlotNameTests
{
    [Theory]
    [InlineData("0.1.5")]
    [InlineData("1.4.1-rc.2")]   // 引擎本体的预发布版：真版本槽
    [InlineData("latest")]        // 指向 latest 的槽也要能列出来
    public void 正常版本号_是版本槽(string version)
    {
        Assert.True(HarnessForm.IsEngineSlotName(version));
    }

    [Theory]
    [InlineData("old")]                  // engine.old：升级换下来的上一版本
    [InlineData("tmp")]                  // engine.tmp：安装暂存
    [InlineData("migrating")]            // engine.migrating：待归档
    [InlineData("migrating.1a2b3c4d")]   // 认领副本：归档期间的私有形态（事故里点名的那个怪条目）
    public void 中转目录_不是版本槽(string version)
    {
        Assert.False(HarnessForm.IsEngineSlotName(version));
    }

    [Theory]
    [InlineData("Migrating")]            // 前缀判定是大小写不敏感的
    [InlineData("Migrating.X")]          // 认领副本的大小写变体：同样躲不掉
    [InlineData("MIGRATING.1a2b3c4d")]
    [InlineData("migRatinG.1a2b3c4d")]
    [InlineData("OLD")]                  // 曾是三条例外里唯一区分大小写的那条
    [InlineData("Tmp")]
    public void 中转目录的大小写变体_同样不是版本槽(string version)
    {
        // 枚举目录名时三种判据必须同一种口径：只要其中一条换成区分大小写，
        // 一个被改名/被手工重建过的残留就会以 "migrating.1a2b3c4d" 或 "OLD"
        // 的名义出现在「版本管理」里，而它装着的要么是别人正在归档的那份引擎，
        // 要么是升级换下来的上一版本——两条路都会让用户对着一堆怪条目手足无措。
        Assert.False(HarnessForm.IsEngineSlotName(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空名_不是版本槽(string? version)
    {
        // 纯函数必须自己挡脏输入：null/空串在 C# 里极易从
        // string[prefix.Length..] 之类的切片或目录枚举的空结果里漏进来，
        // 而它一旦被判成 true 就会进到"切换版本"的路径里去。
        Assert.False(HarnessForm.IsEngineSlotName(version));
    }

    [Fact]
    public void 放行方向必须一样确定_漏掉一个真版本槽就是一份没人管得着的214MB()
    {
        // 排除项只该吃掉中转目录，不许顺带吃掉版本号。实现若从"整段相等 + 前缀判定"
        // 退化成别的口径（Contains、或把判据整体写成 StartsWith 排除），最常见版本
        // （0.1.5、1.4.1-rc.2）随时可能被一起吃掉——而漏掉的后果比多列一条更安静：
        // 「版本管理」里根本看不到那个目录，插件不兼容时用户连回退的选项都没有。
        Assert.True(HarnessForm.IsEngineSlotName("0.1.5"));
        Assert.True(HarnessForm.IsEngineSlotName("1.4.1-rc.2"));
    }
}
