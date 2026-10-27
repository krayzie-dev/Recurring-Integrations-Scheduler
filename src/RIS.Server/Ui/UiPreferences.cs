using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;
using MudBlazor;
using RIS.Server.Resources;

namespace RIS.Server.Ui;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>Time zone used to display times in the UI.</summary>
public enum DisplayTimeZone
{
    Server,
    Utc
}

/// <summary>
/// Per-user UI settings, kept in the "ris.prefs" cookie (written by JS without a page reload).
/// The language is in the standard ASP.NET Core culture cookie.
/// </summary>
public sealed record UiPreferences(ThemeMode Theme = ThemeMode.System, DisplayTimeZone TimeZone = DisplayTimeZone.Server)
{
    public const string CookieName = "ris.prefs";

    public static UiPreferences FromCookie(string? value)
    {
        var parts = (value ?? string.Empty).Split('|');
        var theme = parts.Length > 0 && Enum.TryParse<ThemeMode>(parts[0], out var t) ? t : ThemeMode.System;
        var zone = parts.Length > 1 && Enum.TryParse<DisplayTimeZone>(parts[1], out var z) ? z : DisplayTimeZone.Server;
        return new UiPreferences(theme, zone);
    }

    public string ToCookie() => $"{Theme}|{TimeZone}";

    public DateTimeOffset ToDisplay(DateTimeOffset value) =>
        TimeZone == DisplayTimeZone.Utc ? value.ToUniversalTime() : TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local);

    public string Format(DateTimeOffset? value, string format = "g") =>
        value is null ? "–" : ToDisplay(value.Value).ToString(format, CultureInfo.CurrentCulture);

    public string ZoneLabel => TimeZone == DisplayTimeZone.Utc ? "UTC" : TimeZoneInfo.Local.StandardName;
}

/// <summary>Supported UI languages.</summary>
public static class RisCultures
{
    public static readonly string[] Supported = ["en-US", "pl-PL"];

    public static RequestLocalizationOptions Options()
    {
        var options = new RequestLocalizationOptions()
            .SetDefaultCulture(Supported[0])
            .AddSupportedCultures(Supported)
            .AddSupportedUICultures(Supported);
        options.FallBackToParentCultures = true;
        options.FallBackToParentUICultures = true;
        // Order: culture cookie, browser language, default (en). Query string is not used.
        options.RequestCultureProviders = [new CookieRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
        return options;
    }

    /// <summary>Sets the culture cookie and returns to the page; Blazor Server takes the culture when the circuit starts.</summary>
    public static void MapCultureEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/culture/set", (string culture, string? redirect, HttpContext context) =>
        {
            if (Supported.Contains(culture))
            {
                context.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                    new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });
            }
            // Local redirects only.
            return Results.LocalRedirect(string.IsNullOrEmpty(redirect) || !redirect.StartsWith('/') || redirect.StartsWith("//") ? "/" : redirect);
        }).RequireAuthorization();
}

/// <summary>Light and dark palettes; status colours chosen for contrast in both.</summary>
public static class RisTheme
{
    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1565C0",
            Secondary = "#6A1B9A",
            AppbarBackground = "#0D47A1",
            Success = "#2E7D32",
            Warning = "#E65100",
            Error = "#C62828",
            Info = "#0277BD",
            Background = "#F5F7FA"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#64B5F6",
            Secondary = "#CE93D8",
            AppbarBackground = "#1A1F2B",
            Success = "#66BB6A",
            Warning = "#FFA726",
            Error = "#EF5350",
            Info = "#4FC3F7",
            Background = "#12151C",
            Surface = "#1C212B",
            DrawerBackground = "#171B23"
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "6px" }
    };
}

/// <summary>Translations of MudBlazor's own texts (pager, filters, …) from <see cref="UiText"/>; English falls back to MudBlazor.</summary>
public sealed class RisMudLocalizer(IStringLocalizer<UiText> text) : MudLocalizer
{
    public override LocalizedString this[string key]
    {
        get
        {
            var value = text[key];
            return value.ResourceNotFound || value.Value == key ? new LocalizedString(key, key, resourceNotFound: true) : value;
        }
    }
}
