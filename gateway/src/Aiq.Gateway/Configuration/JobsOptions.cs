using System.ComponentModel.DataAnnotations;

namespace Aiq.Gateway.Configuration;

public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Directory used to stage uploaded PDFs while a Hangfire worker picks them up.</summary>
    [Required]
    public string UploadStagingDirectory { get; init; } = "/var/aiq/uploads";

    /// <summary>Number of Hangfire worker threads in this process.</summary>
    [Range(1, 64)]
    public int WorkerCount { get; init; } = 4;

    /// <summary>Path prefix of the Hangfire dashboard. Set to empty to disable.</summary>
    public string DashboardPath { get; init; } = "/jobs/dashboard";
}
