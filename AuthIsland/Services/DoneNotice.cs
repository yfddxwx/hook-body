namespace AuthIsland.Services;

/// <summary>
/// 一次"任务跑完了"的通知。由 Stop hook 推过来，
/// 点卡片时用 <see cref="SessionId"/> 拼 workbuddy://chat/&lt;id&gt; 直达会话。
/// </summary>
public sealed class DoneNotice
{
    public string SessionId { get; set; } = "";

    public string Summary { get; set; } = "";

    public string Cwd { get; set; } = "";

    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
}
