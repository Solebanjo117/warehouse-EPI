using System.Text.RegularExpressions;

namespace WarehouseEPI.Tests.Web;

public sealed class ContentSecurityPolicyContractTests
{
    // La política es `script-src 'self'` sin 'unsafe-inline': cualquier handler escrito como
    // atributo (onclick, onchange, onsubmit…) o un href "javascript:" queda bloqueado por el
    // navegador y el control deja de responder sin error visible.
    private static readonly Regex InlineHandler = new("\\son[a-z]+\\s*=\\s*[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JavascriptHref = new("(href|src)\\s*=\\s*[\"']\\s*javascript:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void No_razor_page_uses_inline_event_handlers_or_javascript_urls()
    {
        var pages = Directory.GetFiles(RepositoryDirectory("src", "WarehouseEPI.Web", "Pages"), "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(pages);

        var offenders = pages
            .Select(path => (Path: path, Content: File.ReadAllText(path)))
            .Where(page => InlineHandler.IsMatch(page.Content) || JavascriptHref.IsMatch(page.Content))
            .Select(page => Path.GetFileName(page.Path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Policy_keeps_scripts_restricted_to_the_application_origin()
    {
        var program = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "Program.cs"));

        Assert.Contains("script-src 'self'", program, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", program, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", program, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }

    private static string RepositoryDirectory(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
