using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExperienceManager.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Jellyfin.Plugin.ExperienceManager.Tests;

public class IndexPageMiddlewareTests
{
    private const string Index = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body></body></html>";
    private const string StockTag = "\"stock\"";

    private static readonly string Version = typeof(Plugin).Assembly.GetName().Version!.ToString();

    private static readonly string Tag = "<link rel=\"stylesheet\" href=\"../ExperienceManager/home.css?v=" + Version + "\">"
        + "<script async src=\"../ExperienceManager/home.js?v=" + Version + "\"></script>";

    // Stands in for Jellyfin's static files: honours the validators, compression and
    // HEAD the way the real pipeline does, so the middleware has to undo each one.
    private static Task StaticFiles(HttpContext context, string page)
    {
        var request = context.Request;
        var response = context.Response;
        response.ContentType = "text/html";
        response.Headers.ETag = StockTag;
        response.Headers.LastModified = "Wed, 01 Jan 2026 00:00:00 GMT";
        if (request.Headers.IfNoneMatch == StockTag || request.Headers.ContainsKey(HeaderNames.IfModifiedSince))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        var bytes = Encoding.UTF8.GetBytes(page);
        if (request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.Ordinal))
        {
            using var zipped = new MemoryStream();
            using (var gzip = new GZipStream(zipped, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(bytes);
            }

            bytes = zipped.ToArray();
            response.Headers.ContentEncoding = "gzip";
        }

        response.ContentLength = bytes.Length;
        return HttpMethods.IsHead(request.Method) ? Task.CompletedTask : response.Body.WriteAsync(bytes).AsTask();
    }

    private static async Task<(HttpContext Context, string Body)> Serve(string path, string method = "GET", Action<HttpRequest>? setUp = null, RequestDelegate? next = null, string page = Index, Func<string, string?>? change = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        setUp?.Invoke(context.Request);
        var body = new MemoryStream();
        context.Response.Body = body;

        await new IndexPageMiddleware(next ?? (c => StaticFiles(c, page)), NullLogger<IndexPageMiddleware>.Instance, change ?? IndexPageTags.AddTo).InvokeAsync(context);

        return (context, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Theory]
    [InlineData("/web/index.html")]
    [InlineData("/web/")]
    [InlineData("/jf/web/index.html")]
    [InlineData("/jf/web/")]
    public async Task TheIndexPageGetsTheTagUnderAnyBaseUrl(string path)
    {
        var (context, body) = await Serve(path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(Index.Replace("</head>", Tag + "</head>", StringComparison.Ordinal), body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), context.Response.ContentLength);
    }

    [Theory]
    [InlineData("/web/main.jellyfin.bundle.js")]
    [InlineData("/web/config.json")]
    [InlineData("/Users/Me")]
    [InlineData("/ExperienceManager/home.js")]
    public async Task OtherRequestsPassThroughUnchanged(string path)
    {
        var (context, body) = await Serve(path, setUp: r => r.Headers.AcceptEncoding = "gzip");

        Assert.Equal(StockTag, context.Response.Headers.ETag.ToString());
        Assert.Equal("gzip", context.Response.Headers.ContentEncoding.ToString());
        Assert.DoesNotContain(Tag, body);
    }

    [Fact]
    public async Task AClientThatAcceptsCompressionGetsThePlainChangedPage()
    {
        var (context, body) = await Serve("/web/index.html", setUp: r => r.Headers.AcceptEncoding = "gzip, br");

        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.ContentEncoding));
        Assert.Contains(Tag + "</head>", body);
    }

    [Fact]
    public async Task HeadAnswersWithTheHeadersOfGetAndNoBody()
    {
        var (get, _) = await Serve("/web/index.html");
        var (head, body) = await Serve("/web/index.html", "HEAD");

        Assert.Equal(string.Empty, body);
        Assert.Equal(StatusCodes.Status200OK, head.Response.StatusCode);
        Assert.Equal(get.Response.ContentLength, head.Response.ContentLength);
        Assert.Equal(get.Response.Headers.ETag.ToString(), head.Response.Headers.ETag.ToString());
        Assert.Equal("HEAD", head.Request.Method);
    }

    [Fact]
    public async Task TheETagDescribesTheChangedPageAndItsValidatorGets304()
    {
        var (first, body) = await Serve("/web/index.html");
        var etag = first.Response.Headers.ETag.ToString();

        Assert.Equal(IndexPageMiddleware.ETagOf(Encoding.UTF8.GetBytes(body)), etag);
        Assert.NotEqual(StockTag, etag);
        Assert.False(first.Response.Headers.ContainsKey(HeaderNames.LastModified));

        var (again, empty) = await Serve("/web/index.html", setUp: r => r.Headers.IfNoneMatch = etag);
        Assert.Equal(StatusCodes.Status304NotModified, again.Response.StatusCode);
        Assert.Equal(string.Empty, empty);
    }

    [Fact]
    public async Task TheStockPagesValidatorsNeverHideTheTag()
    {
        // A client that cached the page before the plugin was installed.
        var (context, body) = await Serve("/web/index.html", setUp: r =>
        {
            r.Headers.IfNoneMatch = StockTag;
            r.Headers.IfModifiedSince = "Wed, 01 Jan 2026 00:00:00 GMT";
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(Tag, body);
    }

    [Fact]
    public async Task APageAlreadyCarryingTheTagOrWithoutAHeadIsLeftAlone()
    {
        var changed = Index.Replace("</head>", Tag + "</head>", StringComparison.Ordinal);
        Assert.Equal(changed, (await Serve("/web/", page: changed)).Body);
        Assert.Equal("<html><body></body></html>", (await Serve("/web/", page: "<html><body></body></html>")).Body);
    }

    [Fact]
    public async Task ANonOkAnswerIsPassedOnAsItCame()
    {
        var (context, body) = await Serve("/web", next: c =>
        {
            c.Response.StatusCode = StatusCodes.Status302Found;
            c.Response.Headers.Location = "/web/";
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/web/", context.Response.Headers.Location.ToString());
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task APageSentCompressedAnywayIsPassedOnUnchanged()
    {
        var (context, body) = await Serve("/web/index.html", next: async c =>
        {
            c.Response.Headers.ContentEncoding = "br";
            await c.Response.WriteAsync("compressed bytes");
        });

        Assert.Equal("br", context.Response.Headers.ContentEncoding.ToString());
        Assert.Equal("compressed bytes", body);
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.ETag));
    }

    [Fact]
    public async Task AChangeThatFailsServesTheStockPage()
    {
        var (context, body) = await Serve("/web/index.html", change: _ => throw new InvalidOperationException("broken"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(Index, body);
        Assert.Equal(Encoding.UTF8.GetByteCount(Index), context.Response.ContentLength);
    }

    [Fact]
    public async Task AFailingChangeIsLoggedOnceNotOnEveryLoad()
    {
        var logger = new CountingLogger();
        var middleware = new IndexPageMiddleware(c => StaticFiles(c, Index), logger, _ => throw new InvalidOperationException("broken"));

        for (var i = 0; i < 3; i++)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/web/";
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context);
        }

        Assert.Equal(1, logger.Errors);
    }

    [Fact]
    public async Task TheStartupFilterPutsTheMiddlewareAheadOfTheRestOfThePipeline()
    {
        var app = new ApplicationBuilder(new ServiceCollection().AddLogging().BuildServiceProvider());

        // The rest of Jellyfin's pipeline, here just the static files, is added after the filter's middleware.
        new IndexPageStartupFilter(NullLogger<IndexPageStartupFilter>.Instance)
            .Configure(rest => rest.Run(context => StaticFiles(context, Index)))(app);

        var context = new DefaultHttpContext { RequestServices = app.ApplicationServices };
        context.Request.Method = "GET";
        context.Request.Path = "/web/";
        var body = new MemoryStream();
        context.Response.Body = body;
        await app.Build()(context);

        Assert.Contains(Tag + "</head>", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public void TheRegistratorRegistersTheStartupFilter()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.Contains(services, d => d.ServiceType == typeof(IStartupFilter) && d.ImplementationType == typeof(IndexPageStartupFilter));
    }

    private sealed class CountingLogger : ILogger<IndexPageMiddleware>
    {
        public int Errors { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors++;
            }
        }
    }
}
