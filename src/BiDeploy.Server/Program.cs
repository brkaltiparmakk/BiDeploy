using System.Text.Json.Serialization;
using BiDeploy.Server;
using BiDeploy.Server.Data;
using BiDeploy.Server.Endpoints;
using BiDeploy.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var serverOptions = builder.Configuration.GetSection("BiDeploy").Get<ServerOptions>() ?? new ServerOptions();
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("BiDeploy"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<LicenseService>();

var connectionString = builder.Configuration.GetConnectionString("BiDeploy") ?? "Data Source=bideploy.db";
builder.Services.AddDbContext<BiDeployDb>(o =>
{
    if (serverOptions.DatabaseProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(connectionString);
    else o.UseSqlite(connectionString);
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Mikro setup'ları 200-300 MB; paket yüklemede form sınırı yükseltilir.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024);

// Web paneli: e-posta + şifre ile giriş, oturum çerezi. Anahtarlar diskte tutulur ki
// konteyner yeniden başlayınca kullanıcıların oturumu düşmesin.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetFullPath(serverOptions.StorageDirectory), "dataprotection-keys")))
    .SetApplicationName("BiDeploy");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Giris";
        o.AccessDeniedPath = "/Giris";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();
// Panel Türkçe: ç, ğ, ı, ö, ş, ü gibi karakterler HTML'de &#x..; yerine olduğu gibi yazılsın.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/", "Signed");
    o.Conventions.AllowAnonymousToPage("/Giris");
    o.Conventions.AuthorizeFolder("/Panel", Roles.Dealer);
    o.Conventions.AuthorizeFolder("/Yonetim", Roles.Admin);
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Signed", p => p.RequireAuthenticatedUser())
    .AddPolicy(Roles.Dealer, p => p.RequireRole(Roles.Dealer).RequireClaim(BiDeploy.Server.Pages.PanelPageModel.DealerIdClaim))
    .AddPolicy(Roles.Admin, p => p.RequireRole(Roles.Admin));

// VPS'te uygulama Caddy'nin arkasında çalışır; gerçek şema (https) ve istemci IP'si başlıklardan alınır.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // İlk sürüm: şema otomatik oluşturulur. Üretime geçmeden EF migration'larına geçilecek.
    var db = scope.ServiceProvider.GetRequiredService<BiDeployDb>();
    db.Database.EnsureCreated();
    await SeedAdminAsync(db, scope.ServiceProvider.GetRequiredService<LicenseService>(), serverOptions, app.Logger);
}

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAdminEndpoints();
app.MapDealerEndpoints();
app.MapAgentEndpoints();
app.MapRazorPages();

app.Run();

// Hiç yönetici yoksa ayarlardaki ilk yönetici hesabı oluşturulur (BiDeploy__InitialAdminEmail / Password).
static async Task SeedAdminAsync(BiDeployDb db, LicenseService licenses, ServerOptions options, ILogger logger)
{
    if (await db.Users.AnyAsync(u => u.Role == Roles.Admin)) return;
    if (string.IsNullOrWhiteSpace(options.InitialAdminEmail))
    {
        logger.LogWarning("Yönetici hesabı yok. BiDeploy__InitialAdminEmail ve BiDeploy__InitialAdminPassword ayarlayın.");
        return;
    }
    var result = await licenses.CreateUserAsync(options.InitialAdminEmail, options.InitialAdminPassword, Roles.Admin, null);
    if (result.Ok) logger.LogInformation("İlk yönetici hesabı oluşturuldu: {Email}", result.Value!.Email);
    else logger.LogError("İlk yönetici hesabı oluşturulamadı: {Error}", result.Error);
}

public partial class Program;
