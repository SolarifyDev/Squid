using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.Security;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

/// <summary>
/// The service-layer compare-and-set that fills a secret an import left empty: which Squid value is meant (identity), whether it
/// is still empty, and that a value set by anyone — before the read, between the read and the write, or by a concurrent save that
/// re-inserted the set's variables — is never overwritten.
/// </summary>
public class SecretFillServiceTests
{
    private const int SetId = 41;

    private static readonly VariableIdentity DbPassword = VariableIdentity.Of("Db.Password", VariableType.Password, [(VariableScopeType.Environment, "3")]);

    // ── Identity ───────────────────────────────────────────────────────────

    [Fact]
    public void Identity_ScopeOrderWhitespaceAndBlankValues_DoNotMatter()
    {
        var stored = VariableIdentity.Of("Db.Password", VariableType.Password, [(VariableScopeType.Machine, "9"), (VariableScopeType.Environment, " 3 "), (VariableScopeType.Role, ""), (VariableScopeType.Environment, "3")]);
        var incoming = VariableIdentity.Of("Db.Password", VariableType.Password, [(VariableScopeType.Environment, "3"), (VariableScopeType.Machine, "9")]);

        stored.Matches(incoming).ShouldBeTrue();
        stored.Scopes.ShouldBe(["Environment:3", "Machine:9"]);
    }

    [Theory]
    [InlineData("db.password", VariableType.Password, "3")]   // names are case-sensitive in Squid
    [InlineData("Db.Password", VariableType.String, "3")]
    [InlineData("Db.Password", VariableType.Password, "4")]
    [InlineData("Db.Password", VariableType.Password, null)]
    public void Identity_OtherNameTypeOrScope_DoesNotMatch(string name, VariableType type, string environment)
    {
        var other = VariableIdentity.Of(name, type, environment == null ? [] : [(VariableScopeType.Environment, environment)]);

        DbPassword.Matches(other).ShouldBeFalse();
        DbPassword.Matches(null).ShouldBeFalse();
    }

    // ── Which variable, and is it still empty ──────────────────────────────

    [Theory]
    [InlineData(new bool[0], SecretSlotState.NotFound)]
    [InlineData(new[] { false }, SecretSlotState.Empty)]
    [InlineData(new[] { true }, SecretSlotState.AlreadySet)]
    [InlineData(new[] { false, false }, SecretSlotState.Ambiguous)]
    [InlineData(new[] { false, true }, SecretSlotState.Ambiguous)]
    public void VariableSlot_ClassifiedByTheMatchingVariablesOfTheSet(bool[] matchingHaveValue, SecretSlotState expected)
    {
        var slots = matchingHaveValue.Select((hasValue, i) => new SensitiveVariableSlot(100 + i, SetId, DbPassword, hasValue)).ToList();
        slots.Add(new SensitiveVariableSlot(200, SetId + 1, DbPassword, false));
        slots.Add(new SensitiveVariableSlot(201, SetId, VariableIdentity.Of("Db.Password", VariableType.Password, []), false));

        var (state, slot) = SensitiveVariableSlots.Find(slots, SetId, DbPassword);

        state.ShouldBe(expected);
        (slot?.VariableId).ShouldBe(expected is SecretSlotState.Empty or SecretSlotState.AlreadySet ? 100 : null);
    }

    [Theory]
    [InlineData(false, null, "robot", SecretSlotState.Empty)]
    [InlineData(false, "", "robot", SecretSlotState.Empty)]
    [InlineData(false, "robot", "robot", SecretSlotState.Empty)]
    [InlineData(false, "user-robot", "robot", SecretSlotState.AlreadySet)]   // a username set in Squid makes the credentials the user's
    [InlineData(false, "robot", null, SecretSlotState.AlreadySet)]
    [InlineData(false, null, null, SecretSlotState.Empty)]
    [InlineData(true, null, "robot", SecretSlotState.AlreadySet)]
    [InlineData(true, "robot", "robot", SecretSlotState.AlreadySet)]
    public void FeedSlot_FillableOnlyWithoutPasswordAndWithAnEmptyOrTheSameUsername(bool hasPassword, string storedUsername, string fillUsername, SecretSlotState expected)
    {
        ExternalFeedCredentialSlots.StateOf(new ExternalFeedCredentialSlot(7, storedUsername, hasPassword), fillUsername).ShouldBe(expected);
        ExternalFeedCredentialSlots.StateOf(null, fillUsername).ShouldBe(SecretSlotState.NotFound);
    }

    // ── Variable compare-and-set ───────────────────────────────────────────

    [Theory]
    [InlineData(true, SecretSlotState.AlreadySet)]
    [InlineData(false, SecretSlotState.NotFound)]
    public async Task FillVariable_NotEmptyOrGone_NothingWritten(bool exists, SecretSlotState expected)
    {
        var harness = new VariableHarness(exists ? [new SensitiveVariableSlot(100, SetId, DbPassword, true)] : []);

        (await harness.FillAsync("from-export")).ShouldBe(expected);

        harness.Writes.ShouldBeEmpty();
        harness.VersionBumps.ShouldBe(0);
    }

    [Fact]
    public async Task FillVariable_TwoVariablesWithTheIdentity_AmbiguousNothingWritten()
    {
        var harness = new VariableHarness([new SensitiveVariableSlot(100, SetId, DbPassword, false), new SensitiveVariableSlot(101, SetId, DbPassword, false)]);

        (await harness.FillAsync("from-export")).ShouldBe(SecretSlotState.Ambiguous);

        harness.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task FillVariable_StillEmpty_WritesTheEncryptedValueOnceAndMovesTheSetVersion()
    {
        var harness = new VariableHarness([new SensitiveVariableSlot(100, SetId, DbPassword, false)]);

        (await harness.FillAsync("from-export")).ShouldBe(SecretSlotState.Filled);

        harness.Writes.ShouldBe([(100, $"enc[{SetId}]:from-export")]);
        harness.VersionBumps.ShouldBe(1);
    }

    // Set by a user between the read and the write: the conditional UPDATE writes nothing and nothing is overwritten.
    [Fact]
    public async Task FillVariable_SetBetweenReadAndWrite_AlreadySetNoVersionBump()
    {
        var harness = new VariableHarness([new SensitiveVariableSlot(100, SetId, DbPassword, false)], [new SensitiveVariableSlot(100, SetId, DbPassword, true)]) { RowsWritten = [0] };

        (await harness.FillAsync("from-export")).ShouldBe(SecretSlotState.AlreadySet);

        harness.Writes.Count.ShouldBe(1, "one conditional write was attempted and refused by the database");
        harness.VersionBumps.ShouldBe(0);
    }

    // The set was saved in between (variables re-inserted with new ids, still empty): found again by identity and filled once.
    [Fact]
    public async Task FillVariable_SetReSavedBetweenReadAndWrite_FoundAgainByIdentityAndFilled()
    {
        var harness = new VariableHarness([new SensitiveVariableSlot(100, SetId, DbPassword, false)], [new SensitiveVariableSlot(300, SetId, DbPassword, false)]) { RowsWritten = [0, 1] };

        (await harness.FillAsync("from-export")).ShouldBe(SecretSlotState.Filled);

        harness.Writes.Select(w => w.VariableId).ShouldBe([100, 300]);
        harness.VersionBumps.ShouldBe(1);
    }

    [Fact]
    public async Task FillVariable_ReSavedTwiceInARow_StaysEmptyNothingWritten()
    {
        var harness = new VariableHarness([new SensitiveVariableSlot(100, SetId, DbPassword, false)], [new SensitiveVariableSlot(300, SetId, DbPassword, false)]) { RowsWritten = [0, 0] };

        (await harness.FillAsync("from-export")).ShouldBe(SecretSlotState.Empty);

        harness.VersionBumps.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task FillVariable_EmptySourceValue_Refused(string value)
    {
        var harness = new VariableHarness([new SensitiveVariableSlot(100, SetId, DbPassword, false)]);

        await Should.ThrowAsync<ArgumentException>(() => harness.FillAsync(value));

        harness.Writes.ShouldBeEmpty();
    }

    // ── Feed compare-and-set ───────────────────────────────────────────────

    [Theory]
    [InlineData(true, null, 1, SecretSlotState.AlreadySet, false)]
    [InlineData(false, "user-robot", 1, SecretSlotState.AlreadySet, false)]
    [InlineData(false, null, 1, SecretSlotState.Filled, true)]
    [InlineData(false, "robot", 1, SecretSlotState.Filled, true)]
    [InlineData(false, null, 0, SecretSlotState.AlreadySet, true)]   // set between the read and the conditional write
    public async Task FillFeed_OnlyWhileTheCredentialsAreStillEmpty(bool hasPassword, string storedUsername, int rowsWritten, SecretSlotState expected, bool writeAttempted)
    {
        var provider = new Mock<IExternalFeedDataProvider>();
        provider.Setup(p => p.GetCredentialSlotsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([new ExternalFeedCredentialSlot(7, storedUsername, hasPassword)]);
        provider.Setup(p => p.SetCredentialsIfEmptyAsync(7, "robot", "registry-token", It.IsAny<CancellationToken>())).ReturnsAsync(rowsWritten);

        var state = await new ExternalFeedService(Mock.Of<IMapper>(), provider.Object).FillMissingCredentialsAsync(7, "robot", "registry-token", CancellationToken.None);

        state.ShouldBe(expected);
        provider.Verify(p => p.SetCredentialsIfEmptyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), writeAttempted ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task FillFeed_EmptySourcePassword_Refused(string password)
    {
        var provider = new Mock<IExternalFeedDataProvider>();

        await Should.ThrowAsync<ArgumentException>(() => new ExternalFeedService(Mock.Of<IMapper>(), provider.Object).FillMissingCredentialsAsync(7, "robot", password, CancellationToken.None));

        provider.Verify(p => p.SetCredentialsIfEmptyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private sealed class VariableHarness
    {
        private readonly Queue<List<SensitiveVariableSlot>> _reads = new();
        private readonly Mock<IVariableDataProvider> _provider = new();

        public VariableHarness(List<SensitiveVariableSlot> firstRead, List<SensitiveVariableSlot> laterReads = null)
        {
            _reads.Enqueue(firstRead);
            var later = laterReads ?? firstRead;

            _provider.Setup(p => p.GetSensitiveVariableSlotsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _reads.Count > 0 ? _reads.Dequeue() : later);
            _provider.Setup(p => p.SetSensitiveValueIfEmptyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int id, string value, CancellationToken _) =>
                {
                    Writes.Add((id, value));
                    return RowsWritten.Count >= Writes.Count ? RowsWritten[Writes.Count - 1] : 1;
                });
            _provider.Setup(p => p.GetVariableSetByIdAsync(SetId, It.IsAny<CancellationToken>())).ReturnsAsync(() => new VariableSet { Id = SetId, Version = 3 });
            _provider.Setup(p => p.UpdateVariableSetAsync(It.Is<VariableSet>(s => s.Id == SetId && s.Version == 4), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback(() => VersionBumps++).Returns(Task.CompletedTask);
        }

        public List<(int VariableId, string Value)> Writes { get; } = [];

        public List<int> RowsWritten { get; init; } = [];

        public int VersionBumps { get; private set; }

        public Task<SecretSlotState> FillAsync(string value)
        {
            var encryption = new Mock<IVariableEncryptionService>();
            encryption.Setup(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<int>())).Returns((string plain, int setId) => $"enc[{setId}]:{plain}");

            var service = new VariableService(Mock.Of<IMapper>(), _provider.Object, Mock.Of<IVariableScopeDataProvider>(), new SensitiveVariableHandler(), Mock.Of<ILibraryVariableSetDataProvider>(), encryption.Object);

            return service.FillEmptySensitiveValueAsync(SetId, DbPassword, value, CancellationToken.None);
        }
    }
}
