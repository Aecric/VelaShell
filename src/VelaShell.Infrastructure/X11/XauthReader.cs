using System.ComponentModel;
using System.Diagnostics;

namespace VelaShell.Infrastructure.X11;

/// <summary>一次 <c>xauth</c> 查询的结论。</summary>
internal enum XauthOutcome
{
    /// <summary>查到了 MIT-MAGIC-COOKIE-1。</summary>
    Ok,

    /// <summary>系统里没有 <c>xauth</c> 这个可执行文件。</summary>
    Missing,

    /// <summary><c>xauth</c> 跑通了,但这个 display 没有 MIT-MAGIC-COOKIE-1 条目。</summary>
    NoEntry,

    /// <summary><c>xauth</c> 非零退出、超时,或输出解析不出来。</summary>
    Failed
}

/// <summary>
/// <c>xauth</c> 查询结果。
/// </summary>
/// <param name="Outcome">结论。</param>
/// <param name="CookieHex">32 位十六进制 cookie;仅 <see cref="XauthOutcome.Ok" /> 时非 null。</param>
/// <param name="Detail">诊断细节(stderr / 超时说明)。**不含 stdout** —— 那里面是 cookie。</param>
internal readonly record struct XauthLookup(XauthOutcome Outcome, string? CookieHex, string? Detail);

/// <summary>本机 cookie 的读取契约;抽出来是为了让 <see cref="X11DisplayService" /> 的用例不必真跑 xauth。</summary>
internal interface IXauthReader
{
    /// <summary>查某个 display 的 MIT-MAGIC-COOKIE-1。</summary>
    Task<XauthLookup> LookupAsync(string displayName, CancellationToken cancellationToken);
}

/// <summary>
/// 调 <c>xauth</c> 命令行取本机 cookie。
/// <para>
/// 为什么不自己解析 <c>XAUTHORITY</c> 二进制:那个格式里 display 号为空的条目是**通配**
/// (GNOME/mutter 给 Xwayland 写的就是这种,本机实测 <c>xauth nlist</c> 的 number 段长度为 0),
/// 通配、<c>FamilyWild</c>、主机名归一这几条匹配规则都在 xauth 里,自己实现一份就是自己维护
/// 一份会漂的副本。<c>ssh -X</c> 走的也正是 <c>xauth list</c>。
/// </para>
/// </summary>
internal sealed class XauthProcessReader(TimeSpan? timeout = null) : IXauthReader
{
    /// <summary>xauth 的等待上限。authority 文件在网络 home 上、或被别的进程加着锁时会拖住。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const string ExecutableName = "xauth";

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <inheritdoc />
    public async Task<XauthLookup> LookupAsync(string displayName, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ExecutableName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // -n:不做地址的反向解析。authority 文件里有 FamilyInternet 条目而 DNS 又不通时,
        //    xauth 会卡在解析上 —— 我们只要 cookie,不需要它把地址显示成主机名。
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("list");
        // DISPLAY 原值直接透传(含 .screen 也没关系,xauth 自己按 display 归一)——
        // 少一层我们自己的重建,就少一处和 xauth 语义漂移的机会。
        startInfo.ArgumentList.Add(displayName);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return new(XauthOutcome.Failed, null, "xauth did not start.");
            }

            // stdout 与 stderr 同时读干:只等一条、另一条写满管道缓冲区就是个死锁。
            //
            // 这两个读**刻意不带取消令牌**,超时只作用在 WaitForExitAsync 上:超时的处置是
            // 杀进程,进程一死管道就到结尾,两个读随之自然完成 —— 于是无论走哪条路都能把它们
            // 等回来。传了令牌反而是两处麻烦:读会以 OperationCanceledException 收场,而那两个
            // Task 在超时分支里没人 await,就成了未观察异常(仓库里已经吃过一次这种苦头,
            // 见 SshConnectionService.ConnectAsync 那段关于 Task.Run 的注释)。
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时,或调用方撤销。两种情况都别把 xauth 留在后台:它可能正卡在
                // authority 文件的锁上,而没人会再来收它。
                KillQuietly(process);
                await DrainAsync(stdout, stderr).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                return new(XauthOutcome.Failed, null, $"xauth timed out after {_timeout.TotalSeconds:0.#}s.");
            }
            string output = await stdout.ConfigureAwait(false);
            string errors = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return new(XauthOutcome.Failed, null, Describe(errors));
            }
            return XauthOutputParser.TryFindCookie(output, out string? hex)
                ? new(XauthOutcome.Ok, hex, null)
                : new(XauthOutcome.NoEntry, null, null);
        }
        catch (Win32Exception)
        {
            // ENOENT:精简发行版 / 容器里常见没装 xauth。
            return new(XauthOutcome.Missing, null, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            KillQuietly(process);
            return new(XauthOutcome.Failed, null, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// 把两条已经没人要的流等回来,只为了「观察」它们 —— 结果与异常一并丢掉。
    /// 不等的话它们就是两个未观察异常的 Task;而这里等得起:进程已经被杀,管道立刻到结尾。
    /// </summary>
    private static async Task DrainAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // 进程被杀之后读端怎么收场都无所谓,这里只是把异常观察掉。
        }
    }

    /// <summary>stderr 掐短再进诊断:xauth 的报错是一行,但没必要为一个坏 display 名字灌满日志。</summary>
    private static string? Describe(string errors)
    {
        string trimmed = errors.Trim();
        if (trimmed.Length == 0)
        {
            return "xauth exited with a nonzero status.";
        }
        return trimmed.Length <= 300 ? trimmed : trimmed[..300];
    }

    private static void KillQuietly(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 已经自己退了 / 没权限收尾。查不到 cookie 这个结论不受影响。
        }
    }
}

/// <summary><c>xauth list</c> 输出的解析。单独拿出来:它是纯函数,用例直接喂文本。</summary>
internal static class XauthOutputParser
{
    /// <summary>MIT-MAGIC-COOKIE-1 是唯一被 X11 转发支持的认证协议(库那边也只实现了它)。</summary>
    internal const string MagicCookieProtocol = "MIT-MAGIC-COOKIE-1";

    /// <summary>
    /// 从 <c>xauth list</c> 的输出里取第一条 MIT-MAGIC-COOKIE-1 的 cookie。
    /// 每行形如 <c>&lt;display&gt;  &lt;协议&gt;  &lt;十六进制&gt;</c>;
    /// 按**末尾两段**取而不是按第 2、3 段,这样 display 名里出现空格也不至于错位。
    /// </summary>
    internal static bool TryFindCookie(string output, out string? cookieHex)
    {
        cookieHex = null;
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 3)
            {
                continue;
            }
            if (!fields[^2].Equals(MagicCookieProtocol, StringComparison.Ordinal))
            {
                continue;
            }
            string candidate = fields[^1];
            if (candidate.Length != 32 || !candidate.All(Uri.IsHexDigit))
            {
                continue;
            }
            cookieHex = candidate;
            return true;
        }
        return false;
    }
}
