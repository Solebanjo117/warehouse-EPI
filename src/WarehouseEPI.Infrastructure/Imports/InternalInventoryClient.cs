using System.Net;
using System.Text;
using AngleSharp.Html.Parser;

namespace WarehouseEPI.Infrastructure.Imports;

public interface IInternalInventoryClient
{
    Task<ProductSpreadsheetReadResult> ReadAsync(string password, CancellationToken cancellationToken = default);
}

// A new transport and cookie jar per operation; no pooled authentication state or HTTP body logging.
public class InternalInventoryTransportFactory
{
    public virtual HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan };
}

public sealed class InternalInventoryClient(InternalInventoryTransportFactory transports) : IInternalInventoryClient
{
    public static readonly Uri PageUri = new("https://www.epindustrial.com/internal-inventory/");
    private static readonly Uri PasswordUri = new("https://www.epindustrial.com/wp-login.php?action=postpass");
    private const string ViewerHost = "extrapackaging.ws";
    public const int MaxResponseBytes = 10 * 1024 * 1024;

    public async Task<ProductSpreadsheetReadResult> ReadAsync(string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new InternalInventoryException(InternalInventoryError.Password);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = transports.CreateClient();
        var cookies = new CookieContainer();
        try
        {
            // Always authenticate this request, even if the embed can otherwise be read directly.
            var html = await FetchAsync(client, PageUri, cookies, null, deadline.Token);
            using (var document = new HtmlParser().ParseDocument(html))
            {
                var form = document.QuerySelector("form.post-password-form");
                if (form is null || !Uri.TryCreate(PageUri, form.GetAttribute("action"), out var action) || action != PasswordUri ||
                    !string.Equals(form.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase) ||
                    form.QuerySelector("input[name=post_password]") is null)
                    throw new InternalInventoryException(InternalInventoryError.Structure);
            }
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["post_password"] = password, ["redirect_to"] = PageUri.AbsoluteUri
            });
            _ = await FetchAsync(client, PasswordUri, cookies, body, deadline.Token);
            html = await FetchAsync(client, PageUri, cookies, null, deadline.Token);
            using var unlocked = new HtmlParser().ParseDocument(html);
            if (unlocked.QuerySelector("form.post-password-form") is not null)
                throw new InternalInventoryException(InternalInventoryError.Password);
            var frames = unlocked.QuerySelectorAll("iframe");
            if (frames.Length != 1 || !Uri.TryCreate(PageUri, frames[0].GetAttribute("src"), out var viewer) || !Allowed(viewer, ViewerHost))
                throw new InternalInventoryException(InternalInventoryError.Structure);
            // Never send WordPress cookies or password to the embed host.
            var inventory = await FetchAsync(client, viewer, null, null, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            var result = InternalInventoryReader.Read(inventory);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InternalInventoryException(InternalInventoryError.Timeout); }
        catch (HttpRequestException) { throw new InternalInventoryException(InternalInventoryError.Connection); }
        catch (IOException) { throw new InternalInventoryException(InternalInventoryError.Connection); }
        catch (CookieException) { throw new InternalInventoryException(InternalInventoryError.Structure); }
    }

    private static bool Allowed(Uri uri, string host) => uri.Scheme == Uri.UriSchemeHttps && uri.Host == host &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static async Task<string> FetchAsync(HttpClient client, Uri uri, CookieContainer? cookies,
        HttpContent? body, CancellationToken cancellationToken)
    {
        var host = cookies is null ? ViewerHost : PageUri.Host;
        for (var redirects = 0; redirects <= 4; redirects++)
        {
            if (!Allowed(uri, host)) throw new InternalInventoryException(InternalInventoryError.Structure);
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
            request.Content = body;
            if (cookies is not null)
            {
                var cookie = cookies.GetCookieHeader(uri);
                if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (cookies is not null && response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) cookies.SetCookies(uri, value);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                // Do not replay credentials on redirects, even to another path at the same host.
                if (body is not null && (int)response.StatusCode is 307 or 308)
                    throw new InternalInventoryException(InternalInventoryError.Structure);
                if (response.Headers.Location is not { } location)
                    throw new InternalInventoryException(InternalInventoryError.Structure);
                uri = new Uri(uri, location);
                body = null;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InternalInventoryException(InternalInventoryError.Connection);
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InternalInventoryException(InternalInventoryError.TooLarge);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) throw new InternalInventoryException(InternalInventoryError.TooLarge);
                buffer.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        throw new InternalInventoryException(InternalInventoryError.Structure);
    }
}
