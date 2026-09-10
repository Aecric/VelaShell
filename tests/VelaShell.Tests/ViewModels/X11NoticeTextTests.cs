using System.Globalization;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// X11 探测结论 → 用户看到的那句话。
/// </summary>
/// <remarks>
/// 这里守的是「新加一个 <see cref="X11DisplayStatus" /> 却忘了配文案」。
/// 忘掉的后果不是编译错误也不是异常:<c>Strings.Get</c> 取不到键时**回退成键名本身**,
/// 于是用户在终端里读到的是一行 <c>Msg_X11StatusSomething</c>。
/// 判据就用这个回退行为 —— 文案等于键名即视为没配。
/// </remarks>
[TestClass]
[TestCategory("X11")]
public sealed class X11NoticeTextTests
{
    [TestInitialize]
    public void UseEnglish() => CultureInfo.CurrentUICulture = new("en");

    /// <summary>每一个失败状态都要有一句能读的解释,一个都不能漏。</summary>
    [TestMethod]
    public void EveryFailureStatusHasRealText()
    {
        List<string> missing = [];
        foreach (X11DisplayStatus status in Enum.GetValues<X11DisplayStatus>())
        {
            if (status == X11DisplayStatus.Available)
            {
                continue;
            }
            string text = X11NoticeText.Describe(X11DisplayProbe.Failed(status));
            // 回退成键名时整句话里会带着 Msg_X11Status 这个前缀 —— 那就是"没配"。
            if (text.Contains("Msg_X11", StringComparison.Ordinal))
            {
                missing.Add($"  {status} → {text}");
            }
        }

        Assert.IsEmpty(missing,
                       "以下 X11 状态没有对应文案(五份 resx 都要加),用户会直接读到资源键名:\n" +
                       string.Join("\n", missing));
    }

    /// <summary>成功那句要报出 DISPLAY —— 报障时第一个要问的就是它。</summary>
    [TestMethod]
    public void ReadyTextNamesTheDisplay()
    {
        X11DisplayProbe probe = new(
            X11DisplayStatus.Available,
            new X11ForwardingSettings
            {
                LocalEndPoint = new System.Net.Sockets.UnixDomainSocketEndPoint("/tmp/.X11-unix/X0"),
                Cookie = new X11AuthenticationCookie("0123456789abcdef0123456789abcdef"),
                DisplayName = ":0"
            });

        string text = X11NoticeText.Describe(probe);

        Assert.Contains(":0", text);
        Assert.DoesNotContain("Msg_X11", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 提示里绝不能出现 cookie。这句话会进终端 —— 而终端的输出会被会话日志与录制
    /// 原样落盘(<c>SshTerminalBridge.DataReceived</c> 那条旁路),漏一次就是把凭据写进了文件。
    /// </summary>
    [TestMethod]
    public void NoticeNeverLeaksTheCookie()
    {
        const string cookie = "0123456789abcdef0123456789abcdef";
        X11DisplayProbe probe = new(
            X11DisplayStatus.Available,
            new X11ForwardingSettings
            {
                LocalEndPoint = new System.Net.Sockets.UnixDomainSocketEndPoint("/tmp/.X11-unix/X0"),
                Cookie = new X11AuthenticationCookie(cookie),
                DisplayName = ":0"
            });

        Assert.DoesNotContain(cookie, X11NoticeText.Describe(probe));
    }
}
