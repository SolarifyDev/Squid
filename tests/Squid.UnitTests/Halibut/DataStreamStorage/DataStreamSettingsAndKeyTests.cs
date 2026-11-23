using System.Linq;
using Halibut;
using Microsoft.Extensions.Configuration;
using Moq;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Halibut.DataStreamStorage.Exceptions;
using Squid.Core.Handlers.CommandHandlers.DataStreamStorage;
using Squid.Core.Jobs.RecurringJobs;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Halibut;
using Squid.Message.Commands.DataStreamStorage;
using static Squid.UnitTests.Halibut.DataStreamStorage.DataStreamStorageTestSupport;

namespace Squid.UnitTests.Halibut.DataStreamStorage;

public class DataStreamStorageOptionsTests
{
    private static HalibutSetting Bind(params (string Key, string Value)[] pairs)
        => new(new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))).Build());

    [Fact]
    public void NoConfiguration_YieldsOneMebibyteChunksAndAnHourTtl()
    {
        var options = DataStreamStorageOptions.From(Bind());

        options.ChunkBytes.ShouldBe(1024 * 1024);
        options.OrphanTtl.ShouldBe(TimeSpan.FromMinutes(60));
    }

    // Renaming a key silently drops an operator's override; pin the section path and key names.
    [Fact]
    public void ConfigurationKeys_AreBound()
    {
        var options = DataStreamStorageOptions.From(Bind(("Halibut:DataStreamStorage:ChunkBytes", "262144"), ("Halibut:DataStreamStorage:OrphanTtlMinutes", "15")));

        options.ChunkBytes.ShouldBe(262144);
        options.OrphanTtl.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void NonPositive_FallsBackToTheDefaults(string raw)
    {
        var options = DataStreamStorageOptions.From(Bind(("Halibut:DataStreamStorage:ChunkBytes", raw), ("Halibut:DataStreamStorage:OrphanTtlMinutes", raw)));

        options.ChunkBytes.ShouldBe(DataStreamStorageSettings.DefaultChunkBytes);
        options.OrphanTtl.ShouldBe(TimeSpan.FromMinutes(DataStreamStorageSettings.DefaultOrphanTtlMinutes));
    }

    [Fact]
    public void ChunkBytes_AboveTheMaximum_IsLoweredToIt()
        => DataStreamStorageOptions.From(Bind(("Halibut:DataStreamStorage:ChunkBytes", "2000000000"))).ChunkBytes.ShouldBe(DataStreamStorageSettings.MaxChunkBytes);

    [Fact]
    public void Defaults_ArePinned()
    {
        DataStreamStorageSettings.DefaultChunkBytes.ShouldBe(1024 * 1024);
        DataStreamStorageSettings.MaxChunkBytes.ShouldBe(32 * 1024 * 1024);
        DataStreamStorageSettings.DefaultOrphanTtlMinutes.ShouldBe(60);
    }

    [Fact]
    public void TheDefaultTtl_OutlastsHalibutsRequestMessageLifetime()
        => DataStreamStorageOptions.From(Bind()).OrphanTtl.ShouldBeGreaterThan(TimeSpan.FromMinutes(20), "Halibut keeps a response 20 minutes and a request for its pickup timeout plus 2 minutes");
}

public class DataStreamKeyProtectorTests
{
    // Persisted formats: changing either strands every stored stream.
    [Fact]
    public void PersistedFormat_IsPinned()
    {
        DataStreamKeyProtector.KdfScope.ShouldBe(-1003);
        DataStreamKeyProtector.PurposeTag.ShouldBe("squid.halibut-data-stream-key.v1\n");
    }

    [Fact]
    public void WrapThenUnwrap_RoundTrips_AndTheEnvelopeIsTheMasterKeyV2Envelope()
    {
        var keys = Keys();
        var key = keys.NewKey();

        var wrapped = keys.Wrap(key);

        wrapped.ShouldStartWith(VariableEncryptionService.EncryptionPrefixV2);
        keys.Unwrap(wrapped, Guid.NewGuid()).ShouldBe(key);
    }

    [Fact]
    public void NewKey_IsRandom32Bytes()
    {
        var keys = Keys();

        keys.NewKey().Length.ShouldBe(32);
        keys.NewKey().ShouldNotBe(keys.NewKey());
    }

    [Fact]
    public void Unwrap_AnEnvelopeWrittenForAnotherPurpose_IsRefused()
    {
        var foreign = Encryption().EncryptAsync(Convert.ToBase64String(new byte[32]), DataStreamKeyProtector.KdfScope);

        Should.Throw<DataStreamUnreadableException>(() => Keys().Unwrap(foreign, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext-not-an-envelope")]
    public void Unwrap_NotAnEnvelope_IsRefused_NeverReadAsAPlaintextKey(string value)
        => Should.Throw<DataStreamUnreadableException>(() => Keys().Unwrap(value, Guid.NewGuid()));

    [Fact]
    public void Unwrap_UnderAnotherMasterKey_IsRefused()
    {
        var wrapped = Keys(seed: 1).Wrap(new byte[32]);

        Should.Throw<DataStreamUnreadableException>(() => Keys(seed: 50).Unwrap(wrapped, Guid.NewGuid()));
    }
}

public class DataStreamOrphanSweepTests
{
    private static HalibutSetting Bind(params (string Key, string Value)[] pairs)
        => new(new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))).Build());

    [Fact]
    public async Task Sweep_DeletesOlderThanTheConfiguredTtl_AndReportsTheCount()
    {
        var persistence = new InMemoryDataStreamPersistence { OlderThanResult = 4 };
        var sweeper = new DataStreamOrphanSweeper(persistence, new DataStreamStorageOptions(1024, TimeSpan.FromMinutes(42)));

        (await sweeper.SweepAsync()).ShouldBe(4);
        persistence.DeletedOlderThan.ShouldBe(TimeSpan.FromMinutes(42));
    }

    [Fact]
    public async Task Sweep_FromSettings_UsesTheBoundTtl()
    {
        var setting = new HalibutSetting { DataStreamStorage = { OrphanTtlMinutes = 45 } };
        var persistence = new InMemoryDataStreamPersistence();

        await new DataStreamOrphanSweeper(persistence, setting).SweepAsync();

        persistence.DeletedOlderThan.ShouldBe(TimeSpan.FromMinutes(45));
    }

    // A TTL shorter than a request may wait for pickup (and then for its stream to be read) would delete the data of a request that is still alive.
    [Fact]
    public async Task Sweep_FromSettings_NeverUsesATtlShorterThanASafeRequestMayWait()
    {
        var setting = new HalibutSetting { DataStreamStorage = { OrphanTtlMinutes = 1 } };
        var persistence = new InMemoryDataStreamPersistence();

        await new DataStreamOrphanSweeper(persistence, setting).SweepAsync();

        persistence.DeletedOlderThan.ShouldBe(DataStreamStorageOptions.MinimumOrphanTtl);
    }

    [Theory]
    [InlineData(2, 20)]
    [InlineData(10, 20)]
    [InlineData(15, 25)]
    [InlineData(60, 70)]
    public void SafeOrphanTtl_IsThePickupTimeoutPlusTheMargin_ButNeverUnderTheMinimum(int pickupMinutes, int expectedMinutes)
        => DataStreamStorageOptions.SafeOrphanTtl(TimeSpan.FromMinutes(pickupMinutes)).ShouldBe(TimeSpan.FromMinutes(expectedMinutes));

    [Theory]
    [InlineData(5, 15, 25)]
    [InlineData(40, 15, 40)]
    public void WithSafeOrphanTtl_RaisesAShortTtl_AndKeepsALongOne(int configuredMinutes, int pickupMinutes, int expectedMinutes)
        => new DataStreamStorageOptions(1024, TimeSpan.FromMinutes(configuredMinutes)).WithSafeOrphanTtl(TimeSpan.FromMinutes(pickupMinutes)).OrphanTtl.ShouldBe(TimeSpan.FromMinutes(expectedMinutes));

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(19, 2, true)]
    [InlineData(20, 2, false)]
    [InlineData(60, 2, false)]
    [InlineData(30, 25, true)]
    public void TheStartupCheck_SaysSoWhenTheConfiguredTtlIsTooShort(int configuredMinutes, int pickupMinutes, bool expectedToComplain)
        => new DataStreamOrphanTtlStartupCheck(new HalibutSetting { DataStreamStorage = { OrphanTtlMinutes = configuredMinutes } }).Check(TimeSpan.FromMinutes(pickupMinutes)).ShouldBe(expectedToComplain);

    [Fact]
    public void TheStartupCheck_WithNoSetting_UsesTheDefaults_AndIsQuiet()
        => new DataStreamOrphanTtlStartupCheck().Check(TimeSpan.FromMinutes(2)).ShouldBeFalse();

    [Fact]
    public void KeyRotation_HasADefault_IsBound_AndFallsBackWhenInvalid()
    {
        DataStreamStorageOptions.From(Bind()).KeyRotation.ShouldBe(TimeSpan.FromMinutes(10));
        DataStreamStorageOptions.From(Bind(("Halibut:DataStreamStorage:KeyRotationMinutes", "3"))).KeyRotation.ShouldBe(TimeSpan.FromMinutes(3));
        DataStreamStorageOptions.From(Bind(("Halibut:DataStreamStorage:KeyRotationMinutes", "0"))).KeyRotation.ShouldBe(TimeSpan.FromMinutes(10));
        DataStreamStorageOptions.From(Bind(("Halibut:DataStreamStorage:KeyRotationMinutes", "-4"))).KeyRotation.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Handler_DispatchesToTheSweeper_AndReturnsItsCount()
    {
        var sweeper = new Mock<IDataStreamOrphanSweeper>();
        sweeper.Setup(s => s.SweepAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var response = await new SweepOrphanedDataStreamsCommandHandler(sweeper.Object).Handle(new Mock<IReceiveContext<SweepOrphanedDataStreamsCommand>>().Object, CancellationToken.None);

        response.Data.Removed.ShouldBe(3);
        sweeper.Verify(s => s.SweepAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // The sweep is only a thin dispatcher: if the job stopped sending the command nothing would ever remove an orphan.
    [Fact]
    public async Task Job_SendsTheSweepCommand_ThroughTheMediator()
    {
        var mediator = new Mock<Mediator.Net.IMediator>();
        mediator.Setup(m => m.SendAsync<SweepOrphanedDataStreamsCommand, SweepOrphanedDataStreamsResponse>(It.IsAny<SweepOrphanedDataStreamsCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SweepOrphanedDataStreamsResponse());
        var job = new DataStreamOrphanSweepRecurringJob(mediator.Object);

        await job.Execute();

        mediator.Verify(m => m.SendAsync<SweepOrphanedDataStreamsCommand, SweepOrphanedDataStreamsResponse>(It.IsAny<SweepOrphanedDataStreamsCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        job.JobId.ShouldBe("halibut-data-stream-orphan-sweep");
        job.CronExpression.ShouldBe("*/10 * * * *");
    }
}
