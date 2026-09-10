using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>
/// X11 探测结论 → 用户看得懂的一句话。连接时的终端内提示与连接配置页的
/// 「测试本机 X Server」共用这一处 —— 同一个失败在两个地方措辞不同,用户会以为是两回事。
/// </summary>
internal static class X11NoticeText
{
    /// <summary>把探测结果说成一句完整的话(成功时报出 DISPLAY,失败时报原因)。</summary>
    internal static string Describe(X11DisplayProbe probe) =>
        probe.IsAvailable
            ? Strings.Format("Msg_X11Ready", probe.Settings!.DisplayName)
            : Strings.Format("Msg_X11Unavailable", Reason(probe.Status));

    /// <summary>
    /// 失败原因的短语,填进 <c>Msg_X11Unavailable</c> 的占位符。
    /// <see cref="X11DisplayStatus.Available" /> 不该走到这里,兜底也给一句能看的话。
    /// </summary>
    private static string Reason(X11DisplayStatus status) => Strings.Get(status switch
    {
        X11DisplayStatus.PlatformUnsupported => "Msg_X11StatusPlatformUnsupported",
        X11DisplayStatus.NoDisplay => "Msg_X11StatusNoDisplay",
        X11DisplayStatus.DisplayNotLocal => "Msg_X11StatusDisplayNotLocal",
        X11DisplayStatus.DisplayMalformed => "Msg_X11StatusDisplayMalformed",
        X11DisplayStatus.XauthMissing => "Msg_X11StatusXauthMissing",
        X11DisplayStatus.XauthFailed => "Msg_X11StatusXauthFailed",
        X11DisplayStatus.CookieMissing => "Msg_X11StatusCookieMissing",
        _ => "Msg_X11StatusServerUnreachable"
    });
}
