using System.Net;

namespace VelaShell.Core.Ssh;

/// <summary>本机 X11 display 的探测结果。除 <see cref="Available" /> 外都不能开转发。</summary>
public enum X11DisplayStatus
{
    /// <summary>探测成功:display、cookie、endpoint 都拿到了,且本机 X Server 连得上。</summary>
    Available,

    /// <summary>当前平台第一版不支持(只做了 Linux)。</summary>
    PlatformUnsupported,

    /// <summary>没有 <c>DISPLAY</c> 环境变量 —— 纯 Wayland 会话且没起 Xwayland,或根本没有图形会话。</summary>
    NoDisplay,

    /// <summary><c>DISPLAY</c> 指向的不是本机(远程 X Server)。第一版只支持本机 display。</summary>
    DisplayNotLocal,

    /// <summary><c>DISPLAY</c> 的格式不认识。</summary>
    DisplayMalformed,

    /// <summary>找不到 <c>xauth</c> 可执行文件 —— 取不到 cookie,而 cookie 是必需的。</summary>
    XauthMissing,

    /// <summary><c>xauth</c> 跑失败或超时。</summary>
    XauthFailed,

    /// <summary><c>xauth</c> 里没有这个 display 的 MIT-MAGIC-COOKIE-1 条目。</summary>
    CookieMissing,

    /// <summary>本机 X Server 连不上(socket 不存在 / 端口拒绝连接)。</summary>
    ServerUnreachable
}

/// <summary>
/// 本机 X Server 的 MIT-MAGIC-COOKIE-1。**它是凭据**:拿到它就能连本机 X display,
/// 也就能读键盘输入和整块屏幕内容。
/// <para>
/// 包成一个类型而不是裸 <see cref="string" />,图的就是 <see cref="ToString" /> ——
/// 日志、录制、异常消息、调试器的对象视图里出现的都是一个固定的遮罩串,
/// 想拿到明文必须显式调 <see cref="Reveal" />(全仓只有 Infrastructure 的 SSH 包装
/// 那一处调它)。裸字符串迟早会被谁顺手插进一条 <c>LogDebug</c>。
/// </para>
/// </summary>
public sealed class X11AuthenticationCookie
{
    /// <summary>日志里代替 cookie 出现的固定遮罩串(不本地化:它要能被日志检索直接搜到)。</summary>
    public const string Redacted = "MIT-MAGIC-COOKIE-1(redacted)";

    private readonly string _hex;

    /// <summary>用 32 位十六进制(16 字节)的 cookie 构造。</summary>
    /// <param name="hex">32 个十六进制数字,大小写均可。</param>
    /// <exception cref="ArgumentException">长度不是 32,或含非十六进制字符。</exception>
    public X11AuthenticationCookie(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit))
        {
            // 异常消息里不带 hex 本身 —— 它可能是一个**正确**的 cookie,只是多了个换行。
            throw new ArgumentException("Expected 32 hexadecimal digits (a 16-byte MIT-MAGIC-COOKIE-1).", nameof(hex));
        }
        _hex = hex;
    }

    /// <summary>取出 cookie 明文。调用点只应有一处:映射到 SSH 库的 X11 转发选项时。</summary>
    public string Reveal() => _hex;

    /// <summary>恒为 <see cref="Redacted" /> —— 见类型注释。</summary>
    public override string ToString() => Redacted;
}

/// <summary>
/// 一条已解析完成的 X11 转发目标:往哪个本机 endpoint 送、用哪个 cookie、哪块 screen。
/// <para>
/// **只在「能转发」时存在**,所以这里没有「是否启用」字段 —— 启用状态在
/// <c>SessionProfile.EnableX11Forwarding</c>(那一份跟着连接配置走、会同步),
/// 而本对象是每次建连现探测出来的运行时产物:cookie 会随 X Server 重启而变,
/// 落盘或同步它既没意义也不安全。
/// </para>
/// </summary>
public sealed class X11ForwardingSettings
{
    /// <summary>本机 X Server 的 endpoint:Unix domain socket,或环回 TCP。</summary>
    public required EndPoint LocalEndPoint { get; init; }

    /// <summary>本机 X Server 的 cookie。</summary>
    public required X11AuthenticationCookie Cookie { get; init; }

    /// <summary>X11 screen 号,通常是 0。</summary>
    public int ScreenNumber { get; init; }

    /// <summary>
    /// display 的来源,即 <c>DISPLAY</c> 的原值(如 <c>:0</c>)。
    /// 可以进日志与界面 —— 它不是凭据,而且报障时第一个要问的就是它。
    /// </summary>
    public required string DisplayName { get; init; }
}

/// <summary>一条 shell 上 X11 转发的最终去向。</summary>
public enum X11ForwardingOutcome
{
    /// <summary>还没试(请求刚建好,shell 尚未打开)。</summary>
    Pending,

    /// <summary>服务器接受了 <c>x11-req</c>,转发已就绪。</summary>
    Enabled,

    /// <summary>
    /// 服务器拒绝了 <c>x11-req</c> —— <c>sshd_config</c> 里 <c>X11Forwarding no</c>,
    /// 或者服务器上没有可用的 <c>xauth</c>。**普通终端照常可用**(见
    /// <c>TmdsSshClientWrapper.OpenShellAsync</c> 的不带 X11 重开)。
    /// </summary>
    RejectedByServer
}

/// <summary>
/// 开一条 shell 时的 X11 转发请求。**既是入参也是回执** —— 调用方建好它传进
/// <see cref="ISshClientWrapper.CreateShellStreamAsync" />,回来读 <see cref="Outcome" />
/// 就知道服务器认没认。
/// <para>
/// 为什么不做成返回值的一部分:<see cref="IShellStreamWrapper" /> 有三个实现
/// (SSH、ConPTY、插件协议),给接口加一个只有 SSH 那份答得上来的成员,
/// 等于让另外两个实现替一件与自己无关的事签字。
/// </para>
/// </summary>
/// <param name="settings">已探测好的本机 display 与 cookie。</param>
public sealed class X11ForwardingRequest(X11ForwardingSettings settings)
{
    /// <summary>要转发到的本机 display。</summary>
    public X11ForwardingSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>请求的去向;shell 打开之后才有结论。</summary>
    public X11ForwardingOutcome Outcome { get; private set; } = X11ForwardingOutcome.Pending;

    /// <summary>被拒时服务器/库给出的说明(不含 cookie),可进日志与界面。</summary>
    public string? Detail { get; private set; }

    /// <summary>由 SSH 实现调用:服务器接受了转发。</summary>
    public void MarkEnabled()
    {
        Outcome = X11ForwardingOutcome.Enabled;
        Detail = null;
    }

    /// <summary>由 SSH 实现调用:服务器拒绝了转发,shell 已按不带 X11 重开。</summary>
    public void MarkRejectedByServer(string? detail)
    {
        Outcome = X11ForwardingOutcome.RejectedByServer;
        Detail = detail;
    }
}

/// <summary>
/// 一次本机 display 探测的结果。<see cref="Status" /> 是 <see cref="X11DisplayStatus.Available" />
/// 时 <see cref="Settings" /> 非 null,其余情况反之。
/// </summary>
/// <param name="Status">探测结论。</param>
/// <param name="Settings">解析出的转发目标;失败时为 null。</param>
/// <param name="Detail">
/// 诊断细节,可进日志与「测试本机 X Server」的结果里:失败原因的具体值(如 <c>DISPLAY</c> 的内容、
/// <c>xauth</c> 的 stderr)。**永远不含 cookie** —— <c>xauth</c> 的 stdout 一个字节都不进这里。
/// 界面上给用户看的那句话按 <see cref="Status" /> 本地化,这里只是补充。
/// </param>
public sealed record X11DisplayProbe(
    X11DisplayStatus Status,
    X11ForwardingSettings? Settings,
    string? Detail = null)
{
    /// <summary>探测是否成功。</summary>
    public bool IsAvailable => Status == X11DisplayStatus.Available && Settings is not null;

    /// <summary>构造一个失败结果。</summary>
    public static X11DisplayProbe Failed(X11DisplayStatus status, string? detail = null) =>
        new(status, null, detail);
}

/// <summary>
/// 本机 X11 display 的探测契约:读当前用户的图形会话,解析出「往哪送 + 用什么 cookie」。
/// <para>
/// 平台细节(<c>DISPLAY</c> 怎么解析、<c>xauth</c> 怎么调、socket 在哪)全在 Infrastructure 的实现里;
/// 这个契约只返回已解析的 endpoint 与包好的 cookie,不向上暴露任何本机路径。
/// </para>
/// </summary>
public interface IX11DisplayService
{
    /// <summary>
    /// 探测本机 display。含一次真实的连接尝试 —— 「X Server 连不上」要在建连时就说清楚,
    /// 而不是等用户敲了 <c>xclock</c> 再收到一句看不懂的远端报错。
    /// </summary>
    Task<X11DisplayProbe> ProbeAsync(CancellationToken cancellationToken = default);
}
