using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Configuration;

/// <summary>
/// Sprint 7 Phase B — config cho DLP pipeline. Default disabled để không
/// phá existing flow khi deploy; bật qua appsettings per-environment.
/// </summary>
public sealed class DlpOptions
{
    public const string SectionName = "Dlp";

    /// <summary>
    /// Feature flag — khi false, ScrubbingAgentClient không intercept
    /// (passthrough như AgentClient gốc). Default false để safe rollout.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// TTL vault reverse mapping. Mặc định 10 phút — đủ cho LLM roundtrip
    /// chậm nhất mà không giữ PII trong memory quá lâu.
    /// </summary>
    [Range(1, 60)]
    public int VaultTtlMinutes { get; init; } = 10;
}
