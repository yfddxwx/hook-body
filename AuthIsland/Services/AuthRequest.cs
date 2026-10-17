using System.Text.Json.Serialization;

namespace AuthIsland.Services;

/// <summary>hook 侧发来的一次授权请求。</summary>
public sealed class AuthRequest
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Risk { get; set; } = "normal";

    /// <summary>闸门给的一句话：为什么这次值得看一眼（如「写工作区外文件」）。</summary>
    public string Reason { get; set; } = "";

    public int TimeoutMs { get; set; } = 60_000;

    [JsonIgnore]
    public DateTime Deadline { get; set; }

    [JsonIgnore]
    public TaskCompletionSource<AuthDecision> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class AuthDecision
{
    public string Decision { get; set; } = "pass";
    public string Reason { get; set; } = "";
}
