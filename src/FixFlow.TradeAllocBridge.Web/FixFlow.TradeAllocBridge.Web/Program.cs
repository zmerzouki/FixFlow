using FixFlow.TradeAllocBridge.Web.Client.Pages;
using FixFlow.TradeAllocBridge.Web.Components;
using FixFlow.TradeAllocBridge.Web.Client.State;
using FixFlow.TradeAllocBridge.Core.Config;
using FixFlow.TradeAllocBridge.Core.Excel;
using FixFlow.TradeAllocBridge.Core.Fix;
using FixFlow.TradeAllocBridge.Core.Mapping;
using FixFlow.TradeAllocBridge.Core.Reporting;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Prefer appsettings.json from the runtime working directory (bin output) if present.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: true);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, $"appsettings.{builder.Environment.EnvironmentName}.json"),
    optional: true,
    reloadOnChange: true);
var sharedSettingsPath = SharedConfigResolver.ResolveSharedAppSettingsPath(AppContext.BaseDirectory);
if (!string.IsNullOrWhiteSpace(sharedSettingsPath))
{
    builder.Configuration.AddJsonFile(sharedSettingsPath, optional: true, reloadOnChange: true);
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    var baseUri = httpContext is null
        ? "http://localhost/"
        : $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/";
    return new HttpClient { BaseAddress = new Uri(baseUri) };
});
builder.Services.AddSingleton<ExcelParser>();
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var fixConfig = configuration.GetSection("Fix").Get<FixConfig>() ?? new FixConfig();
    if (string.IsNullOrWhiteSpace(fixConfig.SessionQualifier))
    {
        fixConfig.SessionQualifier = configuration["FixSessionQualifiers:Web"] ?? "FIXFLOWWEB";
    }

    return fixConfig;
});
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<FixMappingRepository>>();
    var configDir = ResolveConfigDirectory();
    return new FixMappingRepository(configDir, logger);
});
builder.Services.AddSingleton<ValidationReport>();
builder.Services.AddSingleton<FixApp>();
builder.Services.AddSingleton<FixEngine>();
builder.Services.AddSingleton<FixClient>();
builder.Services.AddScoped<DirectIngestionState>();
builder.Services.AddScoped<MessageLogState>();
builder.Services.AddScoped<MapManagementState>();
builder.Services.AddScoped<FixFlow.TradeAllocBridge.Web.Client.State.SettingsState>();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FixFlow.TradeAllocBridge.Web.Client._Imports).Assembly);

app.Run();

static string ResolveConfigDirectory()
{
    var probePaths = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "configs"),
        Path.Combine(Directory.GetCurrentDirectory(), "configs"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "configs")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"))
    };

    var existing = probePaths.FirstOrDefault(Directory.Exists);
    var resolved = existing ?? Path.Combine(AppContext.BaseDirectory, "configs");
    Directory.CreateDirectory(resolved);
    return resolved;
}
