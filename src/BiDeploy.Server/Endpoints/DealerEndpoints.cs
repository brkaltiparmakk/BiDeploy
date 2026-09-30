using System.Text.Json;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;
using BiDeploy.Server.Data;
using BiDeploy.Server.Services;

namespace BiDeploy.Server.Endpoints;

/// <summary>Bayi API'si (X-Api-Key): müşteri (VKN) ekleme, lisans atama/yenileme/taşıma, durum izleme.</summary>
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

        dealer.MapPost("/companies", async (CreateCompanyRequest req, HttpContext http, LicenseService licenses) =>
            (await licenses.CreateCompanyAsync(http.CurrentDealer().Id, req.Vkn, req.Title))
                .ToHttp(c => new { c.Id, c.Vkn, c.Title }));

        dealer.MapGet("/companies", async (HttpContext http, LicenseService licenses) =>
            (await licenses.ListCompaniesAsync(http.CurrentDealer().Id)).Select(ToView).ToList());

        dealer.MapPost("/companies/{id:int}/license", async (int id, HttpContext http, LicenseService licenses) =>
            (await licenses.AssignLicenseAsync(http.CurrentDealer().Id, id)).ToHttp(ToView));

        dealer.MapPost("/companies/{id:int}/license/renew", async (int id, HttpContext http, LicenseService licenses) =>
            (await licenses.RenewLicenseAsync(http.CurrentDealer().Id, id)).ToHttp(ToView));

        dealer.MapPost("/companies/{id:int}/license/transfer", async (int id, HttpContext http, LicenseService licenses) =>
            (await licenses.TransferLicenseAsync(http.CurrentDealer().Id, id)).ToHttp(ToView));
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
