using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Semantico.Core.Data;

namespace Semantico.Tests.Common;

/// <summary>
/// Base class for tests that verify EF Core LINQ queries translate to valid PostgreSQL SQL.
///
/// These tests catch queries that compile in C# but fail at runtime because Npgsql
/// can't translate them (e.g., Max() on empty sequences, unsupported LINQ methods).
///
/// Usage: call AssertQueryTranslates() with a lambda that builds an IQueryable from the context.
/// The test passes if ToQueryString() succeeds — meaning Npgsql can generate valid SQL.
/// No database connection is actually opened.
/// </summary>
public abstract class QueryTranslationTestBase : IDisposable
{
    protected NpgsqlTestContext Context { get; } = NpgsqlTestContext.Create();

    /// <summary>
    /// Verifies that the given LINQ query can be translated to PostgreSQL SQL.
    /// Throws if the Npgsql provider can't translate the expression tree.
    /// </summary>
    protected void AssertQueryTranslates<T>(Func<SemanticoContext, IQueryable<T>> queryBuilder)
    {
        var query = queryBuilder(Context);
        var sql = query.ToQueryString();

        Assert.That(sql, Is.Not.Null.And.Not.Empty,
            "Query translated to empty SQL — check the LINQ expression.");
    }

    public void Dispose()
    {
        Context.Dispose();
        GC.SuppressFinalize(this);
    }
}
