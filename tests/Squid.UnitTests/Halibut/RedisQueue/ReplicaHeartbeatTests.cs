using System.Linq;
using Shouldly;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.RedisQueue.Encryption;
using Squid.Core.Services.Security;
using Squid.UnitTests.Halibut.DataStreamStorage;
using Xunit;
using static Squid.UnitTests.Halibut.RedisQueue.RedisQueueTestSupport;

namespace Squid.UnitTests.Halibut.RedisQueue;

public sealed class ReplicaPresenceTests
{
    [Fact]
    public void StartsWithNoOtherReplica_AndFollowsWhatItIsTold()
    {
        var presence = new ReplicaPresence();

        presence.OtherReplicaIsLive.ShouldBeFalse();

        presence.Set(true);
        presence.OtherReplicaIsLive.ShouldBeTrue();

        presence.Set(false);
        presence.OtherReplicaIsLive.ShouldBeFalse();
    }

    [Fact]
    public void TheAppearedToken_IsCancelledWhenAReplicaAppears_AndIsFreshAgainOnceItIsGone()
    {
        var presence = new ReplicaPresence();
        var first = presence.OtherReplicaAppeared;

        first.IsCancellationRequested.ShouldBeFalse();

        presence.Set(true);
        first.IsCancellationRequested.ShouldBeTrue("a dequeue waiting without listening to Redis is woken up");

        presence.Set(false);
        presence.OtherReplicaAppeared.IsCancellationRequested.ShouldBeFalse("the next wait is not woken by the previous appearance");
    }

    [Fact]
    public void SettingTheSameValueAgain_DoesNotSignalTwice()
    {
        var presence = new ReplicaPresence();

        presence.Set(false);
        presence.OtherReplicaAppeared.IsCancellationRequested.ShouldBeFalse();
    }
}

public sealed class ReplicaHeartbeatTests
{
    private sealed class FakeStore : IReplicaStore
    {
        public int OthersAlive { get; set; }

        public Exception Failure { get; set; }

        public List<Guid> Beats { get; } = new();

        public List<TimeSpan> Windows { get; } = new();

        public List<Guid> Left { get; } = new();

        public Dictionary<Guid, string> OtherKeyChecks { get; } = new();

        public List<string> PublishedKeyChecks { get; } = new();

        public Func<CancellationToken, Task<ReplicaBeat>> OnBeat { get; set; }

        public Task<ReplicaBeat> BeatAsync(Guid replicaId, TimeSpan liveWindow, string keyCheck, CancellationToken ct)
        {
            if (OnBeat != null) return OnBeat(ct);

            if (Failure != null) throw Failure;

            Beats.Add(replicaId);
            Windows.Add(liveWindow);
            PublishedKeyChecks.Add(keyCheck);

            return Task.FromResult(new ReplicaBeat(OthersAlive, OtherKeyChecks));
        }

        public Task LeaveAsync(Guid replicaId, CancellationToken ct)
        {
            Left.Add(replicaId);

            return Task.CompletedTask;
        }
    }

    private readonly FakeStore _store = new();
    private readonly ReplicaPresence _presence = new();

    private ReplicaHeartbeat Heartbeat() => new(_store, _presence, Options() with { ReplicaHeartbeat = TimeSpan.FromMilliseconds(50) });

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task ABeat_PublishesWhetherAnotherReplicaIsLive(int others, bool expected)
    {
        _store.OthersAlive = others;

        await Heartbeat().BeatOnceAsync(CancellationToken.None);

        _presence.OtherReplicaIsLive.ShouldBe(expected);
        _store.Windows.Single().ShouldBe(TimeSpan.FromMilliseconds(150), "the live window is three beats");
    }

    private static QueueKeyCheck KeyCheck(byte masterKeySeed) => new(new QueueMessageKeyProtector(new Lazy<IVariableEncryptionService>(() => DataStreamStorageTestSupport.Encryption(masterKeySeed))));

    private ReplicaHeartbeat Heartbeat(IQueueKeyCheck keyCheck) => new(_store, _presence, Options() with { ReplicaHeartbeat = TimeSpan.FromMilliseconds(50) }, keyCheck);

    // What a real replica publishes, as another replica sees it.
    private static string PublishedBy(byte masterKeySeed) => KeyCheck(masterKeySeed).Create();

    [Fact]
    public async Task AReplicaWithAnotherMasterKey_IsReportedOnce_AndOneWithTheSameKeyIsNot()
    {
        var sameKey = Guid.NewGuid();
        var otherKey = Guid.NewGuid();
        var heartbeat = Heartbeat(KeyCheck(masterKeySeed: 1));

        _store.OthersAlive = 2;
        _store.OtherKeyChecks[sameKey] = PublishedBy(masterKeySeed: 1);
        _store.OtherKeyChecks[otherKey] = PublishedBy(masterKeySeed: 50);

        await heartbeat.BeatOnceAsync(CancellationToken.None);
        await heartbeat.BeatOnceAsync(CancellationToken.None);

        heartbeat.ReplicasWithAnotherMasterKey.ShouldBe([otherKey]);
    }

    [Fact]
    public async Task AReplicaThatPublishedNoCheck_IsNotReported()
    {
        var heartbeat = Heartbeat(KeyCheck(masterKeySeed: 1));

        _store.OthersAlive = 1;
        _store.OtherKeyChecks[Guid.NewGuid()] = null;

        await heartbeat.BeatOnceAsync(CancellationToken.None);

        heartbeat.ReplicasWithAnotherMasterKey.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheOwnKeyCheck_IsMadeOnce_AndPublishedWithEveryBeat()
    {
        var heartbeat = Heartbeat(KeyCheck(masterKeySeed: 1));

        await heartbeat.BeatOnceAsync(CancellationToken.None);
        await heartbeat.BeatOnceAsync(CancellationToken.None);

        _store.PublishedKeyChecks.Distinct().Count().ShouldBe(1, "sealing it is a PBKDF2: done once");
        _store.PublishedKeyChecks.ShouldAllBe(c => c != null);
        KeyCheck(masterKeySeed: 1).CanOpen(_store.PublishedKeyChecks[0]).ShouldBeTrue();
        KeyCheck(masterKeySeed: 50).CanOpen(_store.PublishedKeyChecks[0]).ShouldBeFalse();
    }

    [Fact]
    public async Task NoMasterKey_PublishesNoCheck_AndTheBeatStillWorks()
    {
        var heartbeat = Heartbeat(new QueueKeyCheck(new QueueMessageKeyProtector(new Lazy<IVariableEncryptionService>(() => throw new InvalidOperationException("no master key")))));

        _store.OthersAlive = 1;

        await heartbeat.BeatOnceAsync(CancellationToken.None);

        _store.PublishedKeyChecks.ShouldAllBe(c => c == null);
        _presence.OtherReplicaIsLive.ShouldBeTrue();
    }

    [Fact]
    public async Task AFailingStore_MeansNoOtherReplica_AndRecoversOnTheNextGoodBeat()
    {
        var heartbeat = Heartbeat();

        _store.OthersAlive = 1;
        await heartbeat.BeatOnceAsync(CancellationToken.None);
        _presence.OtherReplicaIsLive.ShouldBeTrue();

        _store.Failure = new InvalidOperationException("database down");
        await heartbeat.BeatOnceAsync(CancellationToken.None);
        _presence.OtherReplicaIsLive.ShouldBeFalse("when it cannot tell, the queue stays local");

        _store.Failure = null;
        await heartbeat.BeatOnceAsync(CancellationToken.None);
        _presence.OtherReplicaIsLive.ShouldBeTrue();
    }

    // Disposing a container whose beat is in flight: the beat's connection scope may already be gone, and whatever it throws must not fail the shutdown.
    [Fact]
    public async Task ABeatThatFailsWithSomethingOtherThanCancellationWhileShuttingDown_DoesNotFailTheShutdown()
    {
        var inFlight = new TaskCompletionSource();

        _store.OnBeat = async ct =>
        {
            inFlight.SetResult();

            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });

            throw new ObjectDisposedException("the container's scope");
        };

        var heartbeat = Heartbeat();

        heartbeat.Start();

        await inFlight.Task.WithinAsync(TimeSpan.FromSeconds(10), "the first beat to start");
        await Should.NotThrowAsync(async () => await heartbeat.DisposeAsync());
    }

    [Fact]
    public async Task Started_BeatsRepeatedly_AndOnDisposeLeavesUnderItsOwnId()
    {
        _store.OthersAlive = 1;

        var heartbeat = Heartbeat();

        heartbeat.Start();

        await WaitUntilAsync(() => _store.Beats.Count >= 3);
        await heartbeat.DisposeAsync();

        _presence.OtherReplicaIsLive.ShouldBeTrue();
        _store.Beats.Distinct().Count().ShouldBe(1, "one replica, one id");
        _store.Left.ShouldBe([_store.Beats[0]], "a clean shutdown is noticed by the others at once");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition never became true");

            await Task.Delay(10);
        }
    }
}
