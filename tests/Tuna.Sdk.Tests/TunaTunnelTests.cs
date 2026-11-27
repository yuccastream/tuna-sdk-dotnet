using System.Net.Http.Headers;
using System.Text;
using Tuna.Sdk;
using Xunit;

namespace Tuna.Sdk.Tests;

public class TunaTunnelTests
{
    [Theory]
    [InlineData("127.0.0.1:8080", "127.0.0.1", 8080)]
    [InlineData("localhost:3000", "localhost", 3000)]
    [InlineData("[::1]:9000", "::1", 9000)]
    public void ParsesLocalAddr(string addr, string host, int port)
    {
        Assert.Equal((host, port), TunaTunnel.ParseLocalAddr(addr));
    }

    [Theory]
    [InlineData("unix:///tmp/app.sock")]
    [InlineData("8080")]
    [InlineData("host:0")]
    [InlineData("host:http")]
    public void RejectsLocalAddr(string addr)
    {
        Assert.Throws<ArgumentException>(() => TunaTunnel.ParseLocalAddr(addr));
    }

    [Fact]
    public async Task StartRejectsUnixSocket()
    {
        var o = new TunaOptions { ApiKey = "k", Protocol = "http" };
        var e = await Assert.ThrowsAsync<ArgumentException>(() => TunaTunnel.StartAsync(o, "unix:///tmp/a.sock", TestContext.Current.CancellationToken));
        Assert.Contains("unix", e.Message);
    }

    [Fact]
    public async Task StartValidatesOptions()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => TunaTunnel.StartAsync(new TunaOptions { ApiKey = "k", Protocol = "udp" }, "127.0.0.1:1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => TunaTunnel.StartAsync(new TunaOptions { ApiKey = "k", Protocol = "http", ServerUrl = "ftp://x" }, "127.0.0.1:1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => TunaTunnel.StartAsync(new TunaOptions { ApiKey = "k", Protocol = "http", ClientName = "a\nb" }, "127.0.0.1:1", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(400, "BadRequest", false, true)]
    [InlineData(401, "Unauthorized", false, true)]
    [InlineData(402, "PaymentRequired", false, true)]
    [InlineData(403, "Forbidden", false, true)]
    [InlineData(404, "NotFound", false, true)]
    [InlineData(426, "UpgradeRequired", false, true)]
    [InlineData(503, "ServiceUnavailable", true, false)]
    [InlineData(500, "", true, false)]
    [InlineData(404, "", false, false)]
    [InlineData(404, "", true, true)]
    [InlineData(408, "", true, false)]
    [InlineData(429, "", true, false)]
    [InlineData(301, "", false, true)]
    public void ClassifiesServerErrors(int status, string code, bool beforeFirstConnect, bool permanent)
    {
        var e = new TunaServerException(status, "x", code) { BeforeFirstConnect = beforeFirstConnect };
        Assert.Equal(permanent, TunaTunnel.IsPermanent(e));
    }

    [Fact]
    public void TerminatedIsPermanent()
    {
        Assert.True(TunaTunnel.IsPermanent(new TunaTerminatedException("deleted")));
        Assert.False(TunaTunnel.IsPermanent(new KeepAliveTimeoutException()));
        Assert.False(TunaTunnel.IsPermanent(new IOException()));
        Assert.Equal("tunnel was deleted", new TunaTerminatedException("deleted").Message);
        Assert.Equal("tunnel terminated by server (other)", new TunaTerminatedException("other").Message);
        Assert.Contains("keepalive timeout", new KeepAliveTimeoutException().Message);
    }

    [Fact]
    public void ParsesServerErrorBody()
    {
        var body = Encoding.UTF8.GetBytes("""{"code":"Forbidden","error":"no scope","request_id":"r1"}""");
        var e = Api.ServerError(403, body, null, null);
        Assert.Equal("Forbidden", e.Code);
        Assert.Equal("r1", e.RequestId);
        Assert.Equal("no scope (request id r1)", e.Message);
    }

    [Fact]
    public void ServerErrorWithoutJson()
    {
        var e = Api.ServerError(404, Encoding.UTF8.GetBytes("<html>not found</html>"), null, null);
        Assert.Equal("server responded with status 404", e.Message);
        Assert.Equal("", e.Code);
    }

    [Fact]
    public void ServerErrorRedirect()
    {
        var e = Api.ServerError(301, [], "https://elsewhere.example", null);
        Assert.Contains("elsewhere.example", e.Message);
        Assert.True(TunaTunnel.IsPermanent(e));
    }

    [Fact]
    public void RetryAfterIsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), Api.ServerError(503, [], null, new RetryConditionHeaderValue(TimeSpan.FromSeconds(3))).RetryAfter);
        Assert.Equal(TimeSpan.FromMinutes(5), Api.ServerError(503, [], null, new RetryConditionHeaderValue(TimeSpan.FromHours(1))).RetryAfter);
    }

    [Fact]
    public void RetryDelayStaysInRange()
    {
        var seen = new HashSet<TimeSpan>();
        foreach (var backoff in new[] { 1, 2, 4, 8, 15, 15 })
        {
            for (var i = 0; i < 50; i++)
            {
                var d = TunaTunnel.RetryDelay(TimeSpan.FromSeconds(backoff), new IOException());
                Assert.InRange(d, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
                seen.Add(d);
            }
        }
        Assert.True(seen.Count > 10);
    }

    [Fact]
    public void RetryDelayHonorsRetryAfter()
    {
        var e = new TunaServerException(503, "x", retryAfter: TimeSpan.FromSeconds(120));
        Assert.Equal(TimeSpan.FromSeconds(120), TunaTunnel.RetryDelay(TimeSpan.FromSeconds(1), e));
    }

    [Fact]
    public void PublicUrlFallback()
    {
        Assert.Equal("https://x.tuna.am", new ConnectResponse { PublicUrl = "https://x.tuna.am", DomainName = "y" }.PublicUrlFor("http"));
        Assert.Equal("https://demo.tuna.am", new ConnectResponse { DomainName = "demo.tuna.am" }.PublicUrlFor("http"));
        Assert.Equal("tcp://ru.tuna.am:40001", new ConnectResponse { DomainName = "ru.tuna.am", EndpointPort = 40001 }.PublicUrlFor("tcp"));
    }

    [Fact]
    public void ParsesAuthorizedKey()
    {
        var blob = new byte[] { 0, 0, 0, 11, 1, 2, 3 };
        Assert.Equal(blob, TunaTunnel.ParseAuthorizedKey("ssh-ed25519 " + Convert.ToBase64String(blob) + " node"));
        Assert.Throws<FormatException>(() => TunaTunnel.ParseAuthorizedKey("garbage"));
    }

    [Fact]
    public void ConnectRequestAlwaysHasSessionId()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ConnectRequest { ClientVersion = 2 }, ApiJson.Default.ConnectRequest);
        Assert.Contains("\"tunnel_session_id\":\"\"", json);
        Assert.DoesNotContain("cidr_allow_list", json);
    }

    [Fact]
    public void ParsesNodeEvents()
    {
        var ev = System.Text.Json.JsonSerializer.Deserialize("""{"type":"session_expiring","expires_in":300,"extra":1}""", ApiJson.Default.NodeEvent);
        Assert.Equal("session_expiring", ev!.Type);
        Assert.Equal(300, ev.ExpiresIn);
    }
}
