namespace AgentIsland.Core;

/// 一次模型请求的用量和耗时，各家日志统一成这个形状。只有数字和时间，不含任何对话内容。
///
/// InputTotal 是这次请求读进去的全部输入 token（命中的和没命中的），CacheHit 是其中命中缓存的部分，
/// 各家的换算不同：Claude 是 input + cache_read + cache_creation，Codex 的 input 已含缓存。
/// Start 为 null：日志里找不到这次请求是什么时候发出的，只用它算缓存率，不算速度。
/// Complete：这次请求的输出已经写完（Claude 的 stop_reason、Codex 和 Kimi 的记录本身就是写完才有）。
public sealed record RequestSample(
    string Key, DateTimeOffset? Start, DateTimeOffset End, long CacheHit, long InputTotal, long Output, bool HasCache, bool Complete);

/// 一个会话此刻的缓存率和输出速度（设计稿 §4）。数字最多每 5 秒重算一次，显示时再按“现在”判断要不要藏。
/// CachePercent 是 0–100 的整数，向下取整；Speed 是 tok/s。
public sealed record SessionMetrics(int? CachePercent, double? Speed, int Requests, DateTimeOffset? LastOutputAt)
{
    /// 缓存率取最近几次请求（用户 11/26 定：5 次）。
    public const int CacheWindow = 5;

    /// 速度取最近几次够长的回复（用户 11/26 定：3 次，输出 ≥ 50 token）。
    public const int SpeedWindow = 3;
    public const long SpeedMinOutput = 50;

    /// 有用量记录的请求少于这个数，两项都不显示（§4.3 数据不足）。
    public const int MinRequests = 2;

    /// 最近这么久没有新的输出，速度不显示，缓存率照常（§4.3）。
    public static readonly TimeSpan SpeedFreshFor = TimeSpan.FromSeconds(30);

    /// 速度低于这个值不显示（§4.3）。
    public const double MinSpeed = 1;

    /// 从最近的请求（按发生的先后排好）算出读数。缓存率是命中之和除以输入之和，不是每次的平均：
    /// 一次几十 token 的小请求不该和十几万 token 的大请求同权。日志里没有缓存字段的请求不参与，
    /// 一个都没有就没有缓存率。速度是每次回复各自的“输出 ÷ 耗时”再取平均，耗时不是正数的丢掉。
    public static SessionMetrics Compute(IReadOnlyList<RequestSample> samples, DateTimeOffset? lastOutputAt)
    {
        int? cache = null;
        var cached = samples.Where(sample => sample.HasCache).TakeLast(CacheWindow).ToList();
        var total = cached.Sum(sample => sample.InputTotal);
        if (cached.Count > 0 && total > 0)
        {
            // 整数除法就是向下取整：99.6% 写 99，全部命中才写 100。
            cache = (int)Math.Clamp(cached.Sum(sample => Math.Min(sample.CacheHit, sample.InputTotal)) * 100 / total, 0, 100);
        }
        double? speed = null;
        var speeds = samples
            .Where(sample => sample.Complete && sample.Output >= SpeedMinOutput && sample.Start is not null
                && sample.End > sample.Start)
            .TakeLast(SpeedWindow)
            .Select(sample => sample.Output / (sample.End - sample.Start!.Value).TotalSeconds)
            .ToList();
        if (speeds.Count > 0) speed = speeds.Average();
        return new SessionMetrics(cache, speed, samples.Count, lastOutputAt);
    }

    /// 按 §4.3 能显示的部分；两项都不显示就返回 null（整组隐藏，不写占位）。
    /// 主对话在等你、出错、空闲时不显示，那是调用方按状态判断的。
    public SessionMetrics? Shown(DateTimeOffset now)
    {
        if (Requests < MinRequests) return null;
        var speed = Speed is { } s && s >= MinSpeed && LastOutputAt is { } at && now - at <= SpeedFreshFor ? s : (double?)null;
        return CachePercent is null && speed is null ? null : this with { Speed = speed };
    }
}
