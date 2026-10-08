using System.Text.Json;
using Umbraco.Cms.Core;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSearcherHardeningTests
{
    private const string IndexAlias = "unit-test-index";

    [Test]
    public async Task AQuoteInTheCultureCannotEndTheClause()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test", culture: "x') or true or ('a' eq 'a");

        Assert.That(service.LastFilter(), Does.Contain("x'') or true or (''a'' eq ''a"));
    }

    [Test]
    public async Task AQuoteInTheSegmentCannotEndTheClause()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test", segment: "x') or true or ('a' eq 'a");

        Assert.That(service.LastFilter(), Does.Contain("x'') or true or (''a'' eq ''a"));
    }

    // A filter on a faceted field moves to a second request for the facet counts; it must be protected too.
    [Test]
    public async Task TheFacetCountRequestIsAccessFilteredToo()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "test",
            filters: [new KeywordFilter("genre", ["rock"], false)],
            facets: [new KeywordFacet("genre")]);

        var bodies = service.Requests
            .Where(r => r.Path.Contains("search.post.search"))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement)
            .ToArray();
        var filters = bodies.Select(body => body.TryGetProperty("filter", out var filter) ? filter.GetString() : null).ToArray();
        Assert.That(bodies, Has.Length.EqualTo(2));
        Assert.That(filters, Has.All.Contain("accessKeys/any("));
        // The second request is the facet count one: no filter on the faceted field, facets requested.
        Assert.That(filters[0], Does.Contain("genre_keywords"));
        Assert.That(filters[1], Does.Not.Contain("genre_keywords"));
        Assert.That(bodies[1].TryGetProperty("facets", out _), Is.True);
    }

    [Test]
    public async Task ADecimalBeyondInt64IsADoubleLiteral()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "test",
            filters: [new DecimalExactFilter("price", [12345678901234567890m], false), DecimalRangeFilter.Single("weight", null, 12345678901234567890m, false)]);

        Assert.That(service.LastFilter(), Does.Contain("price_decimals/any(f: f eq 12345678901234567890.0)"));
        Assert.That(service.LastFilter(), Does.Contain("weight_decimals/any(f: f lt 12345678901234567890.0)"));
    }

    [Test]
    public async Task ANegativeDecimalIsADoubleLiteral()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test", filters: [new DecimalExactFilter("delta", [-5m, -0.25m], false)]);

        Assert.That(service.LastFilter(), Does.Contain("delta_decimals/any(f: f eq -5.0) or delta_decimals/any(f: f eq -0.25)"));
    }

    [Test]
    public async Task SortersStopShortOfAzuresLimitToLeaveRoomForTheTieBreaker()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "test",
            sorters: Enumerable.Range(0, 40).Select(i => (Sorter)new IntegerSorter($"field{i}", Direction.Ascending)).ToArray());

        var clauses = service.LastOrderBy()!.Split(',');
        Assert.That(clauses, Has.Length.EqualTo(32));
        Assert.That(clauses[30], Does.StartWith("field30_"));
        Assert.That(clauses.Last(), Is.EqualTo("id asc"));
    }

    [Test]
    public async Task ThirtyOneSortersAreAllKept()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "test",
            sorters: Enumerable.Range(0, 31).Select(i => (Sorter)new IntegerSorter($"field{i}", Direction.Ascending)).ToArray());

        var clauses = service.LastOrderBy()!.Split(',');
        Assert.That(clauses, Has.Length.EqualTo(32));
        Assert.That(clauses[30], Does.StartWith("field30_"));
    }

    // Field names are not quoted in the OData text, so a caller passing one through from a request must not be able
    // to end a clause with it.
    [Test]
    public void AFieldNameThatIsNotAnIdentifierIsRefused()
    {
        var searcher = new FakeSearchService().CreateSearcher();
        const string injected = "x_keywords/any() or true or y";

        Assert.ThrowsAsync<ArgumentException>(() => searcher.SearchAsync(IndexAlias, query: "test", filters: [new KeywordFilter(injected, ["v"], false)]));
        Assert.ThrowsAsync<ArgumentException>(() => searcher.SearchAsync(IndexAlias, query: "test", sorters: [new KeywordSorter(injected, Direction.Ascending)]));
        Assert.ThrowsAsync<ArgumentException>(() => searcher.SearchAsync(IndexAlias, query: "test", facets: [new KeywordFacet(injected)]));
    }

    [Test]
    public async Task IdentifierFieldNamesAreAccepted()
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(
            IndexAlias,
            query: "test",
            filters: [new KeywordFilter("_private9", ["v"], false)],
            sorters: [new KeywordSorter("Name_2", Direction.Ascending), new ScoreSorter(Direction.Descending)]);

        Assert.That(service.LastFilter(), Does.Contain("_private9_keywords"));
        Assert.That(service.LastOrderBy(), Does.Contain("Name_2_"));
    }
}
