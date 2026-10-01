using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// StartTime 容差比较（IsSameProcessStart）的单测。
///
/// 它守着两条杀进程路径（「停止」与关窗清扫）里共同的 PID 复用防护：
///   判"同"失败 → 真目标被跳过（「停止」空转，不报错——本工具最忌讳的失手形态）；
///   判"异"失败 → PID 复用后的新进程被误杀。
///
/// 容差的存在理由（实测数据，见函数注释）：WMI CreationDate 是 DMTF 微秒精度、
/// Process.StartTime 是 100ns 精度，同一进程的两个读数恒差 0–0.9 µs，
/// 严格相等在 21 个进程样本里只命中 3 个。边界值全部来自这组实测。
/// </summary>
public class ProcessStartToleranceTests
{
    private static readonly DateTime Base = new(2025, 6, 1, 12, 0, 0);

    [Theory]
    // —— 同一进程的两个读数：0–0.9 µs 的精度差必须判"同" ——
    // 实测差值分布（ticks = 100ns）：0,1,3,4,5,6,7,8,9
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]      // 0.9 µs：实测观察到的最大精度差
    [InlineData(10)]     // 正好 1 µs（1 个 tick 不会出现，但边界顺带钉住）
    public void 同一进程的精度差_判同(long deltaTicks)
    {
        Assert.True(HarnessForm.IsSameProcessStart(Base, Base.AddTicks(deltaTicks)));
        Assert.True(HarnessForm.IsSameProcessStart(Base.AddTicks(deltaTicks), Base));   // 双向
        Assert.True(HarnessForm.IsSameProcessStart(Base, Base.AddTicks(-deltaTicks)));  // 负方向
    }

    [Fact]
    public void 正好1毫秒_仍判同_边界含等号()
    {
        // 容差取 <=：1ms 正好是"判同"与"判异"的分界（TicksPerMillisecond）。
        // 写成 < 的话，1ms 整的差会被判"异"——而 PID 复用的时间差以秒计，
        // 这 1ms 不影响防护语义，只是边界约定的自我一致。
        Assert.True(HarnessForm.IsSameProcessStart(Base, Base.AddMilliseconds(1)));
    }

    [Theory]
    [InlineData(2)]      // 2 ms
    [InlineData(100)]    // 10 ms
    [InlineData(1000)]   // 100 ms：远小于 PID 复用的现实时间差，但足以排除"同进程"
    public void 超出容差_判异(long ms)
    {
        Assert.False(HarnessForm.IsSameProcessStart(Base, Base.AddMilliseconds(ms)));
        Assert.False(HarnessForm.IsSameProcessStart(Base.AddMilliseconds(ms), Base));
    }

    [Fact]
    public void PID复用的现实时间差_判异()
    {
        // PID 复用的前提是旧进程完全退出、旧句柄全部关闭——现实时间差以秒/分钟计。
        // 秒级差异必须判"异"，这条是防护语义本体。
        Assert.False(HarnessForm.IsSameProcessStart(Base, Base.AddSeconds(1)));
        Assert.False(HarnessForm.IsSameProcessStart(Base, Base.AddMinutes(3)));
    }

    [Fact]
    public void 严格相等_判同()
    {
        // 没有精度差时的理想形状（少数进程确实如此——实测 21 个里 3 个）。
        Assert.True(HarnessForm.IsSameProcessStart(Base, Base));
    }

    [Fact]
    public void Kind不同的同一时刻_也判同()
    {
        // WMI 转出来的 CreationDate 是 Utc Kind，Process.StartTime 是 Local Kind。
        // DateTime 的 == 只比 Ticks 不比 Kind，容差比较用 Ticks 差同样不受影响；
        // 钉住这一点，防止将来有人"好心"改成 Equals（那是比 Kind 的）。
        var utc = DateTime.SpecifyKind(Base, DateTimeKind.Utc);
        var local = DateTime.SpecifyKind(Base, DateTimeKind.Local);
        Assert.True(HarnessForm.IsSameProcessStart(utc, local));
    }
}
