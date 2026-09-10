using VelaShell.Infrastructure.X11;

namespace VelaShell.Infrastructure.Tests.X11;

/// <summary>
/// <c>xauth list</c> 输出的解析。
/// </summary>
/// <remarks>
/// 判据全是文本,不跑 xauth —— 三平台 CI 都真跑。样本取自真实输出:
/// GNOME/mutter 给 Xwayland 写的 authority 文件里是**两条**记录(FamilyLocal 与
/// FamilyWild),display 号那一段长度为 0,即通配。取第一条命中的即可,
/// 与 <c>ssh -X</c> 的做法一致。
/// </remarks>
[TestClass]
[TestCategory("X11")]
public sealed class XauthOutputParserTests
{
    /// <summary>本机实测的真实输出(cookie 已换成假值)。</summary>
    private const string MutterOutput =
        "my-thinkbook/unix:  MIT-MAGIC-COOKIE-1  edbd9f27d0a3627f071c26e0fb4a39b0\n" +
        "#ffff#6d792d7468696e6b626f6f6b#:  MIT-MAGIC-COOKIE-1  edbd9f27d0a3627f071c26e0fb4a39b0\n";

    [TestMethod]
    public void FindsCookieInRealMutterOutput()
    {
        Assert.IsTrue(XauthOutputParser.TryFindCookie(MutterOutput, out string? cookie));
        Assert.AreEqual("edbd9f27d0a3627f071c26e0fb4a39b0", cookie);
    }

    /// <summary>Xorg 会话的常见形态:display 号写全了。</summary>
    [TestMethod]
    public void FindsCookieWithExplicitDisplayNumber()
    {
        Assert.IsTrue(XauthOutputParser.TryFindCookie(
            "myhost/unix:0  MIT-MAGIC-COOKIE-1  0123456789abcdef0123456789ABCDEF\n", out string? cookie));
        Assert.AreEqual("0123456789abcdef0123456789ABCDEF", cookie);
    }

    /// <summary>CRLF 与前后空白都可能出现,不该因此漏读。</summary>
    [TestMethod]
    public void ToleratesCarriageReturnsAndPadding()
    {
        Assert.IsTrue(XauthOutputParser.TryFindCookie(
            "\r\n   myhost/unix:0\tMIT-MAGIC-COOKIE-1\t0123456789abcdef0123456789abcdef  \r\n", out string? cookie));
        Assert.AreEqual("0123456789abcdef0123456789abcdef", cookie);
    }

    /// <summary>只跳过读不了的那几行,后面真有一条 MIT cookie 时仍要找到它。</summary>
    [TestMethod]
    public void SkipsUnusableLinesAndKeepsLooking()
    {
        const string output =
            "myhost/unix:0  XDM-AUTHORIZATION-1  0123456789abcdef0123456789abcdef\n" +
            "myhost/unix:0  MIT-MAGIC-COOKIE-1  short\n" +
            "myhost/unix:0  MIT-MAGIC-COOKIE-1  zzzz56789abcdef0123456789abcdefg\n" +
            "myhost/unix:1  MIT-MAGIC-COOKIE-1  fedcba9876543210fedcba9876543210\n";

        Assert.IsTrue(XauthOutputParser.TryFindCookie(output, out string? cookie));
        Assert.AreEqual("fedcba9876543210fedcba9876543210", cookie);
    }

    [TestMethod]
    [DataRow("", DisplayName = "空输出(这个 display 没有条目)")]
    [DataRow("\n\n", DisplayName = "只有空行")]
    [DataRow("myhost/unix:0  MIT-MAGIC-COOKIE-1\n", DisplayName = "缺 cookie 那一段")]
    [DataRow("myhost/unix:0  XDM-AUTHORIZATION-1  0123456789abcdef0123456789abcdef\n",
             DisplayName = "只有别的认证协议 —— 库只支持 MIT-MAGIC-COOKIE-1")]
    [DataRow("myhost/unix:0  MIT-MAGIC-COOKIE-1  0123456789abcdef\n", DisplayName = "cookie 长度不对")]
    [DataRow("myhost/unix:0  MIT-MAGIC-COOKIE-1  0123456789abcdef0123456789abcdeg\n",
             DisplayName = "cookie 含非十六进制字符")]
    [DataRow("myhost/unix:0  mit-magic-cookie-1  0123456789abcdef0123456789abcdef\n",
             DisplayName = "协议名大小写不对 —— xauth 只会原样输出大写,小写说明不是它")]
    public void ReturnsNothingWhenThereIsNoUsableCookie(string output)
    {
        Assert.IsFalse(XauthOutputParser.TryFindCookie(output, out string? cookie));
        Assert.IsNull(cookie);
    }
}
