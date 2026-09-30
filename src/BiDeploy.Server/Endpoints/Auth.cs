using BiDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BiDeploy.Server.Endpoints;

/// <summary>Üç tür çağıran: BiYazılım yöneticisi (X-Admin-Key), bayi (X-Api-Key), sunucu ajanı (Bearer cihaz jetonu).</summary>
public static class Auth
{
    public static RouteGroupBuilder RequireAdmin(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var options = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value;
            var key = ctx.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            if (string.IsNullOrEmpty(options.AdminKey) || !Secrets.FixedTimeEquals(key, options.AdminKey))
                return Results.Unauthorized();
            return await next(ctx);
        });

    public static RouteGroupBuilder RequireDealer(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var key = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
            if (string.IsNullOrEmpty(key)) return Results.Unauthorized();
            var db = ctx.HttpContext.RequestServices.GetRequiredService<BiDeployDb>();
            var hash = Secrets.Hash(key);
            var dealer = await db.Dealers.SingleOrDefaultAsync(d => d.ApiKeyHash == hash);
            if (dealer == null) return Results.Unauthorized();
            ctx.HttpContext.Items[typeof(Dealer)] = dealer;
            return await next(ctx);
        });

    public static RouteGroupBuilder RequireAgent(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return Results.Unauthorized();
            var db = ctx.HttpContext.RequestServices.GetRequiredService<BiDeployDb>();
            var hash = Secrets.Hash(header["Bearer ".Length..].Trim());
            var license = await db.Licenses.Include(l => l.Company).SingleOrDefaultAsync(l => l.DeviceTokenHash == hash);
            if (license == null) return Results.Unauthorized();
            ctx.HttpContext.Items[typeof(License)] = license;
            return await next(ctx);
        });

    public static Dealer CurrentDealer(this HttpContext http) => (Dealer)http.Items[typeof(Dealer)]!;
    public static License CurrentLicense(this HttpContext http) => (License)http.Items[typeof(License)]!;
}
