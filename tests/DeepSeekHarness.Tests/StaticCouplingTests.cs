using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace DeepSeekHarness.Tests;

/// <summary>
/// 测「测试套件不被 UI 静态状态绑架」这件事本身。
///
/// 这类断言看起来怪：一个 <c>TypeInitializationException</c> 能有什么可测的？
/// 答案在于它是整套套件能不能在没有 GUI 的机器上跑的前提——
/// Windows Server Core、容器 CI、精简版 Windows 上**没有 GDI+ 字体**，
/// 而且常常没有 LOCALAPPDATA。只要 HarnessForm 的类型初始化器里还留着
/// <c>new Font(...)</c> 与 LocalAppDir 解析，**一次纯字符串函数的调用**就能让
/// 整套测试红成一条与被测逻辑毫无关系的 TypeInitializationException。
///
/// 而这条约束无法用常规用例表达——它不是"某个输入该得到某个输出"，
/// 而是"调用纯函数不该被迫初始化 GDI+ 与文件系统路径"。所以只能直接断言
/// 类型初始化器**没有**留下那两样东西。
/// </summary>
public class StaticCouplingTests
{
    /// <summary>
    /// 触发类型初始化器但不执行任何 UI 初始化：取一个纯 static 常量的值。
    /// 如果字体或路径解析仍在静态字段初始化器里，这里就会抛
    /// TypeInitializationException / 外层 finally 里那圈 AppDomain 事件也会被触发。
    /// </summary>
    private static void TouchTypeWithoutUiInitialization()
    {
        // EnginePackageName 是编译期常量，取它不会真的强制运行 .cctor——
        // 所以改用反射显式触发，那才是"类型初始化器"这个动作本身。
        RuntimeHelpers.RunClassConstructor(typeof(HarnessForm).TypeHandle);
    }

    [Fact]
    public void 类型初始化器不再需要字体_纯函数调用不会拉起GDI()
    {
        var ex = Record.Exception(TouchTypeWithoutUiInitialization);
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("UiFont")]
    [InlineData("BoldFont")]
    [InlineData("StatusFont")]
    public void 三个字体不再是静态字段_而是惰性属性(string name)
    {
        var field = typeof(HarnessForm).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(field);
    }

    [Theory]
    [InlineData("LocalAppDir")]
    [InlineData("engineDir")]
    [InlineData("engineStageDir")]
    [InlineData("engineOldDir")]
    [InlineData("engineMigratingDir")]
    [InlineData("engineStdioLog")]
    [InlineData("webProfileDir")]
    [InlineData("userHomeDir")]
    public void 数据目录路径不再是静态字段_否则空LOCALAPPDATA会毒化整套套件(string name)
    {
        // 这些字段全都链在 LocalAppDir 上。只要有一个仍是 static readonly，
        // 类型初始化器就会顺带把 LOCALAPPDATA 解析算出来——而它在受限账户 /
        // 容器里可能返回空，于是 Path.Combine 出来的是相对路径，
        // 紧接着那个 throw 就把**整个测试程序集**炸掉。
        var field = typeof(HarnessForm).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(field);
    }

    [Fact]
    public void 惰性化没有删掉那份保护_解析方法仍在且仍是非公开静态()
    {
        // 惰性化只是把"类型首次加载时抛"推迟成"这条路径首次使用时抛"，
        // 方法本身一个字没动。真要在无 LOCALAPPDATA 的环境里验证那个 throw，
        // 需要 CI 上真的没有该变量——而 Windows 的 GetFolderPath 走 shell API
        // 而不是直接读环境变量，单测里改不动。所以这里只钉住"方法还在、签名没变"；
        // 真正被钉住的是上面那几条"不再是静态字段"——那才是耦合的成因。
        var method = typeof(HarnessForm).GetMethod("ResolveLocalAppDir",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(string), method.ReturnType);
    }
}