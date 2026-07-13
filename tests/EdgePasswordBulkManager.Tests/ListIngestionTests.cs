using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EdgePasswordBulkManager.Models;
using EdgePasswordBulkManager.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdgePasswordBulkManager.Tests;

public sealed class ListIngestionTests
{
    [Fact]
    public async Task ImportAsync_RejectsOversizedContentAndCleansTemporaryFile()
    {
        using var fixture = new ServiceTestFixture();
        fixture.Options.MaxListBytes = 8;
        var service = CreateCategoryService(fixture);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("example.com"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportAsync("test", "domains.txt", content));

        Assert.Empty(Directory.EnumerateFiles(fixture.Options.ListDirectory, "*.tmp-*"));
        Assert.Empty(Directory.EnumerateFiles(fixture.Options.ListDirectory, "*.txt"));
    }

    [Fact]
    public async Task ImportAsync_ValidatesAndActivatesUploadedDomains()
    {
        using var fixture = new ServiceTestFixture();
        var service = CreateCategoryService(fixture);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("example.com\n"));

        var result = await service.ImportAsync("test", "domains.txt", content);

        Assert.Equal(1, result.domains);
        Assert.Contains("test", service.Categories);
        Assert.Contains("test", service.Match("login.example.com"));
    }

    [Fact]
    public async Task RefreshNowAsync_RejectsOverlappingRefreshes()
    {
        using var fixture = new ServiceTestFixture();
        fixture.Options.Categories.Add(new CategoryDefinition
        {
            Name = "test",
            Urls = { "https://lists.example/domains.txt" },
        });
        var categories = CreateCategoryService(fixture);
        var handler = new BlockingHandler("example.com\n");
        var service = new ListRefreshService(
            Microsoft.Extensions.Options.Options.Create(fixture.Options),
            categories,
            new TestHttpClientFactory(handler),
            fixture.Audit,
            NullLogger<ListRefreshService>.Instance);

        var first = service.RefreshNowAsync(force: true, CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var overlapping = await service.RefreshNowAsync(force: true, CancellationToken.None);
        handler.Release.TrySetResult();
        var completed = await first;

        Assert.Equal("already refreshing", overlapping);
        Assert.Contains("1 downloaded", completed);
        Assert.Equal(1, categories.CountFor("test"));
    }

    private static CategoryService CreateCategoryService(ServiceTestFixture fixture) => new(
        Microsoft.Extensions.Options.Options.Create(fixture.Options),
        fixture.Audit,
        NullLogger<CategoryService>.Instance);

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class BlockingHandler(string body) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            };
        }
    }
}