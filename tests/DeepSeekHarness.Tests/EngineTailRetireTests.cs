using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 引擎 tail 代际令牌判定的单测（退役语义）。
///
/// 它守着一条不变量：**上一会话的 <c>?token=</c> 行一行都不许发出去**。违反它
/// 不抛异常、不写日志、不打任何警告，只在用户眼前发生——<c>authenticatedUrl</c>
/// 被过期 token 复活、OpenBrowser 弹死链接标签页、web-url.txt 被写脏，
/// 启动等待循环还会在新引擎还没输出任何日志时就提前判"启动成功"。
///
/// 判据只有"相等"：令牌只增不减、不复用，所以"新引擎起跑"和"杀引擎退役"都是加一，
/// 旧循环下一次读就发现自己过期。三处守卫（循环顶 ×2、逐行分发 ×1）必须走
/// <c>HarnessForm.TailGenerationAlive</c> 这**一个**判定——各写各的
/// <c>token != engineTailToken</c> 就多一处能被"顺手改坏"的地方，
/// 而这类改坏编译器一句都不会说。
/// </summary>
public class EngineTailRetireTests
{
    [Theory]
    [InlineData(0)]      // 首个引擎：令牌从 0 起
    [InlineData(1)]
    [InlineData(7)]
    public void 同代令牌_判还在_否则这个循环连一行日志都读不到(int token)
    {
        // 守卫是"逐行"生效的：判错成 false 时，正在退场的旧循环会在手里已经攥着
        // 一整段刚读进来的内容的情况下静默丢弃它们（判 true 则是把旧会话的
        // token 行照发不误）。两个方向都不会报错。
        Assert.True(HarnessForm.TailGenerationAlive(token, token));
    }

    [Fact]
    public void 新引擎起跑后_旧循环下一次读就过期()
    {
        // 起跑处是 ++engineTailToken。旧循环捕获的是自己那一份（3），
        // 之后无论循环顶还是逐行分发，读到的当前令牌都已经是 4。
        Assert.True(HarnessForm.TailGenerationAlive(3, 3));    // 起跑前：同代，照常分发
        Assert.False(HarnessForm.TailGenerationAlive(3, 4));   // 起跑后：立刻退场
    }

    [Fact]
    public void 杀引擎退役后_旧循环必须立刻过期_否则退场排空会把过期token写回去()
    {
        // 这正是此前缺的那一步：只有"新引擎起跑"换令牌，于是杀掉引擎到新引擎起跑
        // 之间的那个窗口里，旧 tail 仍拿着当前令牌通过逐行守卫——
        // 它的退场排空（最多 4×150 ms）会把刚被删掉的 web-url.txt 重写成
        // **过期 token**、authenticatedUrl 复活，紧接着的等待循环
        // （authenticatedUrl is not null → return）就提前判"启动成功"。
        // 全程零报错：用户看到的只是"启动了，但打开的链接打不开"。
        const int loopToken = 11, current = 12;   // 12 = RetireEngineTail 之后的当前令牌
        Assert.True(HarnessForm.TailGenerationAlive(loopToken, loopToken));   // 退役前
        Assert.False(HarnessForm.TailGenerationAlive(loopToken, current));    // 退役后
    }

    [Fact]
    public void 令牌只增不减_旧令牌永远不会再对上_这条是上面两条的前提()
    {
        // 守卫能一劳永逸地盖住"杀引擎"那条路，靠的是单调性：一旦哪天把退役
        // 改成置零或减一（比如为了"重置状态"），正在退场的旧循环会重新变回
        // "当前代"，上面两条会一起变绿，而生产那边不会报任何错。
        // 这里把连续五轮换代/退役走一遍：新代永远活着，旧代每轮都死。
        var current = 5;
        var staleLoopToken = current;
        for (var i = 0; i < 5; i++)
        {
            current++;                                     // 新引擎起跑 或 杀引擎退役
            Assert.False(HarnessForm.TailGenerationAlive(staleLoopToken, current));
            Assert.True(HarnessForm.TailGenerationAlive(current, current));   // 新代自己当然是当前代
        }
        Assert.True(current > staleLoopToken);
    }

    [Fact]
    public void 判定必须严格相等_不能是任何一种宽松比较()
    {
        // 钉住"相等"这个唯一判据本身：把实现改成 >= / > / "差值小于若干"之类的
        // 宽松写法，旧循环就会在令牌只差 1（换代/退役后的**正常**情形）时继续存活，
        // 上面几条会一起翻红——而现场表现与"没加守卫"完全一样。
        Assert.False(HarnessForm.TailGenerationAlive(3, 4));
        Assert.False(HarnessForm.TailGenerationAlive(3, 6));
        Assert.True(HarnessForm.TailGenerationAlive(3, 3));
    }
}
