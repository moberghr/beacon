using System.Collections;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace Beacon.Tests.Common;

/// <summary>
/// Minimal async-queryable test doubles so a mocked <c>DbSet&lt;T&gt;</c> can service
/// EF Core async terminal operators (<c>FirstOrDefaultAsync</c>, <c>ToListAsync</c>, …)
/// against an in-memory sequence — WITHOUT a database connection and WITHOUT the
/// forbidden <c>UseInMemoryDatabase</c> provider (§4.7).
///
/// This is the standard Microsoft-documented pattern for unit-testing code that calls
/// async EF operators through a mocked context.
/// </summary>
internal sealed class TestAsyncQueryProvider<TEntity> : IAsyncQueryProvider
{
    private readonly IQueryProvider _inner;
    private readonly Func<IReadOnlyList<TEntity>, int>? _onExecuteDelete;

    /// <param name="onExecuteDelete">
    /// Optional: services <c>ExecuteDeleteAsync</c> over the double. Receives the rows the query would delete
    /// (evaluated in memory) and returns the count — the callback owns removing them from the backing list.
    /// Without it an <c>ExecuteDeleteAsync</c> call fails the way it does on any non-EF provider.
    /// </param>
    internal TestAsyncQueryProvider(IQueryProvider inner, Func<IReadOnlyList<TEntity>, int>? onExecuteDelete = null)
    {
        _inner = inner;
        _onExecuteDelete = onExecuteDelete;
    }

    public IQueryable CreateQuery(Expression expression) => new TestAsyncEnumerable<TEntity>(expression, _onExecuteDelete);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new TestAsyncEnumerable<TElement>(expression, (object?)_onExecuteDelete as Func<IReadOnlyList<TElement>, int>);

    public object? Execute(Expression expression) => _inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => _inner.Execute<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        // EF's ExecuteDeleteAsync wraps the source in a call to RelationalQueryableExtensions.ExecuteDelete; the
        // LINQ-to-Objects inner provider cannot execute that, so evaluate the source and hand the rows to the test.
        if (_onExecuteDelete != null
            && expression is MethodCallExpression { Method.Name: "ExecuteDelete" } deleteCall)
        {
            var matched = _inner.CreateQuery<TEntity>(deleteCall.Arguments[0]).ToList();

            return (TResult)(object)Task.FromResult(_onExecuteDelete(matched));
        }

        var expectedResultType = typeof(TResult).GetGenericArguments()[0];
        var executionResult = typeof(IQueryProvider)
            .GetMethods()
            .First(m => m.Name == nameof(IQueryProvider.Execute) && m.IsGenericMethod)
            .MakeGenericMethod(expectedResultType)
            .Invoke(this, new object[] { expression });

        return (TResult)typeof(Task)
            .GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(expectedResultType)
            .Invoke(null, new[] { executionResult })!;
    }
}

internal sealed class TestAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    private readonly Func<IReadOnlyList<T>, int>? _onExecuteDelete;

    public TestAsyncEnumerable(IEnumerable<T> enumerable) : base(enumerable) { }

    public TestAsyncEnumerable(Expression expression, Func<IReadOnlyList<T>, int>? onExecuteDelete = null) : base(expression)
    {
        _onExecuteDelete = onExecuteDelete;
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new TestAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());

    IQueryProvider IQueryable.Provider => new TestAsyncQueryProvider<T>(this, _onExecuteDelete);
}

internal sealed class TestAsyncEnumerator<T> : IAsyncEnumerator<T>
{
    private readonly IEnumerator<T> _inner;

    public TestAsyncEnumerator(IEnumerator<T> inner) => _inner = inner;

    public T Current => _inner.Current;

    public ValueTask<bool> MoveNextAsync() => new(_inner.MoveNext());

    public ValueTask DisposeAsync()
    {
        _inner.Dispose();
        return ValueTask.CompletedTask;
    }
}
