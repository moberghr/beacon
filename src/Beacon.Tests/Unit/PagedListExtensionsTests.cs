using System.Net.Http.Json;
using Beacon.Core.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The paging contract every list endpoint shares: zero-based clamped paging, <c>-column,column</c> sorting
/// that never throws on a stale column, and an order that always ends on a unique column so pages cannot
/// overlap.
/// </summary>
[TestFixture]
public class PagedListExtensionsTests
{
    private static readonly Row[] Rows =
    [
        new(1, "charlie", 30, new Owner("zed")),
        new(2, "alpha", 10, new Owner("amy")),
        new(3, "bravo", 30, new Owner("bob")),
        new(4, "delta", 20, new Owner("amy")),
        new(5, "echo", 10, new Owner("cid")),
    ];

    [TestCase(null, null, 0, ListRequest.DefaultPageSize)]
    [TestCase(-3, 0, 0, 1)]
    [TestCase(2, 100_000, 2, ListRequest.MaxPageSize)]
    [TestCase(1, 5, 1, 5)]
    public void PagingValues_AreDefaultedAndClamped(int? page, int? pageSize, int expectedPage, int expectedPageSize)
    {
        var request = new TestRequest { Page = page, PageSize = pageSize };

        request.PageOrDefault.Should().Be(expectedPage);
        request.PageSizeOrDefault.Should().Be(expectedPageSize);
    }

    [Test]
    public void ParseSort_ReadsDirectionsAndSkipsBlanks()
    {
        var criteria = ListRequest.ParseSort(" -score, name ,, -");

        criteria.Select(x => (x.SortColumn, x.SortDirection)).Should().Equal(
            ("score", SortDirection.Descending),
            ("name", SortDirection.Ascending));
    }

    [Test]
    public void ToPagedList_SortsCaseInsensitively_ThenByIdAsTiebreaker()
    {
        var page = Rows.ToPagedList(new TestRequest { Sort = "-SCORE" });

        page.Items.Select(x => x.Id).Should().Equal(1, 3, 4, 2, 5);
    }

    [Test]
    public void ToPagedList_SortsByNavigationPath()
    {
        var page = Rows.ToPagedList(new TestRequest { Sort = "owner.name,-name" });

        page.Items.Select(x => x.Id).Should().Equal(4, 2, 3, 5, 1);
    }

    [Test]
    public void ToPagedList_UnknownOrNonScalarColumn_FallsBackToDefaultSort()
    {
        var page = Rows.ToPagedList(new TestRequest { Sort = "doesNotExist,owner" }, defaultSort: "name");

        page.Items.Select(x => x.Name).Should().Equal("alpha", "bravo", "charlie", "delta", "echo");
    }

    [Test]
    public void ToPagedList_NoSortAndNoDefault_OrdersById()
    {
        var page = Rows.Reverse().ToPagedList(new TestRequest());

        page.Items.Select(x => x.Id).Should().Equal(1, 2, 3, 4, 5);
    }

    [Test]
    public void ToPagedList_ReturnsRequestedPageWithTotals()
    {
        var page = Rows.ToPagedList(new TestRequest { Page = 1, PageSize = 2, Sort = "name" });

        page.Items.Select(x => x.Name).Should().Equal("charlie", "delta");
        page.TotalCount.Should().Be(5);
        page.PageCount.Should().Be(3);
    }

    [Test]
    public void ToPagedList_ConsecutivePagesNeverOverlapOnTiedKeys()
    {
        var seen = new List<int>();
        for (var index = 0; index < 3; index++)
        {
            seen.AddRange(Rows.ToPagedList(new TestRequest { Page = index, PageSize = 2, Sort = "score" }).Items.Select(x => x.Id));
        }

        seen.Should().OnlyHaveUniqueItems().And.HaveCount(Rows.Length);
    }

    [Test]
    public void ToPagedList_CustomTiebreaker_IsUsed()
    {
        var page = Rows
            .Select(x => new KeyedRow(x.Id, x.Score))
            .ToPagedList(new TestRequest { Sort = "score" }, tiebreaker: "rowKey");

        page.Items.Select(x => x.RowKey).Should().Equal(2, 5, 4, 1, 3);
    }

    [Test]
    public void EmptySource_HasNoPages()
    {
        var page = Array.Empty<Row>().ToPagedList(new TestRequest());

        page.TotalCount.Should().Be(0);
        page.PageCount.Should().Be(0);
        page.Items.Should().BeEmpty();
    }

    [Test]
    public async Task AsParameters_BindsInheritedListRequestFromQueryString()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/list", ([AsParameters] TestRequest request) =>
            new { request.PageOrDefault, request.PageSizeOrDefault, request.Sort, request.Search });
        await app.StartAsync();
        using var client = app.GetTestClient();

        var bare = await client.GetFromJsonAsync<BoundValues>("/list");
        var full = await client.GetFromJsonAsync<BoundValues>("/list?page=2&pageSize=500&sort=-name&search=abc");

        bare.Should().Be(new BoundValues(0, ListRequest.DefaultPageSize, null, null));
        full.Should().Be(new BoundValues(2, ListRequest.MaxPageSize, "-name", "abc"));
    }

    [Test]
    public async Task AsParameters_BindsRouteValueIntoListRequestProperty()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/owners/{ownerId:int}/list", ([AsParameters] OwnedRequest request) => new { request.OwnerId, request.PageOrDefault });
        await app.StartAsync();
        using var client = app.GetTestClient();

        var bound = await client.GetFromJsonAsync<OwnedValues>("/owners/42/list?page=3");

        bound.Should().Be(new OwnedValues(42, 3));
    }

    private sealed record Owner(string Name);

    private sealed record Row(int Id, string Name, int Score, Owner Owner);

    private sealed record KeyedRow(int RowKey, int Score);

    private sealed record BoundValues(int PageOrDefault, int PageSizeOrDefault, string? Sort, string? Search);

    private sealed record OwnedValues(int OwnerId, int PageOrDefault);

    private sealed record OwnedRequest : ListRequest
    {
        public int OwnerId { get; init; }
    }

    private sealed record TestRequest : ListRequest
    {
        public string? Search { get; init; }
    }
}
