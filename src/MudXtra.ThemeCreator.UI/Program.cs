using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using MudXtra.ThemeCreator.UI.Startup;
using Auth0.AspNetCore.Authentication;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using MudBlazor.Services;
using MudXtra.ThemeCreator.Infrastructure.Interfaces;
using MudXtra.ThemeCreator.Infrastructure.Services;
using MudXtra.ThemeCreator.UI;
using MudXtra.ThemeCreator.UI.Extensions;
using Serilog;
using Serilog.Events;

var probeMode = args.Contains("--postgres-probe");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--postgres-probe").ToArray());

var config = builder.Configuration;

// Probe mode returns before registering/building any application services. Bare startup
// also checks once: guard success is not a reusable readiness token if PostgreSQL disappears.
using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; _ = shutdown.CancelAsync(); };
Console.CancelKeyPress += cancelHandler;
using var terminate = OperatingSystem.IsLinux()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; _ = shutdown.CancelAsync(); })
    : null;
using var interrupt = OperatingSystem.IsLinux()
    ? PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; _ = shutdown.CancelAsync(); })
    : null;
ProbeResult readiness;
try
{
    // Npgsql's cancellation may need a separate TCP handshake. The process deadline
    // includes that/disposal too. On timeout/shutdown, return before app construction;
    // process exit closes any remaining sockets. This is not another retry layer.
    readiness = await PostgresReadiness.ProbeAsync(config, shutdown.Token)
        .WaitAsync(PostgresReadiness.AttemptTimeout, shutdown.Token);
}
catch (TimeoutException) { readiness = new(PostgresReadiness.TransientFailure, "attempt-timeout"); }
catch (OperationCanceledException) { readiness = new(PostgresReadiness.Cancelled, "shutdown"); }
Console.Error.WriteLine($"PostgreSQL startup: {readiness.Classification}");
if (readiness.ExitCode != PostgresReadiness.Success || probeMode)
    return readiness.ExitCode;
if (shutdown.IsCancellationRequested) return PostgresReadiness.Cancelled;

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning) // Suppress EF Core SQL commands
    .MinimumLevel.Override("Microsoft.AspNetCore.Components", LogEventLevel.Information)
    .Enrich.FromLogContext()
#if DEBUG
    .WriteTo.Console(
        restrictedToMinimumLevel: LogEventLevel.Information)
    .WriteTo.File(
        path: "Logs/applog-.log",
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: LogEventLevel.Warning)
#else // Write to Console only warnings and errors in Release mode for Docker Console Viewer
    .WriteTo.Console(
        restrictedToMinimumLevel: LogEventLevel.Warning)
#endif
        );

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthorization();
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();

builder.Services.AddMudServices(cfg =>
{
    cfg.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.TopRight;
    cfg.SnackbarConfiguration.PreventDuplicates = false;
    cfg.SnackbarConfiguration.HideTransitionDuration = 0;
    cfg.SnackbarConfiguration.VisibleStateDuration = 5000;
});

// PostgreSQL is required. Registration has no second connection test or provider fallback.
await builder.Services.AddThemeDataBaseConnection(config);

//Add other services
builder.Services.AddScoped<UserPreferencesService>(); // track user preferences across app
builder.Services.AddSingleton<IThemeCreatorService, ThemeCreatorService>(); // database theme service
builder.Services.AddScoped<ThemeStateService>(); // theme state service
builder.Services.AddScoped<StyleService>(); // CSS style getter service

// SEO improvements
builder.Services.AddHsts(options =>
{
    options.Preload = true;
    options.IncludeSubDomains = true;
    options.MaxAge = TimeSpan.FromDays(365);
});

// Authentication
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
builder.Services.AddAuth0WebAppAuthentication(options =>
{
    options.Domain = config["domain"]!;
    options.ClientId = config["clientid"]!;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
});
builder.Services.AddHttpsRedirection(options =>
{
    options.HttpsPort = 443;
});
builder.Services.Configure<HttpsRedirectionOptions>(options =>
{
    options.HttpsPort = 443;
});
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

var app = builder.Build();

app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.MapStaticAssets();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/Account/Login", async (HttpContext httpContext, string returnUrl = "/") =>
{
    var authenticationProperties = new LoginAuthenticationPropertiesBuilder()
        .WithScope("openid profile email roles")
        .WithRedirectUri(returnUrl)
        .Build();

    await httpContext.ChallengeAsync(Auth0Constants.AuthenticationScheme, authenticationProperties);
});

app.MapGet("/Account/Logout", async (HttpContext httpContext) =>
{
    var authenticationProperties = new LogoutAuthenticationPropertiesBuilder()
            .WithRedirectUri("/")
            .Build();

    await httpContext.SignOutAsync(Auth0Constants.AuthenticationScheme, authenticationProperties);
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Add headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    await next();
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync(shutdown.Token);
Console.CancelKeyPress -= cancelHandler;
return 0;
