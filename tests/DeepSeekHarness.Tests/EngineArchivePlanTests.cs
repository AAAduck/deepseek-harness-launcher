using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 上一版本归档的规划规则。
///
/// 这段逻辑的价值全在**顺序**上，而顺序错了不会报错：
/// 引擎目录的反面是"用户机器上静静躺着一份 214 MB、界面里又看不见的东西"。
/// 跨进程互斥靠的是同卷目录改名是原子的（engine.old → engine.migrating 的认领），
/// 原子性由文件系统保证；由代码负责、也就值得被测住的是"什么时候动、动哪一步"。
/// </summary>
public class EngineArchivePlanTests
{
    [Fact]
    public void 活动引擎缺失时一律不动_那是恢复流程的活()
    {
        // 此时若抢着归档，RecoverEngineSwap 就没有 engine.old 可搬回，
        // 活动目录会空着，用户直接吃一次 214 MB 重装。
        foreach (var old in new[] { false, true })
        foreach (var migrating in new[] { false, true })
        {
            var plan = HarnessForm.PlanEngineOldArchive(engineExists: false, old, migrating);
            Assert.False(plan.FinishClaimed);
            Assert.False(plan.ClaimOld);
        }
    }

    [Fact]
    public void 只有engine_old_认领并归档()
    {
        var plan = HarnessForm.PlanEngineOldArchive(engineExists: true, oldExists: true, migratingExists: false);
        Assert.False(plan.FinishClaimed);
        Assert.True(plan.ClaimOld);
    }

    [Fact]
    public void 什么都没有_什么都不做()
    {
        var plan = HarnessForm.PlanEngineOldArchive(engineExists: true, oldExists: false, migratingExists: false);
        Assert.False(plan.FinishClaimed);
        Assert.False(plan.ClaimOld);
    }

    [Fact]
    public void 遗留的认领必须先收尾_它占双份磁盘且不参与版本列举_用户看不见()
    {
        var plan = HarnessForm.PlanEngineOldArchive(engineExists: true, oldExists: false, migratingExists: true);
        Assert.True(plan.FinishClaimed);
        Assert.False(plan.ClaimOld);
    }

    [Fact]
    public void 两者都在时_先收尾再认领_顺序反了认领会因目标已存在而落空()
    {
        // 收尾完 engine.migrating 才空出来，engine.old 才有地方可搬。
        // 顺序颠倒的结果是"认领失败"——安全但归档没做成，下次还得再来一遍。
        var plan = HarnessForm.PlanEngineOldArchive(engineExists: true, oldExists: true, migratingExists: true);
        Assert.True(plan.FinishClaimed);
        Assert.True(plan.ClaimOld);
    }
}
