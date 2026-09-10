using System.Net;
using System.Net.Sockets;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.X11;

namespace VelaShell.Infrastructure.Tests.X11;

/// <summary>
/// 本机 display 探测的全部分支。
/// </summary>
/// <remarks>
/// 三个外部依赖(<c>DISPLAY</c>、<c>xauth</c>、socket)全部注入,连"当前是不是 Linux"
/// 也注入 —— CI 是三平台矩阵,这些分支在 Windows / macOS 的作业里也必须真跑,
/// 而不是靠环境早退变成一片"跳过"(MSTest 把跳过记成通过)。
/// </remarks>
[TestClass]
[TestCategory("X11")]
public sealed class X11DisplayServiceTests
{
    private const string Cookie = "0123456789abcdef0123456789abcdef";

    /// <summary>可编排的 xauth:返回什么、被问了哪个 display,都由用例说了算。</summary>
    private sealed class StubXauth(XauthLookup result) : IXauthReader
    {
        internal string? AskedFor { get; private set; }

        public Task<XauthLookup> LookupAsync(string displayName, CancellationToken cancellationToken)
        {
            AskedFor = displayName;
            return Task.FromResult(result);
        }
    }

    private static X11DisplayService Service(
        string? display,
        IXauthReader xauth,
        Func<EndPoint, CancellationToken, Task<bool>>? reachable = null,
        bool platformSupported = true) =>
        new(() => display, xauth, reachable ?? ((_, _) => Task.FromResult(true)), platformSupported);

    private static StubXauth CookieFound() => new(new(XauthOutcome.Ok, Cookie, null));

    /// <summary>非 Linux 平台如实说「不支持」,而不是装作探测过。</summary>
    [TestMethod]
    public async Task NonLinuxPlatform_ReportsUnsupported()
    {
        var xauth = CookieFound();

        X11DisplayProbe probe = await Service(":0", xauth, platformSupported: false).ProbeAsync();

        Assert.AreEqual(X11DisplayStatus.PlatformUnsupported, probe.Status);
        Assert.IsNull(probe.Settings);
        Assert.IsNull(xauth.AskedFor, "平台不支持时不该再去跑 xauth。");
    }

    [TestMethod]
    [DataRow(null, X11DisplayStatus.NoDisplay, DisplayName = "DISPLAY 未设置(纯 Wayland、没有图形会话)")]
    [DataRow("", X11DisplayStatus.NoDisplay, DisplayName = "DISPLAY 是空串")]
    [DataRow("1.2.3.4:0", X11DisplayStatus.DisplayNotLocal, DisplayName = "DISPLAY 指向远程 X Server")]
    [DataRow("nonsense", X11DisplayStatus.DisplayMalformed, DisplayName = "DISPLAY 格式不认识")]
    public async Task BadDisplay_ReportsWhyAndNeverRunsXauth(string? display, X11DisplayStatus expected)
    {
        var xauth = CookieFound();

        X11DisplayProbe probe = await Service(display, xauth).ProbeAsync();

        Assert.AreEqual(expected, probe.Status);
        Assert.IsNull(probe.Settings);
        Assert.IsNull(xauth.AskedFor, "DISPLAY 都没解析出来,不该去问 xauth。");
    }

    /// <remarks>
    /// <see cref="XauthOutcome" /> 是 internal,而 MSTest 的用例方法必须 public ——
    /// public 签名里放不了 internal 类型(CS0051),所以期望值走 int(同
    /// <c>X11DisplayNameTests.RejectsWhatItCannotForwardTo</c>)。
    /// </remarks>
    [TestMethod]
    [DataRow((int)XauthOutcome.Missing, X11DisplayStatus.XauthMissing, DisplayName = "系统里没装 xauth")]
    [DataRow((int)XauthOutcome.NoEntry, X11DisplayStatus.CookieMissing, DisplayName = "xauth 里没有这个 display 的条目")]
    [DataRow((int)XauthOutcome.Failed, X11DisplayStatus.XauthFailed, DisplayName = "xauth 非零退出或超时")]
    public async Task NoCookie_MeansNoForwarding(int outcome, X11DisplayStatus expected)
    {
        // cookie 是库的硬性入参(必须 32 位十六进制),拿不到就没有"降级启用"这条路。
        X11DisplayProbe probe = await Service(
            ":0", new StubXauth(new((XauthOutcome)outcome, null, null))).ProbeAsync();

        Assert.AreEqual(expected, probe.Status);
        Assert.IsNull(probe.Settings);
    }

    /// <summary>
    /// cookie 取不到时不该再去连 X Server:那一步是为了把「连不上」和「没 cookie」分开报,
    /// 而这里已经能确定报什么了,多连一次只是白等一次超时。
    /// </summary>
    [TestMethod]
    public async Task NoCookie_SkipsTheReachabilityProbe()
    {
        var probed = new List<EndPoint>();

        await Service(":0", new StubXauth(new(XauthOutcome.Missing, null, null)),
                      (endPoint, _) => { probed.Add(endPoint); return Task.FromResult(true); }).ProbeAsync();

        Assert.IsEmpty(probed);
    }

    /// <summary>
    /// socket 文件在、cookie 也在,但没人 accept —— 这正是 X Server 已经退出而
    /// <c>/tmp/.X11-unix/X0</c> 还留在那儿的情形,只看文件存在会误判成可用。
    /// </summary>
    [TestMethod]
    public async Task NothingListening_ReportsServerUnreachable()
    {
        X11DisplayProbe probe = await Service(":0", CookieFound(), (_, _) => Task.FromResult(false)).ProbeAsync();

        Assert.AreEqual(X11DisplayStatus.ServerUnreachable, probe.Status);
        Assert.IsNull(probe.Settings);
    }

    /// <summary>顺利探到:endpoint、screen、cookie、DISPLAY 原值都要落在结果里。</summary>
    [TestMethod]
    public async Task HappyPath_ResolvesSocketCookieAndScreen()
    {
        var xauth = CookieFound();

        X11DisplayProbe probe = await Service(":0.2", xauth).ProbeAsync();

        Assert.IsTrue(probe.IsAvailable);
        Assert.AreEqual(X11DisplayStatus.Available, probe.Status);
        Assert.AreEqual(AddressFamily.Unix, probe.Settings!.LocalEndPoint.AddressFamily);
        Assert.AreEqual($"{X11DisplayName.UnixSocketDirectory}/X0", probe.Settings.LocalEndPoint.ToString());
        Assert.AreEqual(2, probe.Settings.ScreenNumber);
        Assert.AreEqual(":0.2", probe.Settings.DisplayName);
        Assert.AreEqual(Cookie, probe.Settings.Cookie.Reveal());
    }

    /// <summary>
    /// <c>DISPLAY</c> 原值(含 screen 段)原样交给 xauth,不做我们自己的重建 ——
    /// xauth 自己按 display 归一,少一层重建就少一处和它语义漂移的机会。
    /// </summary>
    [TestMethod]
    public async Task AsksXauthWithTheDisplayVerbatim()
    {
        var xauth = CookieFound();

        await Service("  :0.2  ", xauth).ProbeAsync();

        Assert.AreEqual(":0.2", xauth.AskedFor, "两端空白要去掉,其余原样透传。");
    }

    /// <summary>
    /// socket 连不上就退回环回 TCP。<c>-nolisten local</c> 这类配置下只有 TCP 活着,
    /// 而 <c>ssh -X</c> 也是两个都试。
    /// </summary>
    [TestMethod]
    public async Task FallsBackToLoopbackTcpWhenTheSocketIsDead()
    {
        X11DisplayProbe probe = await Service(
            ":5", CookieFound(),
            (endPoint, _) => Task.FromResult(endPoint is IPEndPoint)).ProbeAsync();

        Assert.IsTrue(probe.IsAvailable);
        Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 6005), probe.Settings!.LocalEndPoint);
    }

    /// <summary>诊断串里可以有 DISPLAY(报障第一个要问的就是它),但绝不能有 cookie。</summary>
    [TestMethod]
    public async Task DetailCarriesTheDisplayButNeverTheCookie()
    {
        X11DisplayProbe probe = await Service(":9", CookieFound(), (_, _) => Task.FromResult(false)).ProbeAsync();

        Assert.IsNotNull(probe.Detail);
        Assert.Contains(":9", probe.Detail);
        Assert.DoesNotContain(Cookie, probe.Detail);
    }
}
