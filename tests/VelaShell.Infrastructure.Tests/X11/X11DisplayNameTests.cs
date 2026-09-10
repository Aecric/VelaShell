using System.Net;
using System.Net.Sockets;
using VelaShell.Infrastructure.X11;

namespace VelaShell.Infrastructure.Tests.X11;

/// <summary>
/// <c>DISPLAY</c> 的解析与 endpoint 换算。
/// </summary>
/// <remarks>
/// 这一层全是纯函数,所以三平台的 CI 作业里都真跑 —— 判据是字符串与 endpoint,
/// 不碰环境变量也不建 socket。<c>DISPLAY</c> 的各种写法(空主机、<c>unix</c>、
/// <c>主机/unix</c>、<c>localhost:10.0</c>)在真实桌面上都遇得到,认错一个的后果是
/// 把 cookie 往错误的地址上送。
/// </remarks>
[TestClass]
[TestCategory("X11")]
public sealed class X11DisplayNameTests
{
    [TestMethod]
    [DataRow(":0", 0, 0, true, DisplayName = "最常见:空主机 + display 0")]
    [DataRow(":0.1", 0, 1, true, DisplayName = "带 screen 号")]
    [DataRow(":12", 12, 0, true, DisplayName = "两位 display 号")]
    [DataRow("unix:0", 0, 0, true, DisplayName = "显式 unix")]
    [DataRow("myhost/unix:0", 0, 0, true, DisplayName = "主机/unix 也是本机 socket")]
    [DataRow("MYHOST/UNIX:0", 0, 0, true, DisplayName = "主机/unix 不区分大小写")]
    [DataRow("localhost:10.0", 10, 0, false, DisplayName = "本机跑在别人的 ssh -X 里")]
    [DataRow("127.0.0.1:0", 0, 0, false, DisplayName = "环回 IPv4")]
    [DataRow("[::1]:0", 0, 0, false, DisplayName = "环回 IPv6 字面量")]
    public void ParsesLocalDisplays(string value, int display, int screen, bool unixSocket)
    {
        Assert.IsTrue(X11DisplayName.TryParse(value, out X11DisplayName? name, out X11ParseFailure failure),
                      $"「{value}」应当解析成功,却报了 {failure}。");
        Assert.AreEqual(X11ParseFailure.None, failure);
        Assert.AreEqual(display, name!.Display);
        Assert.AreEqual(screen, name.Screen);
        Assert.AreEqual(unixSocket, name.IsUnixSocket);
    }

    /// <remarks>
    /// 期望值走 <see cref="int" /> 而不是枚举本身:<see cref="X11ParseFailure" /> 是 internal
    /// (只在 Infrastructure 内部流动,不该出现在公开契约上),而 MSTest 的用例方法必须是 public,
    /// public 方法的签名里放不了 internal 类型(CS0051)。转换只在方法体里做一次。
    /// </remarks>
    [TestMethod]
    [DataRow(null, (int)X11ParseFailure.Empty, DisplayName = "DISPLAY 未设置")]
    [DataRow("", (int)X11ParseFailure.Empty, DisplayName = "空串")]
    [DataRow("   ", (int)X11ParseFailure.Empty, DisplayName = "只有空白")]
    [DataRow("0", (int)X11ParseFailure.Malformed, DisplayName = "没有冒号")]
    [DataRow(":", (int)X11ParseFailure.Malformed, DisplayName = "冒号后没有 display 号")]
    [DataRow(":abc", (int)X11ParseFailure.Malformed, DisplayName = "display 号不是数字")]
    [DataRow(":0.x", (int)X11ParseFailure.Malformed, DisplayName = "screen 号不是数字")]
    [DataRow(":-1", (int)X11ParseFailure.Malformed, DisplayName = "负的 display 号")]
    [DataRow("1.2.3.4:0", (int)X11ParseFailure.NotLocal, DisplayName = "远程 X Server")]
    [DataRow("192.168.1.5:0", (int)X11ParseFailure.NotLocal, DisplayName = "局域网地址也算远程")]
    public void RejectsWhatItCannotForwardTo(string? value, int expected)
    {
        Assert.IsFalse(X11DisplayName.TryParse(value, out X11DisplayName? name, out X11ParseFailure failure));
        Assert.IsNull(name);
        Assert.AreEqual((X11ParseFailure)expected, failure);
    }

    /// <summary>
    /// display 号大到换算不出合法端口时必须当成畸形值拒掉。
    /// 放过去的话,构造 <see cref="IPEndPoint" /> 会在建连路径上抛
    /// <see cref="ArgumentOutOfRangeException" /> —— 一个「DISPLAY 写错了」的输入
    /// 不该表现成一次崩溃。
    /// </summary>
    [TestMethod]
    public void RejectsDisplayNumbersThatOverflowTheTcpPort()
    {
        Assert.IsFalse(X11DisplayName.TryParse(":999999", out _, out X11ParseFailure failure));
        Assert.AreEqual(X11ParseFailure.Malformed, failure);
    }

    /// <summary>
    /// 本机 socket 型的 display 给两个候选:socket 文件优先,环回 TCP 兜底。
    /// 只给 socket 一个候选的话,<c>-nolisten local</c> 那类配置下就完全连不上了。
    /// </summary>
    [TestMethod]
    public void UnixDisplayOffersSocketThenLoopbackTcp()
    {
        Assert.IsTrue(X11DisplayName.TryParse(":7", out X11DisplayName? name, out _));

        IReadOnlyList<EndPoint> candidates = name!.CandidateEndPoints();

        Assert.HasCount(2, candidates);
        Assert.AreEqual(AddressFamily.Unix, candidates[0].AddressFamily);
        Assert.AreEqual($"{X11DisplayName.UnixSocketDirectory}/X7", candidates[0].ToString());
        Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 6007), candidates[1]);
    }

    /// <summary>
    /// 显式写了 <c>localhost</c> 的 display 只走 TCP。
    /// 这一形态几乎只出现在「VelaShell 自己跑在一条 ssh -X 里」的场景,
    /// 那个 display 后面根本没有本机 socket —— 再去试一遍 socket 只是白等一次连接超时。
    /// </summary>
    [TestMethod]
    public void LoopbackTcpDisplayOffersOnlyTcp()
    {
        Assert.IsTrue(X11DisplayName.TryParse("localhost:10", out X11DisplayName? name, out _));

        IReadOnlyList<EndPoint> candidates = name!.CandidateEndPoints();

        Assert.HasCount(1, candidates);
        Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 6010), candidates[0]);
    }

    /// <summary>IPv6 环回要连 <c>::1</c>,不是把它当成 IPv4。</summary>
    [TestMethod]
    public void IPv6LoopbackDisplayUsesIPv6EndPoint()
    {
        Assert.IsTrue(X11DisplayName.TryParse("[::1]:3", out X11DisplayName? name, out _));

        IReadOnlyList<EndPoint> candidates = name!.CandidateEndPoints();

        Assert.HasCount(1, candidates);
        Assert.AreEqual(new IPEndPoint(IPAddress.IPv6Loopback, 6003), candidates[0]);
    }
}
