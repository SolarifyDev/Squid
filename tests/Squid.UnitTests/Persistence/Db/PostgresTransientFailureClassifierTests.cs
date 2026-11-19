using System.Linq;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Squid.Core.Persistence.Db;

namespace Squid.UnitTests.Persistence.Db;

public class PostgresTransientFailureClassifierTests
{
    [Theory]
    [InlineData("40001", true)]  // serialization_failure
    [InlineData("40P01", true)]  // deadlock_detected
    [InlineData("55P03", true)]  // lock_not_available
    [InlineData("57P01", true)]  // admin_shutdown
    [InlineData("08006", true)]  // connection_failure
    [InlineData("53300", true)]  // too_many_connections
    [InlineData("23505", false)] // unique_violation: the same insert fails again
    [InlineData("23503", false)] // foreign_key_violation
    [InlineData("42P01", false)] // undefined_table
    [InlineData("P0001", false)] // raise_exception from a trigger or function
    public void IsTransient_PostgresSqlState(string sqlState, bool expected)
    {
        PostgresTransientFailureClassifier.IsTransient(Postgres(sqlState)).ShouldBe(expected);
    }

    // The list is the contract of what a caller retries: a driver upgrade must not silently widen or narrow it.
    [Fact]
    public void TransientSqlStates_Pinned()
    {
        PostgresTransientFailureClassifier.TransientSqlStates.OrderBy(s => s, StringComparer.Ordinal).ShouldBe(["08000", "08001", "08003", "08004", "08006", "40001", "40P01", "53300", "55P03", "57P01", "57P02", "57P03"]);
    }

    [Fact]
    public void IsTransient_NpgsqlIoFailure_True()
    {
        PostgresTransientFailureClassifier.IsTransient(new NpgsqlException("Exception while reading from stream", new IOException("connection reset"))).ShouldBeTrue();
    }

    [Fact]
    public void IsTransient_TimeoutInChain_True()
    {
        PostgresTransientFailureClassifier.IsTransient(new InvalidOperationException("wrapper", new TimeoutException("command timeout"))).ShouldBeTrue();
    }

    [Fact]
    public void IsTransient_WrappedInDbUpdateException_True()
    {
        PostgresTransientFailureClassifier.IsTransient(new DbUpdateException("save failed", Postgres("40001"))).ShouldBeTrue();
    }

    [Fact]
    public void IsTransient_WrappedNonTransient_False()
    {
        PostgresTransientFailureClassifier.IsTransient(new DbUpdateException("save failed", Postgres("23505"))).ShouldBeFalse();
    }

    [Fact]
    public void IsTransient_AggregateMixed_False()
    {
        PostgresTransientFailureClassifier.IsTransient(new AggregateException(Postgres("40001"), new InvalidOperationException("bug"))).ShouldBeFalse();
    }

    [Fact]
    public void IsTransient_AggregateAllTransient_True()
    {
        PostgresTransientFailureClassifier.IsTransient(new AggregateException(Postgres("40001"), Postgres("40P01"))).ShouldBeTrue();
    }

    [Fact]
    public void IsTransient_OperationCanceled_False()
    {
        PostgresTransientFailureClassifier.IsTransient(new OperationCanceledException("cancelled", new TimeoutException())).ShouldBeFalse();
    }

    [Fact]
    public void IsTransient_PlainBug_False()
    {
        PostgresTransientFailureClassifier.IsTransient(new NullReferenceException()).ShouldBeFalse();
    }

    [Fact]
    public void SqlStateOf_FindsTheFirstPostgresErrorInTheChain()
    {
        PostgresTransientFailureClassifier.SqlStateOf(new DbUpdateException("save failed", Postgres("40P01"))).ShouldBe("40P01");
        PostgresTransientFailureClassifier.SqlStateOf(new TimeoutException()).ShouldBeNull();
    }

    private static PostgresException Postgres(string sqlState) => new("test failure", "ERROR", "ERROR", sqlState);
}
