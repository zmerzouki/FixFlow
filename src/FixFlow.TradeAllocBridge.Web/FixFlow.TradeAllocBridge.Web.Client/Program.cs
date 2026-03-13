using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Net.Http;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});
builder.Services.AddScoped<FixFlow.TradeAllocBridge.Web.Client.State.DirectIngestionState>();
builder.Services.AddScoped<FixFlow.TradeAllocBridge.Web.Client.State.MessageLogState>();
builder.Services.AddScoped<FixFlow.TradeAllocBridge.Web.Client.State.MapManagementState>();
builder.Services.AddScoped<FixFlow.TradeAllocBridge.Web.Client.State.SettingsState>();

await builder.Build().RunAsync();
