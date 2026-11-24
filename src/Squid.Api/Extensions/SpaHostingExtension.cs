namespace Squid.Api.Extensions;

/// <summary>
/// Serves the bundled web UI (<c>wwwroot</c>, present only in the all-in-one image) from the same
/// origin as the API. Client-side routes fall back to <c>index.html</c>; server prefixes (API,
/// auth, system, Hangfire dashboard, Swagger) never do, so an unknown API path still 404s and the
/// Hangfire middleware further down the pipeline is still reached. Without <c>wwwroot</c> nothing
/// is registered and the API behaves exactly as before.
///
/// <para>The fallback is anonymous on purpose: the API's global FallbackPolicy requires an
/// authenticated user, and a browser navigation (deep link, refresh) carries no bearer token —
/// without this every client route but <c>/</c> answers 401. Only <c>index.html</c> is served.</para>
/// </summary>
public static class SpaHostingExtension
{
    public const string FallbackPattern = "{*path:nonfile:regex(^(?!(api|auth|system|hangfire|swagger)(/|$)).*$)}";

    public static bool HasBundledSpa(this IWebHostEnvironment env) => env.WebRootFileProvider.GetFileInfo("index.html").Exists;

    public static IApplicationBuilder UseBundledSpaFiles(this IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (!env.HasBundledSpa()) return app;

        return app.UseDefaultFiles().UseStaticFiles();
    }

    public static void MapBundledSpaFallback(this IEndpointRouteBuilder endpoints, IWebHostEnvironment env)
    {
        if (!env.HasBundledSpa()) return;

        endpoints.MapFallbackToFile(FallbackPattern, "index.html").AllowAnonymous();
    }
}
