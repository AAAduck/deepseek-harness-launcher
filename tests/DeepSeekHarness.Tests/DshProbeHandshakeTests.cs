using System.Net;
using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 端口探针身份判定的单测（"这个响应是不是 DSH Web"）。
///
/// 这是整个启动器里唯一一个**会主动把 token 发出去**的判定点（复用路径
/// <c>ProbeUrlAsync</c>），也是启动前那道"端口被别的程序占用"预检
/// （<c>ProbeServerAsync</c>）的同一份逻辑。判错的两侧都不报错：
/// 误判成"是 DSH"时，状态栏跟着误报"运行中"、浏览器被开到不相干的进程上、
/// token 进了它的访问日志，而预检被绕过，用户最后看到的是引擎 EADDRINUSE 的
/// 原始报错——正是那道预检要避免的结局。
///
/// 核心一条：**200 也必须有 body 标记**。本机上任何一个 dev server
/// （本项目自己的 web profile 就跑在 Vite 上）对 <c>/</c> 回 200 是再正常不过的事，
/// 只看状态码就等于把"3080 上坐着别人"当成了自己人。
/// </summary>
public class DshProbeHandshakeTests
{
    [Theory]
    // 引擎的两条正常形态：带对 token → 200 + SPA 首页（<title> 那一行）；
    // 不带 token → 401 + 那句纯文本提示。两个标记都取自引擎自身产物。
    [InlineData(HttpStatusCode.OK, false, true, true)]
    [InlineData(HttpStatusCode.Unauthorized, true, false, true)]
    public void 状态码与body标记对得上_认得出是DSH(HttpStatusCode status, bool auth, bool spa, bool expected)
    {
        Assert.Equal(expected, HarnessForm.IsDshHandshake(status, auth, spa));
    }

    [Theory]
    // —— 事故本体：200 却没有 body 标记 ——
    // 旧实现对 200 一律放行，于是本机上任意一个占住 3080 的 dev server
    // （对 "/?token=…" 回 200 是它的日常行为）被当成 DSH。
    [InlineData(HttpStatusCode.OK, true, false)]        // 只有状态码：绝不放行
    [InlineData(HttpStatusCode.OK, false, false)]       // 只有状态码：绝不放行
    // 401 分支对称：标记必须与状态码配套，串了就不认。
    [InlineData(HttpStatusCode.Unauthorized, false, true)]
    [InlineData(HttpStatusCode.Unauthorized, false, false)]
    public void 状态码与body标记对不上_一律不认(HttpStatusCode status, bool auth, bool spa)
    {
        Assert.False(HarnessForm.IsDshHandshake(status, auth, spa));
    }

    [Theory]
    // 其它状态码无论带什么标记都不是 DSH：调用方只在 200/401 上读 body，
    // 这里的 auth/spa 传 true 只是钉住"标记不能反过来救一个不认识的状态码"。
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]              // 302：被重定向到别处也算不得数
    [InlineData(HttpStatusCode.TemporaryRedirect)]  // 307
    [InlineData(HttpStatusCode.NoContent)]          // 204
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void 别的状态码_即便body里两个标记都有_也不认(HttpStatusCode status)
    {
        Assert.False(HarnessForm.IsDshHandshake(status, authMarkerSeen: true, spaMarkerSeen: true));
    }

    [Fact]
    public void 失手方向只有一个_宁可说不是DSH()
    {
        // 把上面整张表反过来读一遍：所有"认得出"的情形都要求标记，
        // 所有"只有状态码"的情形都被拒绝。哪天有人为了少读一次 body
        // 把 200 分支改成 `HttpStatusCode.OK => true`（或把标记检查删掉），
        // 这两条断言会立刻红——而那正是本地 dev server 占端口时的真实事故。
        Assert.False(HarnessForm.IsDshHandshake(HttpStatusCode.OK, authMarkerSeen: false, spaMarkerSeen: false));
        Assert.True(HarnessForm.IsDshHandshake(HttpStatusCode.OK, authMarkerSeen: false, spaMarkerSeen: true));
    }
}
