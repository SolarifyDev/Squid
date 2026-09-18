namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportTemporaryUploadSettings : IScopedDependency
{
    string RootPath { get; }

    TimeSpan DefaultRetentionPeriod { get; }

    TimeSpan FailedRetentionPeriod { get; }

    TimeSpan InterruptedImportGracePeriod { get; }

    int CleanupBatchSize { get; }

    int SecureDeleteBufferBytes { get; }
}

public sealed class OctopusImportTemporaryUploadSettings : IOctopusImportTemporaryUploadSettings
{
    private const int DefaultRetentionHours = 24;
    private const int DefaultFailedRetentionHours = 24;
    private const int DefaultInterruptedImportGraceHours = 6;
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
