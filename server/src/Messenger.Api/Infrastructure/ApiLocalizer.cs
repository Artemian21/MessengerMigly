using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Localization;

namespace Messenger.Api.Infrastructure;

public static partial class ApiLocalizer
{
    public static readonly string[] SupportedCultures = ["en", "uk"];

    private static readonly ResourceManager Resources =
        new("Messenger.Api.Resources.ApiMessages", typeof(ApiLocalizer).Assembly);

    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text) || CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en")
        {
            return text ?? string.Empty;
        }

        if (Lookup(text) is { } direct)
        {
            return direct;
        }

        // Parameterised texts: "{0} was not found." and ASP.NET's "The {0} field is required."
        if (NotFoundRegex().Match(text) is { Success: true } notFound && Lookup("{0} was not found.") is { } notFoundTemplate)
        {
            return string.Format(CultureInfo.CurrentUICulture, notFoundTemplate, Lookup(notFound.Groups[1].Value) ?? notFound.Groups[1].Value);
        }

        if (RequiredRegex().Match(text) is { Success: true } required && Lookup("The {0} field is required.") is { } requiredTemplate)
        {
            return string.Format(CultureInfo.CurrentUICulture, requiredTemplate, required.Groups[1].Value);
        }

        return text;
    }

    /// <summary>Model-binding messages from System.Text.Json are technical; replace them with one readable sentence.</summary>
    public static string TranslateModelError(string message) =>
        message.Contains("JSON", StringComparison.Ordinal) || message.Contains("could not be converted", StringComparison.Ordinal)
            ? Translate("Request body is not valid JSON.")
            : Translate(message);

    public static RequestLocalizationOptions Options()
    {
        var options = new RequestLocalizationOptions()
            .SetDefaultCulture("en")
            .AddSupportedCultures(SupportedCultures)
            .AddSupportedUICultures(SupportedCultures);
        // Accept-Language only: no query-string or cookie culture providers.
        options.RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()];
        return options;
    }

    private static string? Lookup(string key)
    {
        var value = Resources.GetString(key, CultureInfo.CurrentUICulture);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    [GeneratedRegex(@"^(.+) was not found\.$")]
    private static partial Regex NotFoundRegex();

    [GeneratedRegex(@"^The (.+) field is required\.$")]
    private static partial Regex RequiredRegex();
}
