using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// Core reads SQL Server's errors by the driver's type name, without referencing the driver: the error number and each
/// error's number, class and state of a real <c>SqlException</c>; nothing from any other exception. A <c>SqlException</c>
/// whose members cannot be bound or read fails closed — no classification, no failure code, and the generic text in
/// place of its message, which may be one the statement's own code raised.
/// </summary>
[TestFixture]
public class SqlServerErrorsTests
{
    private const string RaisedText = "Customer alice.horvat@bank.example is blocked";

    [Test]
    public void ARealSqlException_IsRead_NumberAndEachErrorsClassAndState()
    {
        var info = SqlServerErrors.Read(TestSqlServerExceptions.Real(50001, RaisedText, errorClass: 16, state: 3));

        info.IsSqlException.Should().BeTrue();
        info.IsReadable.Should().BeTrue();
        info.Number.Should().Be(50001);
        info.Errors.Should().Equal(new SqlServerError(50001, 16, 3));
    }

    [Test]
    public void AnyOtherException_IsNotASqlException()
    {
        SqlServerErrors.Read(new InvalidOperationException(RaisedText)).Should().Be(SqlServerErrorInfo.NotSqlException);
        SqlServerErrors.Read(new PostgresException("dup", "ERROR", "ERROR", "23505")).IsSqlException.Should().BeFalse();
    }

    [TestCase(2601)]
    [TestCase(2627)]
    public void SqlServersDuplicateKeyErrors_AreUniqueViolations(int number)
    {
        DbUniqueViolation.IsUniqueViolation(TestSqlServerExceptions.Real(number, "duplicate")).Should().BeTrue();
        DbUniqueViolation.IsUniqueViolation(new DbUpdateException("x", TestSqlServerExceptions.Real(number, "duplicate"))).Should().BeTrue();
        DbSerializationConflict.IsSerializationConflict(TestSqlServerExceptions.Real(number, "duplicate")).Should().BeFalse();
    }

    [TestCase(1205)]
    [TestCase(3960)]
    public void SqlServersDeadlockAndSnapshotErrors_AreSerializationConflicts(int number)
    {
        DbSerializationConflict.IsSerializationConflict(TestSqlServerExceptions.Real(number, "conflict")).Should().BeTrue();
        DbSerializationConflict.IsSerializationConflict(new DbUpdateException("x", TestSqlServerExceptions.Real(number, "conflict"))).Should().BeTrue();
        DbUniqueViolation.IsUniqueViolation(TestSqlServerExceptions.Real(number, "conflict")).Should().BeFalse();
    }

    [Test]
    public void ANonSqlClientException_IsNotClassified_AndHasNoCode()
    {
        var other = new InvalidOperationException("2627 1205");

        DbUniqueViolation.IsUniqueViolation(other).Should().BeFalse();
        DbSerializationConflict.IsSerializationConflict(other).Should().BeFalse();
        FailureCodes.ErrorCodeOf(other).Should().BeNull();
    }

    [Test]
    public void ARealSqlException_GivesItsNumberAsTheFailureCode()
    {
        FailureCodes.ErrorCodeOf(new DbUpdateException("x", TestSqlServerExceptions.Real(8114, RaisedText))).Should().Be("8114");
    }

    [Test]
    public void ASqlExceptionWhoseMembersDoNotBind_IsUnreadable_AndFailsClosed()
    {
        // The SQLSTATEs would classify it, were the unreadable error not refused first.
        AssertFailsClosed(TestSqlServerExceptions.Unbindable(RaisedText, sqlState: "23505"));
        AssertFailsClosed(TestSqlServerExceptions.Unbindable(RaisedText, sqlState: "40001"));
    }

    [TestCase(50000)]
    [TestCase(2627)]
    [TestCase(1205)]
    public void ASqlExceptionWhoseErrorsCannotBeRead_IsUnreadable_AndFailsClosed(int number)
    {
        // The number alone would classify it or name a raised error: an error that cannot be read in full is not trusted.
        AssertFailsClosed(TestSqlServerExceptions.ErrorsThrow(RaisedText, number));
    }

    [TestCase(UnreadableShape.NumberNotInt)]
    [TestCase(UnreadableShape.NumberAmbiguous)]
    [TestCase(UnreadableShape.ErrorsNotEnumerable)]
    [TestCase(UnreadableShape.NullError)]
    [TestCase(UnreadableShape.ErrorOfWrongType)]
    [TestCase(UnreadableShape.ErrorClassNotByte)]
    [TestCase(UnreadableShape.ErrorStateNotByte)]
    [TestCase(UnreadableShape.ErrorGetterThrows)]
    [TestCase(UnreadableShape.SqlErrorTypeMissing)]
    public void ASqlExceptionWithAMemberOfTheWrongShape_IsUnreadable_AndFailsClosed(UnreadableShape shape)
    {
        AssertFailsClosed(TestSqlServerExceptions.Unreadable(shape, RaisedText));
    }

    [Test]
    public void TheEmittedStandIn_IsReadable_WhenNothingIsWrongWithIt()
    {
        var info = SqlServerErrors.Read(TestSqlServerExceptions.Unreadable((UnreadableShape)(-1), RaisedText, number: 2627));

        info.IsReadable.Should().BeTrue();
        info.Number.Should().Be(2627);
        info.Errors.Should().Equal(new SqlServerError(2627, 16, 1));
    }

    [Test]
    public void TheAmbiguousStandIn_ReallyMakesTheReflectionLookupThrow()
    {
        var ambiguous = TestSqlServerExceptions.Unreadable(UnreadableShape.NumberAmbiguous, RaisedText);

        var lookup = () => ambiguous.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);

        lookup.Should().Throw<AmbiguousMatchException>();
    }

    [Test]
    public void AReadableLookalikeFromAnotherAssembly_IsNotASqlException()
    {
        var lookalike = TestSqlServerExceptions.Lookalike(2627, RaisedText);

        lookalike.GetType().FullName.Should().Be("Microsoft.Data.SqlClient.SqlException");
        SqlServerErrors.Read(lookalike).Should().Be(SqlServerErrorInfo.NotSqlException);
        DbUniqueViolation.IsUniqueViolation(lookalike).Should().BeFalse();
        DbUniqueViolation.IsUniqueViolation(new DbUpdateException("x", lookalike)).Should().BeFalse();
        DbSerializationConflict.IsSerializationConflict(lookalike).Should().BeFalse();
        FailureCodes.ErrorCodeOf(lookalike).Should().BeNull();
        FailureCodes.ErrorCodeOf(new DbUpdateException("x", lookalike)).Should().BeNull();
    }

    private static void AssertFailsClosed(Exception unreadable)
    {
        SqlServerErrors.Read(unreadable).Should().Be(SqlServerErrorInfo.Unreadable);
        SqlServerErrors.Read(new DbUpdateException("x", unreadable)).Should().Be(SqlServerErrorInfo.NotSqlException, "only the exception itself is read");

        DbUniqueViolation.IsUniqueViolation(unreadable).Should().BeFalse();
        DbUniqueViolation.IsUniqueViolation(new DbUpdateException("x", unreadable)).Should().BeFalse();
        DbSerializationConflict.IsSerializationConflict(unreadable).Should().BeFalse();
        DbSerializationConflict.IsSerializationConflict(new DbUpdateException("x", unreadable)).Should().BeFalse();

        FailureCodes.ErrorCodeOf(unreadable).Should().BeNull();
        FailureCodes.ErrorCodeOf(new DbUpdateException("x", unreadable)).Should().BeNull();
        FailureCodes.Describe(unreadable).Should().Contain("error code none").And.NotContain("alice");

        DatabaseErrorRedactor.TextOf(unreadable).Should().Be(DatabaseErrorRedactor.UnreadableSqlServerMessage);
        DatabaseErrorRedactor.TextOf(new DbUpdateException("x", unreadable)).Should().Be(DatabaseErrorRedactor.UnreadableSqlServerMessage);
    }
}
