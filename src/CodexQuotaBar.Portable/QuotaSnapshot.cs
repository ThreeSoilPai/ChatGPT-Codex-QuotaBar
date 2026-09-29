namespace CodexQuotaBar;

internal sealed record QuotaSnapshot(
    double UsedPercent,
    int WindowDurationMinutes,
    DateTimeOffset? ResetsAt,
    string? PlanType)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);

    public string WindowLabel => WindowDurationMinutes switch
    {
        10080 => "周额度",
        1440 => "日额度",
        300 => "5 小时额度",
        60 => "小时额度",
        > 0 when WindowDurationMinutes % 1440 == 0 => $"{WindowDurationMinutes / 1440} 天额度",
        > 0 when WindowDurationMinutes % 60 == 0 => $"{WindowDurationMinutes / 60} 小时额度",
        > 0 => $"{WindowDurationMinutes} 分钟额度",
        _ => "额度"
    };

    public string CompactLabel => $"{WindowLabel} · 剩余 {RemainingPercent:0}%";

    public string AccessibleDescription => ResetsAt is null
        ? CompactLabel
        : $"{CompactLabel}，将在 {ResetsAt.Value.LocalDateTime:M月d日 HH:mm} 重置";
}
