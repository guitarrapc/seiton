using System.Net;
using Seiton.Core.Linting.Http;

namespace Seiton.Core.Tests;

public sealed class SameOriginRedirectHandlerTests
{
    [Test]
    public async Task SendAsync_FollowsRedirect_WhenLocationSameOrigin()
    {
        var visits = new List<string>();
        var inner = new VisitCountingHandler((req, _) =>
        {
            visits.Add(req.RequestUri!.AbsolutePath);
            if (req.RequestUri.AbsolutePath == "/start")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                r.Headers.Location = new Uri("https://api.github.com/final");
                return Task.FromResult(r);
            }

            if (req.RequestUri.AbsolutePath == "/final")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });

        var outer = new SameOriginRedirectHandler { InnerHandler = inner };
        using var client = new HttpClient(outer);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/start");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "dummy");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(visits.Count).IsEqualTo(2);
        await Assert.That(visits[0]).IsEqualTo("/start");
        await Assert.That(visits[1]).IsEqualTo("/final");
    }

    [Test]
    public async Task SendAsync_DoesNotFollowRedirect_WhenLocationDifferentOrigin()
    {
        var visits = new List<string>();
        var inner = new VisitCountingHandler((req, _) =>
        {
            visits.Add(req.RequestUri!.AbsoluteUri);
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example/leak");
            return Task.FromResult(r);
        });

        var outer = new SameOriginRedirectHandler { InnerHandler = inner };
        using var client = new HttpClient(outer);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/start");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "dummy");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(visits.Count).IsEqualTo(1);
        await Assert.That(response.Headers.Location!.Host).IsEqualTo("evil.example");
    }

    private sealed class VisitCountingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => impl(request, cancellationToken);
    }
}
