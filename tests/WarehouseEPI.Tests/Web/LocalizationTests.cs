using System.Globalization;
using System.Collections;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WarehouseEPI.Web.Localization;
using WarehouseEPI.Web.Pages.Preferences;

namespace WarehouseEPI.Tests.Web;

public sealed class LocalizationTests
{
    [Fact]
    public void Resource_source_keys_do_not_collide_during_compilation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var resourcePath = Path.Combine(directory.FullName, "src", "WarehouseEPI.Web", "Resources", "Localization");
        var files = Directory.GetFiles(resourcePath, "*.resx");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in XDocument.Load(file).Root!.Elements("data"))
            {
                var key = entry.Attribute("name")!.Value;
                Assert.True(keys.Add(key), $"ResGen would discard a duplicate key in {Path.GetFileName(file)}: {key}");
            }
        }
    }

    [Fact]
    public void English_catalogs_have_all_neutral_keys_and_preserve_format_arguments()
    {
        var assembly = typeof(SharedTexts).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("WarehouseEPI.Web.Resources.Localization.", StringComparison.Ordinal)
                && name.EndsWith(".resources", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            var manager = new ResourceManager(name[..^".resources".Length], assembly);
            using var neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false);
            using var english = manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false);
            Assert.NotNull(neutral);
            Assert.NotNull(english);
            Assert.Equal(neutral.Cast<DictionaryEntry>().Count(), english.Cast<DictionaryEntry>().Count());
            foreach (DictionaryEntry entry in neutral)
            {
                var translated = english.GetString((string)entry.Key);
                Assert.False(string.IsNullOrWhiteSpace(translated), $"Missing English translation: {name} / {entry.Key}");
                var sourceArguments = Regex.Matches((string)entry.Value!, @"\{[0-9]+(?:[^}]*)\}").Select(match => match.Value).Order().ToArray();
                var targetArguments = Regex.Matches(translated!, @"\{[0-9]+(?:[^}]*)\}").Select(match => match.Value).Order().ToArray();
                Assert.True(sourceArguments.SequenceEqual(targetArguments), $"Changed placeholders: {name} / {entry.Key}");
            }
        }
    }

    [Fact]
    public void Client_script_translation_keys_exist_in_the_shared_dictionary()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var resourcePath = Path.Combine(directory.FullName, "src", "WarehouseEPI.Web", "Resources", "Localization");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resourceName in new[] { "SharedTexts.resx", "ClientTexts.resx" })
        {
            var document = XDocument.Load(Path.Combine(resourcePath, resourceName));
            foreach (var entry in document.Root!.Elements("data"))
                keys.Add(entry.Attribute("name")!.Value);
        }

        var scriptPath = Path.Combine(directory.FullName, "src", "WarehouseEPI.Web", "wwwroot", "js");
        var literalKey = new Regex(@"(?:text|translate)\(\s*[""'](?<key>[^""']+)[""']", RegexOptions.CultureInvariant);
        var missing = Directory.GetFiles(scriptPath, "*.js")
            .SelectMany(file => literalKey.Matches(File.ReadAllText(file))
                .Cast<Match>()
                .Select(match => $"{Path.GetFileName(file)}: {match.Groups["key"].Value}"))
            .Where(candidate => !keys.Contains(candidate[(candidate.IndexOf(": ", StringComparison.Ordinal) + 2)..]))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Missing client translation keys:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Theory]
    [InlineData("en", "Home")]
    [InlineData("es", "Inicio")]
    [InlineData("es-MX", "Inicio")]
    public void Shared_resources_resolve_in_both_languages(string language, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization(options => options.ResourcesPath = "Resources");
            using var provider = services.BuildServiceProvider();
            var texts = provider.GetRequiredService<IStringLocalizer<SharedTexts>>();
            Assert.Equal(expected, texts["Inicio"].Value);
            Assert.False(texts["Inicio"].ResourceNotFound);
            Assert.Equal("Missing resource", texts["Missing resource"].Value);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("en", "en", "es-MX")]
    [InlineData("es", "es", "es-MX")]
    [InlineData("fr", "es", "es-MX")]
    [InlineData(null, "es", "es-MX")]
    [InlineData("en", "en", "")]
    [InlineData("en", "en", "fr-FR")]
    public async Task Language_cookie_changes_only_ui_culture(string? cookie, string expected, string operationalCultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(operationalCultureName);
            var operationalCulture = CultureInfo.CurrentCulture;
            var options = UiLanguage.CreateOptions();
            var context = new DefaultHttpContext();
            // Browser language and standard culture cookies must not change input parsing.
            context.Request.Headers.AcceptLanguage = "fr-FR";
            context.Request.Headers.Cookie = ".AspNetCore.Culture=c=de-DE|uic=de-DE"
                + (cookie is null ? "" : $"; {UiLanguage.CookieName}={cookie}");
            var middleware = new RequestLocalizationMiddleware(nextContext =>
            {
                Assert.Equal(expected, CultureInfo.CurrentUICulture.Name);
                Assert.Equal(operationalCulture, CultureInfo.CurrentCulture);
                Assert.Equal(operationalCulture.NumberFormat.NumberDecimalSeparator,
                    CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
                Assert.NotNull(nextContext.Features.Get<IRequestCultureFeature>());
                return Task.CompletedTask;
            }, Options.Create(options), NullLoggerFactory.Instance);
            await middleware.Invoke(context);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("/Inventory?search=bolt", true)]
    [InlineData("https://example.com", false)]
    [InlineData("//example.com", false)]
    [InlineData(null, false)]
    public void Language_post_persists_preference_and_only_redirects_locally(string? returnUrl, bool local)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        var actionContext = new ActionContext(context, new RouteData(), new ActionDescriptor());
        var page = new LanguageModel
        {
            PageContext = new PageContext { HttpContext = context },
            Url = new UrlHelper(actionContext)
        };
        var result = page.OnPost("en", returnUrl);
        if (local) Assert.Equal(returnUrl, Assert.IsType<LocalRedirectResult>(result).Url);
        else Assert.Equal("/Index", Assert.IsType<RedirectToPageResult>(result).PageName);
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("WarehouseEPI.Language=en", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsupported_language_does_not_write_cookie()
    {
        var context = new DefaultHttpContext();
        var page = new LanguageModel { PageContext = new PageContext { HttpContext = context } };
        Assert.IsType<BadRequestResult>(page.OnPost("fr", "/"));
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }
}

public sealed class LocalizationRouteTests : IClassFixture<AdminRouteTests.WarehouseApplicationFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> factory;
    public LocalizationRouteTests(AdminRouteTests.WarehouseApplicationFactory factory) =>
        this.factory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
    public void Dispose() => factory.Dispose();

    [Theory]
    [InlineData("/", "en", "What do you need to do?")]
    [InlineData("/", "es", "¿Qué necesitas hacer?")]
    [InlineData("/Modules/operations", "en", "Record movements and check inventory.")]
    [InlineData("/Modules/operations", "es", "Registra movimientos y verifica el inventario.")]
    [InlineData("/Operations/Entry", "en", "Receipt")]
    [InlineData("/Operations/Transfer", "en", "Transfer")]
    [InlineData("/Operations/Production", "en", "Production")]
    [InlineData("/Operations/ProductionSupply", "en", "Production supplies")]
    [InlineData("/Inventory", "en", "Stock")]
    [InlineData("/Admin/Login", "en", "Administrative access")]
    public async Task Pages_render_requested_language_and_keep_local_navigation(string path, string language, string expected)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains($"<html lang=\"{language}\">", html, StringComparison.Ordinal);
        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.Contains("/Preferences/Language", html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selector_persists_language_and_returns_to_current_page()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        var response = await client.PostAsync("/Preferences/Language", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["language"] = "en", ["returnUrl"] = "/Modules/operations", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value)
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Modules/operations", response.Headers.Location?.OriginalString);
        var translated = await client.GetStringAsync("/Modules/operations");
        Assert.Contains("Record movements and check inventory.", translated, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", translated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Language_post_requires_antiforgery()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsync("/Preferences/Language", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["language"] = "en", ["returnUrl"] = "/"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("es", "El campo Cantidad debe ser un número.")]
    [InlineData("en", "The field Cantidad must be a number.")]
    public void Model_binding_messages_follow_the_interface_language(string language, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var options = factory.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
            Assert.Equal(expected, options.ModelBindingMessageProvider.ValueMustBeANumberAccessor("Cantidad"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("es", "El NIP es obligatorio.")]
    [InlineData("en", "PIN is required.")]
    public async Task Data_annotation_messages_follow_the_interface_language(string language, string expected)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        var login = await client.GetAsync("/Admin/Login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);

        var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = string.Empty,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value)
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es", "Saldos negativos", "Posiciones producto-ubicación con saldo neto negativo.")]
    [InlineData("en", "Negative balances", "Product-location positions with a negative net balance.")]
    public void Visible_json_text_follows_the_interface_language(
        string language,
        string expectedTitle,
        string expectedDescription)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            var texts = factory.Services.GetRequiredService<IStringLocalizer<CatalogTexts>>();
            var item = new WarehouseEPI.Infrastructure.Reporting.OperationalAlertItemDto(
                WarehouseEPI.Infrastructure.Reporting.OperationalAlertCategory.NegativeInventory,
                WarehouseEPI.Infrastructure.Reporting.OperationalAlertSeverity.Critical,
                2,
                "Saldos negativos",
                "Posiciones producto-ubicación con saldo neto negativo.",
                "/Reports/Inventory");
            var snapshot = new WarehouseEPI.Infrastructure.Reporting.OperationalAlertSnapshotDto(
                WarehouseEPI.Infrastructure.Reporting.OperationalAlertAudience.Public,
                DateTimeOffset.UtcNow,
                DateTimeOffset.Now,
                2,
                0,
                0,
                2,
                [item]);

            var localized = WarehouseEPI.Web.Pages.Reports.Notifications.IndexModel.LocalizeSnapshot(snapshot, texts);

            Assert.Equal(expectedTitle, localized.Items[0].Title);
            Assert.Equal(expectedDescription, localized.Items[0].Description);
            Assert.Equal(item.Category, localized.Items[0].Category);
            Assert.Equal(item.TargetUrl, localized.Items[0].TargetUrl);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}

