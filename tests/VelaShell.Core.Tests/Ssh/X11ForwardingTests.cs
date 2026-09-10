using System.Net;
using System.Net.Sockets;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// X11 转发的中立类型:cookie 的遮罩与校验、请求对象的回执。
/// </summary>
[TestClass]
[TestCategory("X11")]
public sealed class X11ForwardingTests
{
    private const string Cookie = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// cookie 的 <c>ToString</c> 必须是遮罩。
    /// </summary>
    /// <remarks>
    /// 这条守的是一件很容易发生的事:cookie 是本机 X display 的凭据(拿到它就能读
    /// 键盘输入与屏幕内容),而它会被塞进插值字符串、异常消息、调试输出。
    /// 包成类型 + 遮罩 <c>ToString</c> 之后,顺手打印一句日志的默认结果是安全的,
    /// 明文只能显式调 <see cref="X11AuthenticationCookie.Reveal" /> 拿到。
    /// </remarks>
    [TestMethod]
    public void CookieNeverPrintsItself()
    {
        var cookie = new X11AuthenticationCookie(Cookie);

        Assert.AreEqual(X11AuthenticationCookie.Redacted, cookie.ToString());
        Assert.AreEqual(X11AuthenticationCookie.Redacted, $"{cookie}");
        Assert.DoesNotContain(Cookie, $"cookie = {cookie}");
        Assert.AreEqual(Cookie, cookie.Reveal(), "显式取明文仍要拿得到 —— 它要交给 SSH 库。");
    }

    [TestMethod]
    [DataRow("", DisplayName = "空串")]
    [DataRow("0123456789abcdef", DisplayName = "只有 8 字节")]
    [DataRow("0123456789abcdef0123456789abcdef0", DisplayName = "多一位")]
    [DataRow("0123456789abcdef0123456789abcdeg", DisplayName = "含非十六进制字符")]
    [DataRow("0123456789abcdef0123456789abcde\n", DisplayName = "末尾多一个换行(从命令输出里直接切出来的典型错误)")]
    public void RejectsAnythingThatIsNotA16ByteHexCookie(string hex)
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => new X11AuthenticationCookie(hex));
        // 被拒的这一串很可能是一个**正确**的 cookie(只是多了个换行),异常消息不能把它带出去。
        // 空串跳过这一句:任何字符串都"包含"空串,断言恒假,验不到任何东西。
        if (hex.Trim() is { Length: > 0 } candidate)
        {
            Assert.DoesNotContain(candidate, error.Message);
        }
    }

    /// <summary>
    /// 请求对象既是入参也是回执:刚建好是 <see cref="X11ForwardingOutcome.Pending" />,
    /// 由 SSH 实现在 shell 开完之后落一个结论。
    /// </summary>
    [TestMethod]
    public void RequestCarriesTheOutcomeBack()
    {
        var request = new X11ForwardingRequest(NewSettings());
        Assert.AreEqual(X11ForwardingOutcome.Pending, request.Outcome);
        Assert.IsNull(request.Detail);

        request.MarkRejectedByServer("The SSH server rejected X11 forwarding.");
        Assert.AreEqual(X11ForwardingOutcome.RejectedByServer, request.Outcome);
        Assert.AreEqual("The SSH server rejected X11 forwarding.", request.Detail);

        request.MarkEnabled();
        Assert.AreEqual(X11ForwardingOutcome.Enabled, request.Outcome);
        Assert.IsNull(request.Detail, "转成已启用之后不该还留着上一次被拒的说明。");
    }

    [TestMethod]
    public void RequestNeedsSettings() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new X11ForwardingRequest(null!));

    /// <summary>
    /// 探测结果的两个状态必须自洽:成功一定带 settings,失败一定不带。
    /// 调用方是照着 <see cref="X11DisplayProbe.IsAvailable" /> 直接 <c>Settings!</c> 取值的。
    /// </summary>
    [TestMethod]
    public void ProbeAvailabilityMatchesItsPayload()
    {
        var ok = new X11DisplayProbe(X11DisplayStatus.Available, NewSettings());
        Assert.IsTrue(ok.IsAvailable);

        X11DisplayProbe failed = X11DisplayProbe.Failed(X11DisplayStatus.NoDisplay, "DISPLAY is not set.");
        Assert.IsFalse(failed.IsAvailable);
        Assert.IsNull(failed.Settings);

        // 状态说成功却没带 settings —— 不该出现,真出现了也要按不可用处理而不是让调用方吃 null。
        Assert.IsFalse(new X11DisplayProbe(X11DisplayStatus.Available, null).IsAvailable);
    }

    private static X11ForwardingSettings NewSettings() =>
        new()
        {
            LocalEndPoint = new UnixDomainSocketEndPoint("/tmp/.X11-unix/X0"),
            Cookie = new X11AuthenticationCookie(Cookie),
            ScreenNumber = 0,
            DisplayName = ":0"
        };

    /// <summary>转发目标就是「往哪送 + 用什么 cookie + 哪块 screen」,原样留住。</summary>
    [TestMethod]
    public void SettingsKeepTheResolvedTarget()
    {
        var settings = new X11ForwardingSettings
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 6010),
            Cookie = new X11AuthenticationCookie(Cookie),
            ScreenNumber = 3,
            DisplayName = "localhost:10.3"
        };

        Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 6010), settings.LocalEndPoint);
        Assert.AreEqual(3, settings.ScreenNumber);
        Assert.AreEqual("localhost:10.3", settings.DisplayName);
        Assert.AreEqual(Cookie, settings.Cookie.Reveal());
    }
}
