using System.Net;
using System.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MdModManager.Services;

public sealed class EuterpeTokenQueryHandler : DelegatingHandler
{
    private static readonly Uri DownloadBaseUri = new("https://dl.euterpe-org.com/files/");
    private readonly IServiceProvider _services;

    public EuterpeTokenQueryHandler(IServiceProvider services) : base(new HttpClientHandler())
    {
        _services = services;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EuterpeRateLimitGate.ThrowIfBlocked();

        if (request.RequestUri == null || !DownloadBaseUri.IsBaseOf(request.RequestUri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var authService = _services.GetRequiredService<IAuthService>();
        var token = await authService.GetAccessTokenAsync().ConfigureAwait(false);
        request.RequestUri = AppendToken(request.RequestUri, token);

        // Euterpe 谱面下载的回退次数由下载服务统一控制，401 也不能在此隐式补发请求。
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static Uri AppendToken(Uri uri, string token)
    {
        var builder = new UriBuilder(uri);
        var query = HttpUtility.ParseQueryString(builder.Query);
        query.Set("t", token);
        builder.Query = query.ToString();
        return builder.Uri;
    }
}
