using System.Text.Json;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;
using BiDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BiDeploy.Server.Endpoints;

/// <summary>Bayi işlemleri: müşteri (VKN) ekleme, lisans atama/yenileme/taşıma, durum izleme.</summary>
public static class DealerEndpoints
{
    public record CreateCompanyRequest(string Vkn, string Title);

    public record CompanyView(
        int Id, string Vkn, string Title,
        string? ActivationCode, bool Activated, DateTime? LicenseExpiresAtUtc, string? MachineName, DateTime? LastSeenAtUtc,
        ServerReport? LastReport);

    public static void MapDealerEndpoints(this WebApplication app)
    {
        var dealer = app.MapGroup("/api/dealer").RequireDealer();

        dealer.MapGet("/me", (HttpContext http) =>
        {
            var d = http.CurrentDealer();
            return Results.Ok(new { d.Id, d.Name, d.AvailableLicenses });
        });

        dealer.MapPost("/companies", async (CreateCompanyRequest req, HttpContext http, BiDeployDb db, TimeProvider time) =>
        {
            var vkn = (req.Vkn ?? "").Trim();
            if (!TaxId.IsValid(vkn)) return Results.BadRequest("VKN (10 hane) veya TCKN (11 hane) geçersiz.");
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest("Firma unvanı gerekli.");
            if (await db.Companies.AnyAsync(c => c.Vkn == vkn)) return Results.Conflict("Bu VKN zaten kayıtlı.");

            var company = new Company
            {
                DealerId = http.CurrentDealer().Id,
                Vkn = vkn,
                Title = req.Title.Trim(),
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            return Results.Ok(new { company.Id, company.Vkn, company.Title });
        });

        dealer.MapGet("/companies", async (HttpContext http, BiDeployDb db) =>
        {
            var dealerId = http.CurrentDealer().Id;
            var companies = await db.Companies.Include(c => c.License)
                .Where(c => c.DealerId == dealerId).OrderBy(c => c.Title).ToListAsync();
            return companies.Select(ToView).ToList();
        });

        // Havuzdan 1 lisans düşer, aktivasyon kodu üretilir. Süre, sunucu ajanı etkinleştirildiğinde başlar.
        dealer.MapPost("/companies/{id:int}/license", async (int id, HttpContext http, BiDeployDb db, TimeProvider time) =>
        {
            var d = http.CurrentDealer();
            var company = await db.Companies.Include(c => c.License).SingleOrDefaultAsync(c => c.Id == id && c.DealerId == d.Id);
            if (company == null) return Results.NotFound();
            if (company.License != null) return Results.Conflict("Bu firmaya zaten lisans atanmış.");
            if (d.AvailableLicenses <= 0) return Results.BadRequest("Lisans havuzunuz boş.");

            d.AvailableLicenses--;
            company.License = new License { ActivationCode = Secrets.NewActivationCode(), IssuedAtUtc = time.GetUtcNow().UtcDateTime };
            await db.SaveChangesAsync();
            return Results.Ok(ToView(company));
        });

        // Yıllık yenileme: havuzdan 1 lisans düşer, süre bitiş tarihinden (geçmişse bugünden) itibaren uzar.
        dealer.MapPost("/companies/{id:int}/license/renew", async (int id, HttpContext http, BiDeployDb db, TimeProvider time, IOptions<ServerOptions> options) =>
        {
            var d = http.CurrentDealer();
            var company = await db.Companies.Include(c => c.License).SingleOrDefaultAsync(c => c.Id == id && c.DealerId == d.Id);
            if (company?.License == null) return Results.NotFound();
            if (company.License.ActivatedAtUtc == null) return Results.BadRequest("Lisans henüz etkinleştirilmemiş.");
            if (d.AvailableLicenses <= 0) return Results.BadRequest("Lisans havuzunuz boş.");

            var now = time.GetUtcNow().UtcDateTime;
            var from = company.License.ExpiresAtUtc > now ? company.License.ExpiresAtUtc.Value : now;
            company.License.ExpiresAtUtc = from.AddDays(options.Value.LicenseDays);
            d.AvailableLicenses--;
            await db.SaveChangesAsync();
            return Results.Ok(ToView(company));
        });

        // Sunucu değişimi: lisansı mevcut makineden çözer; aynı kodla yeni sunucuda etkinleştirilebilir. Süre korunur.
        dealer.MapPost("/companies/{id:int}/license/transfer", async (int id, HttpContext http, BiDeployDb db) =>
        {
            var d = http.CurrentDealer();
            var company = await db.Companies.Include(c => c.License).SingleOrDefaultAsync(c => c.Id == id && c.DealerId == d.Id);
            if (company?.License == null) return Results.NotFound();
            company.License.MachineId = null;
            company.License.MachineName = null;
            company.License.DeviceTokenHash = null;
            await db.SaveChangesAsync();
            return Results.Ok(ToView(company));
        });
    }

    private static CompanyView ToView(Company c) => new(
        c.Id, c.Vkn, c.Title,
        c.License?.ActivationCode,
        c.License?.DeviceTokenHash != null,
        c.License?.ExpiresAtUtc,
        c.License?.MachineName,
        c.License?.LastSeenAtUtc,
        c.License?.LastReportJson == null ? null : JsonSerializer.Deserialize<ServerReport>(c.License.LastReportJson, JsonDefaults.Options));
}
