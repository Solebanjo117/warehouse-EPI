using System.Net;
using WarehouseEPI.Infrastructure.Imports;

namespace WarehouseEPI.Tests.Imports;

public sealed class InternalInventoryClientTests
{
    private const string Protected = "<form class='post-password-form' method='post' action='https://www.epindustrial.com/wp-login.php?action=postpass'><input name='post_password'></form>";
    private const string Unlocked = "<iframe src='https://extrapackaging.ws/inventory.php'></iframe>";

    [Fact]
    public async Task Authenticates_each_call_and_never_sends_password_or_cookies_to_viewer()
    {
        var creations = 0;
        var transport = new FakeTransport(() =>
        {
            creations++;
            var step = 0;
            return new Handler(async request =>
            {
                step++;
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                Assert.DoesNotContain("test-secret", request.RequestUri!.AbsoluteUri);
                switch (step)
                {
                    case 1:
                        Assert.False(request.Headers.Contains("Cookie"));
                        return Html(Protected);
                    case 2:
                        Assert.Equal(HttpMethod.Post, request.Method);
                        Assert.Equal("www.epindustrial.com", request.RequestUri.Host);
                        Assert.Contains("post_password=test-secret", body);
                        var response = Redirect("/internal-inventory/");
                        response.Headers.Add("Set-Cookie", "wp-postpass=test-session; Path=/; Secure");
                        return response;
                    case 3:
                    case 4:
                        Assert.Equal(HttpMethod.Get, request.Method);
                        Assert.Contains("wp-postpass=test-session", request.Headers.GetValues("Cookie"));
                        return Html(Unlocked);
                    case 5:
                        Assert.Equal("extrapackaging.ws", request.RequestUri.Host);
                        Assert.False(request.Headers.Contains("Cookie"));
                        Assert.Empty(body);
                        return Html(InternalInventoryReaderTests.Html(InternalInventoryReaderTests.Row("GROUP:PART", "Each")));
                    default: throw new InvalidOperationException();
                }
            });
        });
        var client = new InternalInventoryClient(transport);
        for (var i = 0; i < 2; i++) Assert.Equal("PART", Assert.Single((await client.ReadAsync("test-secret")).Rows).Sku);
        Assert.Equal(2, creations);
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("http://www.epindustrial.com/internal-inventory/")]
    [InlineData("https://www.epindustrial.com:8443/internal-inventory/")]
    [InlineData("https://user@www.epindustrial.com/internal-inventory/")]
    public async Task Rejects_redirect_destinations_before_sending_another_request(string location)
    {
        var count = 0;
        var client = Client(request => { count++; return Task.FromResult(Redirect(location)); });
        var error = await Assert.ThrowsAsync<InternalInventoryException>(() => client.ReadAsync("test-secret"));
        Assert.Equal(InternalInventoryError.Structure, error.Error);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("password", InternalInventoryError.Password)]
    [InlineData("frame", InternalInventoryError.Structure)]
    [InlineData("replay", InternalInventoryError.Structure)]
    [InlineData("too-large", InternalInventoryError.TooLarge)]
    [InlineData("timeout", InternalInventoryError.Timeout)]
    [InlineData("connection", InternalInventoryError.Connection)]
    public async Task Reports_sanitized_errors(string scenario, InternalInventoryError expected)
    {
        var step = 0;
        var client = Client(request =>
        {
            step++;
            if (scenario == "timeout") throw new TaskCanceledException("secret response");
            if (scenario == "connection") throw new HttpRequestException("secret response");
            if (scenario == "too-large") return Task.FromResult(Html(new string('x', InternalInventoryClient.MaxResponseBytes + 1)));
            if (step == 1) return Task.FromResult(Html(Protected));
            if (scenario == "replay") return Task.FromResult(Redirect("/unexpected", HttpStatusCode.TemporaryRedirect));
            if (step == 2) return Task.FromResult(Html("OK"));
            return Task.FromResult(Html(scenario == "password" ? Protected : "<iframe src='https://evil.test/data'></iframe>"));
        });
        var error = await Assert.ThrowsAsync<InternalInventoryException>(() => client.ReadAsync("test-secret"));
        Assert.Equal(expected, error.Error);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task Rejects_a_changed_login_action_without_posting_password()
    {
        var calls = 0;
        var client = Client(_ =>
        {
            calls++;
            return Task.FromResult(Html(Protected.Replace("www.epindustrial.com", "extrapackaging.ws", StringComparison.Ordinal)));
        });
        var error = await Assert.ThrowsAsync<InternalInventoryException>(() => client.ReadAsync("test-secret"));
        Assert.Equal(InternalInventoryError.Structure, error.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Streaming_response_is_limited_even_without_content_length()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnboundedContent()
        }));
        var error = await Assert.ThrowsAsync<InternalInventoryException>(() => client.ReadAsync("test-secret"));
        Assert.Equal(InternalInventoryError.TooLarge, error.Error);
    }

    private sealed class UnboundedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[InternalInventoryClient.MaxResponseBytes + 1]).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(new byte[InternalInventoryClient.MaxResponseBytes + 1]));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reclassified_as_timeout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = Client(_ => throw new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync("test-secret", cancellation.Token));
    }

    private static InternalInventoryClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new FakeTransport(() => new Handler(respond)));
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };
    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class FakeTransport(Func<HttpMessageHandler> create) : InternalInventoryTransportFactory
    {
        public override HttpClient CreateClient() => new(create());
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
