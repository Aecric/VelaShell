using System.Net;
using System.Net.Sockets;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.X11;

/// <summary>
/// <see cref="IX11DisplayService" /> 的 Linux 实现:读 <c>DISPLAY</c>、调 <c>xauth</c> 取 cookie、
/// 连一次本机 X Server 确认它真的在听。
/// <para>
/// 第一版只做 Linux(Xorg 与 Xwayland 都走同一条路 —— Wayland 会话里远端 X11 应用
/// 由 Xwayland 承接,对我们这层没有区别)。Windows 的 VcXsrv、macOS 的 XQuartz 在
/// <c>feature-plan.md</c> 的阶段二,这里如实返回
/// <see cref="X11DisplayStatus.PlatformUnsupported" />,不假装探测。
/// </para>
/// </summary>
public sealed class X11DisplayService : IX11DisplayService
{
    /// <summary>连本机 X Server 的等待上限:环回连接要么立刻成、要么立刻拒,给 2 秒足够宽。</summary>
    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<string?> _displayProvider;
    private readonly IXauthReader _xauth;
    private readonly Func<EndPoint, CancellationToken, Task<bool>> _reachable;
    private readonly bool _platformSupported;

    /// <summary>生产用构造:读进程环境的 <c>DISPLAY</c>,真跑 <c>xauth</c>,真连 X Server。</summary>
    public X11DisplayService()
        : this(static () => Environment.GetEnvironmentVariable("DISPLAY"),
               new XauthProcessReader(),
               ProbeReachableAsync,
               OperatingSystem.IsLinux())
    {
    }

    /// <summary>
    /// 测试用构造:三个外部依赖(环境变量、xauth、socket)全部可替换。
    /// <paramref name="platformSupported" /> 也要能注入 —— CI 是三平台矩阵,
    /// Linux 那条路径的用例在 Windows / macOS 的作业里也必须真跑,而不是跳过。
    /// </summary>
    internal X11DisplayService(
        Func<string?> displayProvider,
        IXauthReader xauth,
        Func<EndPoint, CancellationToken, Task<bool>> reachable,
        bool platformSupported)
    {
        _displayProvider = displayProvider;
        _xauth = xauth;
        _reachable = reachable;
        _platformSupported = platformSupported;
    }

    /// <inheritdoc />
    public async Task<X11DisplayProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_platformSupported)
        {
            return X11DisplayProbe.Failed(X11DisplayStatus.PlatformUnsupported);
        }

        string? display = _displayProvider();
        if (!X11DisplayName.TryParse(display, out X11DisplayName? name, out X11ParseFailure failure))
        {
            return X11DisplayProbe.Failed(failure switch
            {
                X11ParseFailure.Empty => X11DisplayStatus.NoDisplay,
                X11ParseFailure.NotLocal => X11DisplayStatus.DisplayNotLocal,
                _ => X11DisplayStatus.DisplayMalformed
            }, DescribeDisplay(display));
        }

        // cookie 先取:没有它就无法启用转发(库要求必须给出本机 cookie),
        // 那么连 X Server 通不通都不必问了。
        XauthLookup lookup = await _xauth.LookupAsync(display!.Trim(), cancellationToken).ConfigureAwait(false);
        if (lookup.Outcome != XauthOutcome.Ok || lookup.CookieHex is null)
        {
            return X11DisplayProbe.Failed(lookup.Outcome switch
            {
                XauthOutcome.Missing => X11DisplayStatus.XauthMissing,
                XauthOutcome.NoEntry => X11DisplayStatus.CookieMissing,
                _ => X11DisplayStatus.XauthFailed
            }, lookup.Detail ?? DescribeDisplay(display));
        }

        // 再确认 X Server 真在听。socket 文件存在不等于有人 accept,只有连一次才知道。
        foreach (EndPoint candidate in name!.CandidateEndPoints())
        {
            if (!await _reachable(candidate, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            return new X11DisplayProbe(
                X11DisplayStatus.Available,
                new X11ForwardingSettings
                {
                    LocalEndPoint = candidate,
                    Cookie = new X11AuthenticationCookie(lookup.CookieHex),
                    ScreenNumber = name.Screen,
                    DisplayName = display.Trim()
                });
        }
        return X11DisplayProbe.Failed(X11DisplayStatus.ServerUnreachable, DescribeDisplay(display));
    }

    /// <summary>诊断串:只放 <c>DISPLAY</c> 的值 —— 报障时第一个要问的就是它,而它不是凭据。</summary>
    private static string DescribeDisplay(string? display) =>
        string.IsNullOrWhiteSpace(display) ? "DISPLAY is not set." : $"DISPLAY={display.Trim()}";

    /// <summary>
    /// 连一次就断,不做 X11 握手 —— 我们只想知道「有人在这个 endpoint 上 accept 吗」。
    /// 半途放弃的连接对 X Server 是家常便饭(<c>xset q</c> 探活失败时就是这样)。
    /// </summary>
    private static async Task<bool> ProbeReachableAsync(EndPoint endPoint, CancellationToken cancellationToken)
    {
        ProtocolType protocol = endPoint.AddressFamily == AddressFamily.Unix
            ? ProtocolType.Unspecified
            : ProtocolType.Tcp;
        try
        {
            using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, protocol);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ReachabilityTimeout);
            await socket.ConnectAsync(endPoint, timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }
}
