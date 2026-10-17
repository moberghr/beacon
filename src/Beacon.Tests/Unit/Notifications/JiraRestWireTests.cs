using System.Net;
using System.Text;
using Beacon.Core.Adapters.Jira;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// The exact requests the Jira adapter puts on the wire (method, URL, headers, body bytes) and how it reads the canned
/// answers, recorded through a stub handler.
/// </summary>
[TestFixture]
public class JiraRestWireTests
{
    private const string Recipient = "https://acme.atlassian.net;SEC;svc@acme.eu;token";

    private const string SearchAnswer = """
        {"startAt":0,"maxResults":50,"total":2,"issues":[
        {"id":"1","key":"SEC-1","self":"https://acme.atlassian.net/rest/api/3/issue/1","fields":{"summary":"First","description":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello "},{"type":"text","text":"world"}]}]},"status":{"id":"3","name":"In Progress"},"reporter":{"accountId":"a1","displayName":"Svc","emailAddress":"svc@acme.eu"},"created":"2026-01-02T03:04:05.000+0000","updated":"2026-02-03T04:05:06.000+0000"}},
        {"id":"2","key":"SEC-2","self":"https://acme.atlassian.net/rest/api/3/issue/2","fields":{"summary":"Second","description":null,"status":{"id":"1","name":"To Do"},"reporter":null,"created":"2026-03-04T05:06:07.000+0000","updated":"2026-03-05T05:06:07.000+0000","unknown":{"x":1}}}]}
        """;

    private StubHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new StubHandler();
    }

    [Test]
    public async Task SearchTickets_SendsTheQueryAndMapsTheAnswer()
    {
        _handler.Answer(HttpStatusCode.OK, SearchAnswer);

        var tickets = await Adapter().SearchTickets(new JiraCredentials(Recipient), "project = SEC AND text ~ \"a&b c\"", 5, CancellationToken.None);

        var request = _handler.Requests.Should().ContainSingle().Which;
        request.Method.Should().Be("GET");
        request.Url.Should().Be("https://acme.atlassian.net/rest/api/3/search/jql?jql=project%20%3D%20SEC%20AND%20text%20~%20%22a%26b%20c%22&maxResults=5&startAt=0&fields=key%2Csummary%2Cdescription%2Cstatus%2Creporter%2Ccreated%2Cupdated");
        request.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("svc@acme.eu:token")));
        request.Body.Should().BeNull();

        tickets.Should().HaveCount(2);
        tickets[0].Key.Should().Be("SEC-1");
        tickets[0].Summary.Should().Be("First");
        tickets[0].Description.Should().Be("Hello world");
        tickets[0].Status.Should().Be("In Progress");
        tickets[0].Reporter.Should().Be("svc@acme.eu");
        tickets[0].Created.Should().Be(DateTime.Parse("2026-01-02T03:04:05.000+0000"));
        tickets[0].Updated.Should().Be(DateTime.Parse("2026-02-03T04:05:06.000+0000"));
        tickets[1].Description.Should().BeEmpty();
        tickets[1].Reporter.Should().BeNull();
    }

    [TestCase("https://jira.acme.eu/jira")]
    [TestCase("https://jira.acme.eu/jira/")]
    public async Task SearchTickets_UnderAContextPath_KeepsThePath(string domainUrl)
    {
        _handler.Answer(HttpStatusCode.OK, """{"startAt":0,"maxResults":5,"total":0,"issues":[]}""");

        await Adapter().SearchTickets(new JiraCredentials($"{domainUrl};SEC;svc@acme.eu;token"), "project = SEC", 5, CancellationToken.None);

        _handler.Requests.Should().ContainSingle().Which.Url.Should().StartWith("https://jira.acme.eu/jira/rest/api/3/search/jql?jql=project%20%3D%20SEC&maxResults=5");
    }

    [Test]
    public async Task CreateWorkItem_WithNoExistingIssue_SearchesThenPostsTheIssue()
    {
        _handler.Answer(HttpStatusCode.OK, """{"startAt":0,"maxResults":10,"total":0,"issues":[]}""");
        _handler.Answer(HttpStatusCode.Created, """{"id":"10","key":"SEC-10","self":"https://acme.atlassian.net/rest/api/3/issue/10"}""");
        var description = new AdfBuilder().AddParagraph("Rows \"found\"").Build();

        var result = await Adapter().CreateWorkItem(new JiraCredentials(Recipient), "session-1", "BEACON: Failed", description, "Task", CancellationToken.None, "beacon", "High", "SEC-5");

        result.Should().Be(new CreateWorkItemResult("SEC-10", "https://acme.atlassian.net/browse/SEC-10", true));
        _handler.Requests.Should().HaveCount(2);
        _handler.Requests[0].Method.Should().Be("GET");
        _handler.Requests[0].Url.Should().StartWith("https://acme.atlassian.net/rest/api/3/search/jql?jql=").And.EndWith("&maxResults=10&startAt=0&fields=key%2Csummary");
        var create = _handler.Requests[1];
        create.Method.Should().Be("POST");
        create.Url.Should().Be("https://acme.atlassian.net/rest/api/3/issue");
        create.Authorization.Should().StartWith("Basic ");
        create.ContentType.Should().Be("application/json; charset=utf-8");
        create.Body.Should().Be("""{"fields":{"project":{"key":"SEC"},"summary":"BEACON: Failed","description":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Rows <Q>found<Q>"}]}]},"issuetype":{"name":"Task"},"assignee":null,"labels":["beacon"],"priority":{"name":"High"},"parent":{"key":"SEC-5"}}}""".Replace("<Q>", "\\" + "u0022"));
    }

    [Test]
    public async Task CreateWorkItem_WithAnExistingIssue_CommentsOnIt()
    {
        _handler.Answer(HttpStatusCode.OK, """{"startAt":0,"maxResults":10,"total":1,"issues":[{"id":"7","key":"SEC-7","self":"s","fields":{"summary":"BEACON: Failed","status":{"name":"To Do"}}}]}""");
        _handler.Answer(HttpStatusCode.Created, """{"id":"c1","author":{"accountId":"a","displayName":"d"},"body":{"type":"doc"},"created":"x","updated":"y"}""");

        var result = await Adapter().CreateWorkItem(new JiraCredentials(Recipient), "session-1", "BEACON: Failed", new AdfBuilder().AddParagraph("Again").Build(), "Task", CancellationToken.None);

        result.Should().Be(new CreateWorkItemResult("SEC-7", "https://acme.atlassian.net/browse/SEC-7", false));
        var comment = _handler.Requests[1];
        comment.Method.Should().Be("POST");
        comment.Url.Should().Be("https://acme.atlassian.net/rest/api/3/issue/SEC-7/comment");
        comment.ContentType.Should().Be("application/json; charset=utf-8");
        comment.Body.Should().Be("""{"body":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Again"}]}]}}""");
    }

    [Test]
    public async Task TransitionIssue_ReadsTheTransitionsThenPostsTheMatchingId()
    {
        _handler.Answer(HttpStatusCode.OK, """{"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress"}},{"id":"31","name":"Done","to":{"id":"3","name":"Done"}}]}""");
        _handler.Answer(HttpStatusCode.NoContent, "");

        await Adapter().TransitionIssue(new JiraCredentials(Recipient), "SEC-1", "done", CancellationToken.None);

        _handler.Requests.Should().HaveCount(2);
        _handler.Requests[0].Method.Should().Be("GET");
        _handler.Requests[0].Url.Should().Be("https://acme.atlassian.net/rest/api/3/issue/SEC-1/transitions");
        _handler.Requests[1].Method.Should().Be("POST");
        _handler.Requests[1].Url.Should().Be("https://acme.atlassian.net/rest/api/3/issue/SEC-1/transitions");
        _handler.Requests[1].ContentType.Should().Be("application/json; charset=utf-8");
        _handler.Requests[1].Body.Should().Be("""{"transition":{"id":"31"}}""");
    }

    [Test]
    public async Task TransitionIssue_WithAnUnknownName_PostsNothing()
    {
        _handler.Answer(HttpStatusCode.OK, """{"transitions":[{"id":"11","name":"Start"}]}""");

        await Adapter().TransitionIssue(new JiraCredentials(Recipient), "SEC-1", "Done", CancellationToken.None);

        _handler.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task CreateIssueRejected_CarriesTheStatusOnly()
    {
        _handler.Answer(HttpStatusCode.OK, """{"startAt":0,"maxResults":10,"total":0,"issues":[]}""");
        _handler.Answer(HttpStatusCode.BadRequest, """{"errors":{"project":"secret-remote-text"}}""");

        var act = () => Adapter().CreateWorkItem(new JiraCredentials(Recipient), "s", "t", new AdfDocument(), "Task", CancellationToken.None);

        var failure = await act.Should().ThrowAsync<Beacon.Core.Notifications.NotificationDeliveryException>();
        failure.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        failure.Which.Message.Should().NotContain("secret-remote-text");
    }

    [Test]
    public async Task SearchRejected_MapsToTheStatusClass()
    {
        _handler.Answer(HttpStatusCode.Unauthorized, "denied");

        var act = () => Adapter().SearchTickets(new JiraCredentials(Recipient), "jql", 5, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        Beacon.Core.Notifications.NotificationFailureReasons.For(thrown.Which).Should().Be("Notification delivery failed: the destination returned HTTP 4xx.");
        ((int?)Beacon.Core.Notifications.NotificationFailureReasons.StatusOf(thrown.Which)).Should().Be(401);
    }

    [TestCase("123")]
    [TestCase("true")]
    [TestCase("plain text")]
    public async Task SearchTickets_StringDescription_StaysTheString(string description)
    {
        _handler.Answer(HttpStatusCode.OK, SearchWithDescription($"\"{description}\""));

        var tickets = await Adapter().SearchTickets(new JiraCredentials(Recipient), "jql", 5, CancellationToken.None);

        tickets.Should().ContainSingle().Which.Description.Should().Be(description);
    }

    [Test]
    public async Task SearchTickets_NumberAsAString_IsRejectedAsUnreadable()
    {
        // Numbers are read strictly, as the client has always read them: a quoted number is an unreadable answer.
        _handler.Answer(HttpStatusCode.OK, """{"startAt":0,"maxResults":5,"total":1,"issues":[{"id":"1","key":"A-1","self":"s","fields":{"summary":"x","status":{"name":"T"},"attachment":[{"id":"10001","filename":"f","created":"c","size":12}]}}]}""");

        var act = () => Adapter().SearchTickets(new JiraCredentials(Recipient), "jql", 5, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task SearchTickets_JsonServedAsAnotherContentType_IsStillRead()
    {
        _handler.Answer(HttpStatusCode.OK, SearchAnswer, "text/html");

        var tickets = await Adapter().SearchTickets(new JiraCredentials(Recipient), "jql", 5, CancellationToken.None);

        tickets.Should().HaveCount(2);
    }

    [TestCase("<html>login</html>", "text/html")]
    [TestCase("", "application/json")]
    [TestCase("null", "application/json")]
    [TestCase("{not json", "application/json")]
    public async Task SearchTickets_UnreadableSuccess_CarriesTheStatus(string body, string contentType)
    {
        _handler.Answer(HttpStatusCode.OK, body, contentType);

        var act = () => Adapter().SearchTickets(new JiraCredentials(Recipient), "jql", 5, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.OK);
        thrown.Which.Message.Should().NotContain("login");
        Beacon.Core.Notifications.NotificationFailureReasons.For(thrown.Which).Should().Be("Notification delivery failed: the destination returned HTTP 2xx.");
        ((int?)Beacon.Core.Notifications.NotificationFailureReasons.StatusOf(thrown.Which)).Should().Be(200);
    }

    [Test]
    public async Task AddComment_EmptyCreatedAnswer_CarriesTheStatus()
    {
        _handler.Answer(HttpStatusCode.Created, "");

        var act = () => Adapter().AddCommentToTicket(new JiraCredentials(Recipient), "SEC-1", new AdfDocument(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        ((int?)Beacon.Core.Notifications.NotificationFailureReasons.StatusOf(thrown.Which)).Should().Be(201);
    }

    [Test]
    public async Task Search_ServiceUnavailable_MapsTo5xx()
    {
        _handler.Answer(HttpStatusCode.ServiceUnavailable, "oops", "text/html");

        var act = () => Adapter().SearchTickets(new JiraCredentials(Recipient), "jql", 5, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        Beacon.Core.Notifications.NotificationFailureReasons.For(thrown.Which).Should().Be("Notification delivery failed: the destination returned HTTP 5xx.");
        ((int?)Beacon.Core.Notifications.NotificationFailureReasons.StatusOf(thrown.Which)).Should().Be(503);
    }

    [Test]
    public async Task Transitions_ServiceUnavailable_MapsTo5xx()
    {
        _handler.Answer(HttpStatusCode.ServiceUnavailable, "oops", "text/html");

        var act = () => Adapter().TransitionIssue(new JiraCredentials(Recipient), "SEC-1", "Done", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        Beacon.Core.Notifications.NotificationFailureReasons.For(thrown.Which).Should().Be("Notification delivery failed: the destination returned HTTP 5xx.");
        ((int?)Beacon.Core.Notifications.NotificationFailureReasons.StatusOf(thrown.Which)).Should().Be(503);
    }

    [Test]
    public async Task Search_RedirectAnswer_MapsTo3xx()
    {
        // This stub has no handler chain, so it shows only the failure mapping of a 302 answer; that the named client
        // does not follow redirects is covered by NotificationHttpClientTests.
        _handler.Answer(HttpStatusCode.Found, "");

        var act = () => Adapter().SearchTickets(new JiraCredentials(Recipient), "jql", 5, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        Beacon.Core.Notifications.NotificationFailureReasons.For(thrown.Which).Should().Be("Notification delivery failed: the destination returned HTTP 3xx.");
        ((int?)Beacon.Core.Notifications.NotificationFailureReasons.StatusOf(thrown.Which)).Should().Be(302);
    }

    private static string SearchWithDescription(string descriptionJson)
    {
        return "{\"startAt\":0,\"maxResults\":5,\"total\":1,\"issues\":[{\"id\":\"1\",\"key\":\"A-1\",\"self\":\"s\",\"fields\":{\"summary\":\"x\",\"description\":" + descriptionJson + ",\"status\":{\"name\":\"T\"}}}]}";
    }

    private JiraApiAdapter Adapter()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_handler, disposeHandler: false));

        return new JiraApiAdapter(new JiraRestClientFactory(factory.Object), NullLogger<JiraApiAdapter>.Instance);
    }

    private sealed record Recorded(string Method, string Url, string? Authorization, string? ContentType, string? Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string ContentType)> _answers = new();

        public List<Recorded> Requests { get; } = [];

        public void Answer(HttpStatusCode status, string body, string contentType = "application/json")
        {
            _answers.Enqueue((status, body, contentType));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                body));

            var (status, answer, contentType) = _answers.Dequeue();
            return new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(answer, Encoding.UTF8, contentType),
            };
        }
    }
}
