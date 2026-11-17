using Microsoft.Extensions.Configuration;
using Squid.Core.Services.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

[Collection("OctopusImportTemporaryUploadSettingsEnv")]
public class OctopusImportTemporaryUploadSettingsTests
{
    // Renaming either constant breaks every operator who pinned the session age cap in configuration or the environment.
    [Fact]
    public void MaxSessionAgeHoursEnvVar_ConstantNamePinned()
    {
        OctopusImportTemporaryUploadSettings.MaxSessionAgeHoursEnvVar.ShouldBe("OctopusImport__TemporaryUploads__MaxSessionAgeHours");
    }

    [Fact]
    public void MaxSessionAgeHoursConfigKey_ConstantNamePinned()
    {
        OctopusImportTemporaryUploadSettings.MaxSessionAgeHoursConfigKey.ShouldBe("OctopusImport:TemporaryUploads:MaxSessionAgeHours");
    }

    [Theory]
    [InlineData(null, 168 * 60)]
    [InlineData("0", 168 * 60)]
    [InlineData("-5", 168 * 60)]
    [InlineData("12", 12 * 60)]
    [InlineData("0.5", 30)]
    public void MaxSessionAge_ConfiguredPositiveHours_OtherwiseSevenDays(string configured, int expectedMinutes)
    {
        var values = new Dictionary<string, string> { [OctopusImportTemporaryUploadSettings.MaxSessionAgeHoursConfigKey] = configured };

        var settings = new OctopusImportTemporaryUploadSettings(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        settings.MaxSessionAge.ShouldBe(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void MaxSessionAge_EnvVarOverridesThroughEnvironmentConfiguration()
    {
        var previous = Environment.GetEnvironmentVariable(OctopusImportTemporaryUploadSettings.MaxSessionAgeHoursEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(OctopusImportTemporaryUploadSettings.MaxSessionAgeHoursEnvVar, "48");

            var settings = new OctopusImportTemporaryUploadSettings(new ConfigurationBuilder().AddEnvironmentVariables().Build());

            settings.MaxSessionAge.ShouldBe(TimeSpan.FromHours(48));
        }
        finally
        {
            Environment.SetEnvironmentVariable(OctopusImportTemporaryUploadSettings.MaxSessionAgeHoursEnvVar, previous);
        }
    }
}
