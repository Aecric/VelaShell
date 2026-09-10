using System.Net;
using System.Net.Sockets;

namespace VelaShell.Infrastructure.X11;

/// <summary>
/// 解析后的 <c>DISPLAY</c>:主机部分、display 号、screen 号。
/// </summary>
/// <remarks>
/// <c>DISPLAY</c> 的形态(X.Org 的 <c>Xserver(1)</c> 与 <c>X(7)</c>)是
/// <c>[主机][/unix]:display[.screen]</c>,实际见得到的有:
/// <c>:0</c>、<c>:0.1</c>、<c>unix:0</c>、<c>hostname/unix:0</c>、<c>localhost:10.0</c>
/// (最后一种是**本机正跑在别人的 ssh -X 里**)、<c>1.2.3.4:0</c>(远程 X Server)。
/// </remarks>
/// <param name="Host">主机部分的原文(可能为空串 = 本机 Unix socket)。</param>
/// <param name="Display">display 号。</param>
/// <param name="Screen">screen 号,缺省 0。</param>
/// <param name="IsUnixSocket">true = 走本机 Unix socket(空主机 / <c>unix</c> / <c>主机/unix</c>)。</param>
internal sealed record X11DisplayName(string Host, int Display, int Screen, bool IsUnixSocket)
{
    /// <summary>X11 的 Unix socket 目录(Linux 上固定是这里)。</summary>
    internal const string UnixSocketDirectory = "/tmp/.X11-unix";

    /// <summary>display 0 对应的 TCP 端口;display N = 6000 + N。</summary>
    private const int TcpPortBase = 6000;

    /// <summary>
    /// 解析 <c>DISPLAY</c>。只认本机(Unix socket 或环回地址)—— 远程 X Server 交给
    /// <see cref="X11ParseFailure.NotLocal" />,由上层翻译成「第一版只支持本机 display」。
    /// </summary>
    internal static bool TryParse(string? value, out X11DisplayName? name, out X11ParseFailure failure)
    {
        name = null;
        failure = X11ParseFailure.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            failure = X11ParseFailure.Empty;
            return false;
        }

        string display = value.Trim();
        // 从**最后**一个冒号切:IPv6 字面量(如 [::1]:0)的地址里也有冒号。
        int colon = display.LastIndexOf(':');
        if (colon < 0)
        {
            failure = X11ParseFailure.Malformed;
            return false;
        }

        string host = display[..colon];
        string numbers = display[(colon + 1)..];
        if (numbers.Length == 0)
        {
            failure = X11ParseFailure.Malformed;
            return false;
        }

        int screen = 0;
        int dot = numbers.IndexOf('.');
        if (dot >= 0)
        {
            if (!int.TryParse(numbers[(dot + 1)..], out screen) || screen < 0)
            {
                failure = X11ParseFailure.Malformed;
                return false;
            }
            numbers = numbers[..dot];
        }
        if (!int.TryParse(numbers, out int displayNumber) || displayNumber < 0)
        {
            failure = X11ParseFailure.Malformed;
            return false;
        }
        // display 号要能换算成一个合法端口,否则连回落路径都构造不出来。
        if (TcpPortBase + (long)displayNumber > IPEndPoint.MaxPort)
        {
            failure = X11ParseFailure.Malformed;
            return false;
        }

        // 「主机/unix」与「unix」都表示本机 socket;空主机也是(最常见的 :0 就走这条)。
        bool unixSocket = host.Length == 0
            || host.Equals("unix", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("/unix", StringComparison.OrdinalIgnoreCase);

        if (!unixSocket && !IsLoopbackHost(host))
        {
            failure = X11ParseFailure.NotLocal;
            return false;
        }

        name = new X11DisplayName(host, displayNumber, screen, unixSocket);
        return true;
    }

    /// <summary>
    /// 候选 endpoint,按尝试顺序。Unix socket 型给两个:socket 文件优先,
    /// 连不上再试环回 TCP —— <c>-nolisten local</c> 这类配置下只有后者活着,
    /// 而 <c>ssh -X</c> 也是两个都试。
    /// </summary>
    internal IReadOnlyList<EndPoint> CandidateEndPoints()
    {
        IPEndPoint tcp = new(IPAddress.Loopback, TcpPortBase + Display);
        if (!IsUnixSocket)
        {
            // 主机部分写成 ::1 时用 IPv6 环回,其余(localhost / 127.0.0.1)用 IPv4。
            return [IsIPv6Loopback(Host) ? new IPEndPoint(IPAddress.IPv6Loopback, tcp.Port) : tcp];
        }
        return
        [
            new UnixDomainSocketEndPoint(Path.Combine(UnixSocketDirectory, $"X{Display}")),
            tcp
        ];
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(bare, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private static bool IsIPv6Loopback(string host)
    {
        string bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(bare, out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetworkV6;
    }
}

/// <summary><c>DISPLAY</c> 解析失败的原因。</summary>
internal enum X11ParseFailure
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary><c>DISPLAY</c> 未设置或全是空白。</summary>
    Empty,

    /// <summary>格式不认识。</summary>
    Malformed,

    /// <summary>指向的不是本机。</summary>
    NotLocal
}
