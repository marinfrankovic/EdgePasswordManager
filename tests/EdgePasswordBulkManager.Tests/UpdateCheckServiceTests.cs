using System.Net;
using System.Text;
using EdgePasswordBulkManager.Services;

namespace EdgePasswordBulkManager.Tests;

public sealed class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("v9.8.7", "9.8.7")]
    [InlineData("v9.8.7-beta.1", "9.8.7")]
    [InlineData("v9.8.7+build", "9.8.7")]
    public async Task CheckAsync_NormalizesReleaseTags(string tag, string expected)
    {
        var json = $$"""{"tag_name":"{{tag}}"}""";
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK, json));
        var service = new UpdateCheckService(client);

        var result = await service.CheckAsync();

        Assert.Equal(expected, result.LatestVersion);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_ReturnsLatestReleaseInformation()
    {
        const string json = """
            {
              "tag_name": "v9.8.7",
              "name": "Version 9.8.7",
              "body": "Release notes",
              "html_url": "https://github.com/example/releases/tag/v9.8.7"
            }
            """;
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK, json));
        var service = new UpdateCheckService(client);

        var result = await service.CheckAsync();

        Assert.Equal("9.8.7", result.LatestVersion);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("Release notes", result.ReleaseNotes);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_HandlesRepositoryWithoutReleases()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.NotFound, "{}"));
        var service = new UpdateCheckService(client);

        var result = await service.CheckAsync();

        Assert.False(result.UpdateAvailable);
        Assert.Equal("No published release is available yet.", result.Error);
    }

    [Fact]
    public async Task CheckAsync_HandlesInvalidGitHubResponse()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK, "not-json"));
        var service = new UpdateCheckService(client);

        var result = await service.CheckAsync();

        Assert.False(result.UpdateAvailable);
        Assert.Equal("GitHub returned an invalid update response.", result.Error);
    }

    private sealed class ResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}