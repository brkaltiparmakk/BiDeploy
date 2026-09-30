using System.Text.Json.Serialization;
using BiDeploy.Server;
using BiDeploy.Server.Data;
using BiDeploy.Server.Endpoints;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var serverOptions = builder.Configuration.GetSection("BiDeploy").Get<ServerOptions>() ?? new ServerOptions();
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("BiDeploy"));
builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("BiDeploy") ?? "Data Source=bideploy.db";
builder.Services.AddDbContext<BiDeployDb>(o =>
{
    if (serverOptions.DatabaseProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(connectionString);
    else o.UseSqlite(connectionString);
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Mikro setup'ları 200-300 MB; paket yüklemede form sınırı yükseltilir.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // İlk sürüm: şema otomatik oluşturulur. Üretime geçmeden EF migration'larına geçilecek.
    scope.ServiceProvider.GetRequiredService<BiDeployDb>().Database.EnsureCreated();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAdminEndpoints();
app.MapDealerEndpoints();
app.MapAgentEndpoints();

app.Run();

public partial class Program;
