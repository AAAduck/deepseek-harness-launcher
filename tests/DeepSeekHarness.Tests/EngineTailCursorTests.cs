using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 换代游标起点的单测（1.4.1）。
///
/// 钉住的失手形态：引擎 stdio 走 cmd `>>` 追加，历史（含上一会话的 `?token=` 行）
/// 留在盘上；tail 游标若从 0 起步就会把历史整体回放一遍——旧 token 行回填
/// authenticatedUrl、OpenBrowser 弹死链接标签页、写脏 web-url.txt，启动等待循环
/// 还会在新引擎就绪前因 authenticatedUrl != null 提前判成"启动成功"。
/// 全部是"错了不报错、只在用户眼前发生"的那一类。
///
/// 结构上这次已把回归做成**编译错误**：LogCursor 没有无参构造，起点必须显式给出。
/// 这里的用例守住的是另一半——起点的值本身。把调用点的"当前文件末尾"改成 0，
/// 生产不再报错，但下面第一条的语义注释与整体设计就作废了（起点值来自 IO，
/// 单测只能钉住纯函数部分：钳制与透传）。
/// </summary>
public class EngineTailCursorTests
{
    [Theory]
    [InlineData(0)]          // 全新机器 / 日志不存在：从 0 起步没有历史可回放，安全
    [InlineData(82)]         // 本机实测形状：一行 token 记录——新代必须整段跳过
    [InlineData(8388608)]    // 恰好是截断保留的 8 MB 尾部末尾
    public void 起点原样透传_新代游标就站在线性末尾(long start)
    {
        Assert.Equal(start, new HarnessForm.LogCursor(start).Pos);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void 负起点钳到0_绝不喂给Seek(long start)
    {
        // FeedEngineLogChunk 把 Pos 直接交给 fs.Seek——负值抛异常，而整个 tail 循环
        // 的读取包在 catch { } 里：认证链接会永远到不了、启动等待默默超时。
        // 这就是"判错了不报错"，所以钳制必须在构造处、且被钉住。
        Assert.Equal(0, new HarnessForm.LogCursor(start).Pos);
    }
}
