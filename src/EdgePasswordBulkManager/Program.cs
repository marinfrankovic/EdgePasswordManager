using EdgePasswordBulkManager.Components;
using EdgePasswordBulkManager.Models;
using EdgePasswordBulkManager.Services;
using EdgePasswordBulkManager.State;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));

builder.Services.AddHttpClient("lists", c => c.Timeout = TimeSpan.FromMinutes(3));

builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<ProfileDiscoveryService>();
builder.Services.AddSingleton<LoginDatabaseReader>();
builder.Services.AddSingleton<BackupExportService>();
builder.Services.AddSingleton<DeleteService>();
builder.Services.AddSingleton<RestoreService>();
builder.Services.AddSingleton<CategoryService>();

builder.Services.AddSingleton<ListRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ListRefreshService>());

// Per-circuit view-model state.
builder.Services.AddScoped<PasswordManagerState>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
