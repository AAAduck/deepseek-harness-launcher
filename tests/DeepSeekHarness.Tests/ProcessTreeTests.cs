using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 杀进程树收集（<see cref="ProcessMatch.CollectProcessTreeIds"/>）的单测。
///
/// "杀哪些进程"是整个启动器最贵的判断：命中即连整棵树一起杀，判错的后果不可逆，
/// 且两种错法都不会报错。种子匹配（MatchesHarnessCommand/IsEngineProcess）已由
/// ProcessMatchTests 钉住，这里钉的是**边**：ParentId 本身不构成父子证明——
/// Windows 会把已退出进程的 PID 复用给引擎，孤儿进程的 ParentId 于是指向引擎的
/// PID，快照里它看起来就像引擎的子进程。此前对边不做校验，撞上就把无关进程
/// 连同它的整棵子树误杀。真实子进程必然晚于父进程创建——这条时间规则是唯一的
/// 防伪依据，改收集逻辑前先看这里。
/// </summary>
public class ProcessTreeTests
{
    private static readonly DateTime T0 = new(2026, 10, 2, 10, 0, 0, DateTimeKind.Local);

    private static ProcessRecord Rec(int id, int parent, DateTime start) =>
        new(id, parent, "node.exe", "node bin.js", start);

    private static HashSet<int> Collect(params ProcessRecord[] records)
    {
        var map = records.ToDictionary(r => r.Id);
        var seeds = new[] { 1 };   // 种子 = 引擎本体（种子匹配由 ProcessMatchTests 钉）
        return ProcessMatch.CollectProcessTreeIds(seeds, map);
    }

    [Fact]
    public void 真实子进程与孙进程_整树收编()
    {
        var all = Collect(
            Rec(1, 0, T0),                 // 引擎（种子）
            Rec(10, 1, T0.AddSeconds(1)),  // node 子进程
            Rec(11, 10, T0.AddSeconds(2)), // 孙进程
            Rec(12, 11, T0.AddSeconds(3)));

        Assert.Equal(new HashSet<int> { 1, 10, 11, 12 }, all);
    }

    [Fact]
    public void 父PID复用的孤儿_不收编()
    {
        // 事故形状：孤儿 X 的真实父进程早已退出，其 PID 被引擎（T0+5 启动）复用，
        // 快照里 X.ParentId == 引擎 PID——但 X 比引擎早 5 秒存在，绝非引擎之子。
        // 此前无时间校验，X 连同它自己的整棵子树被 entireProcessTree 误杀。
        var all = Collect(
            Rec(1, 0, T0.AddSeconds(5)),       // 引擎（种子，晚起）
            Rec(50, 1, T0),                    // 孤儿 X：创建时间早于"父进程"
            Rec(51, 50, T0.AddSeconds(1)));    // X 的子进程：随 X 一起不被收编

        Assert.Equal(new HashSet<int> { 1 }, all);
    }

    [Fact]
    public void CreationDate缺失的进程_不收编_没有依据就不冒险()
    {
        var all = Collect(
            Rec(1, 0, T0),
            new ProcessRecord(60, 1, "node.exe", "node bin.js", DateTime.MinValue));

        Assert.Equal(new HashSet<int> { 1 }, all);
    }

    [Fact]
    public void 与父进程同时刻创建的_不收编_宁漏不误()
    {
        // 严格大于：时间戳相同（时钟精度内分不出先后）时按"可能是伪造的边"处理，
        // 与"宁可漏掉一个残留，也不能误杀"的失手方向一致。
        var all = Collect(
            Rec(1, 0, T0),
            Rec(70, 1, T0));

        Assert.Equal(new HashSet<int> { 1 }, all);
    }
}
