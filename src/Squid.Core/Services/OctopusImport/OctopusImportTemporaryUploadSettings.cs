namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportTemporaryUploadSettings : IScopedDependency
{
    string RootPath { get; }

    TimeSpan DefaultRetentionPeriod { get; }

    TimeSpan FailedRetentionPeriod { get; }

    TimeSpan InterruptedImportGracePeriod { get; }

    /// <summary>The absolute upper bound on a session's life, counted from its creation; sliding retention never extends a session past it.</summary>
    TimeSpan MaxSessionAge { get; }

    int CleanupBatchSize { get; }

    int SecureDeleteBufferBytes { get; }
}

public sealed class OctopusImportTemporaryUploadSettings : IOctopusImportTemporaryUploadSettings
{
    public const string MaxSessionAgeHoursConfigKey = "OctopusImport:TemporaryUploads:MaxSessionAgeHours";
    public const string MaxSessionAgeHoursEnvVar = "OctopusImport__TemporaryUploads__MaxSessionAgeHours";

    private const int DefaultRetentionHours = 24;
    private const int DefaultFailedRetentionHours = 24;
    private const int DefaultInterruptedImportGraceHours = 6;
    private const int DefaultMaxSessionAgeHours = 168;
    private const int DefaultCleanupBatchSize = 100;
    private const int DefaultSecureDeleteBufferBytes = 81920;

    public OctopusImportTemporaryUploadSettings(IConfiguration configuration)
    {
        RootPath = configuration.GetValue<string>("OctopusImport:TemporaryUploads:RootPath")
                   ?? Path.Combine(Path.GetTempPath(), "squid-octopus-import-uploads");

        DefaultRetentionPeriod = PositiveHours(
            configuration.GetValue<double?>("OctopusImport:TemporaryUploads:DefaultRetentionHours"),
            DefaultRetentionHours);
        FailedRetentionPeriod = NonNegativeHours(
            configuration.GetValue<double?>("OctopusImport:TemporaryUploads:FailedRetentionHours"),
            DefaultFailedRetentionHours);
        InterruptedImportGracePeriod = NonNegativeHours(
            configuration.GetValue<double?>("OctopusImport:TemporaryUploads:InterruptedImportGraceHours"),
            DefaultInterruptedImportGraceHours);
        MaxSessionAge = PositiveHours(configuration.GetValue<double?>(MaxSessionAgeHoursConfigKey), DefaultMaxSessionAgeHours);
        CleanupBatchSize = Math.Clamp(
            configuration.GetValue<int?>("OctopusImport:TemporaryUploads:CleanupBatchSize") ?? DefaultCleanupBatchSize,
            1,
            1000);
        SecureDeleteBufferBytes = Math.Clamp(
            configuration.GetValue<int?>("OctopusImport:TemporaryUploads:SecureDeleteBufferBytes") ?? DefaultSecureDeleteBufferBytes,
            4096,
            1024 * 1024);
    }

    public string RootPath { get; }

    public TimeSpan DefaultRetentionPeriod { get; }

    public TimeSpan FailedRetentionPeriod { get; }

    public TimeSpan InterruptedImportGracePeriod { get; }

    public TimeSpan MaxSessionAge { get; }

    public int CleanupBatchSize { get; }

    public int SecureDeleteBufferBytes { get; }

    private static TimeSpan PositiveHours(double? configured, int fallback)
    {
        var hours = configured.GetValueOrDefault(fallback);
        return TimeSpan.FromHours(hours > 0 ? hours : fallback);
    }

    private static TimeSpan NonNegativeHours(double? configured, int fallback)
    {
        var hours = configured.GetValueOrDefault(fallback);
        return TimeSpan.FromHours(hours >= 0 ? hours : fallback);
    }
}
