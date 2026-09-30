using System.Text.Json;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;
using BiDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BiDeploy.Server.Endpoints;

/// <summary>Sunucu ajanlarının kullandığı uçlar. İstemci ajanları VPS'e hiç bağlanmaz.</summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/agent/activate", async (ActivationRequest req, BiDeployDb db, TimeProvider time, IOptions<ServerOptions> options) =>
        {
            if (string.IsNullOrWhiteSpace(req.MachineId)) return Results.BadRequest("MachineId gerekli.");
            var code = (req.ActivationCode ?? "").Trim().ToUpperInvariant();
            var license = await db.Licenses.Include(l => l.Company).SingleOrDefaultAsync(l => l.ActivationCode == code);
            // Kod ve VKN birlikte doğrulanır; hangisinin yanlış olduğu söylenmez.
            if (license == null || license.Company.Vkn != (req.Vkn ?? "").Trim())
                return Results.BadRequest("Aktivasyon kodu veya VKN hatalı.");

            if (license.MachineId != null && license.MachineId != req.MachineId)
                return Results.Conflict("Bu lisans başka bir sunucuda etkin. Sunucu değiştiyse bayi panelinden lisansı taşıyın.");

            var now = time.GetUtcNow().UtcDateTime;
            if (license.ActivatedAtUtc == null)
            {
                license.ActivatedAtUtc = now;
                license.ExpiresAtUtc = now.AddDays(options.Value.LicenseDays);
            }
            var token = Secrets.NewToken();
            license.MachineId = req.MachineId;
            license.MachineName = req.MachineName;
            license.DeviceTokenHash = Secrets.Hash(token);
            license.LastSeenAtUtc = now;
            await db.SaveChangesAsync();

            return Results.Ok(new ActivationResponse
            {
                DeviceToken = token,
                CompanyTitle = license.Company.Title,
                LicenseExpiresAtUtc = license.ExpiresAtUtc!.Value,
            });
        });

        var agent = app.MapGroup("/api/agent").RequireAgent();

        agent.MapGet("/packages/latest", async (string product, string arch, HttpContext http, BiDeployDb db, TimeProvider time) =>
        {
            var license = http.CurrentLicense();
            var now = time.GetUtcNow().UtcDateTime;
            license.LastSeenAtUtc = now;
            await db.SaveChangesAsync();

            var response = new LatestPackageResponse { LicenseExpiresAtUtc = license.ExpiresAtUtc ?? now };
            if (license.IsExpired(now))
            {
                response.LicenseExpired = true;
                return Results.Ok(response);
            }

            var package = await db.Packages
                .Where(p => p.IsActive && p.Product == product && p.Architecture == arch)
                .OrderByDescending(p => p.VersionSortKey)
                .FirstOrDefaultAsync();
            if (package != null)
                response.Package = new SignedManifest { ManifestJson = package.ManifestJson, Signature = package.Signature };
            return Results.Ok(response);
        });

        agent.MapGet("/packages/{packageId}/file", async (string packageId, HttpContext http, BiDeployDb db, TimeProvider time, IOptions<ServerOptions> options) =>
        {
            if (http.CurrentLicense().IsExpired(time.GetUtcNow().UtcDateTime)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var package = await db.Packages.SingleOrDefaultAsync(p => p.PackageId == packageId);
            if (package == null) return Results.NotFound();
            var path = Path.Combine(Path.GetFullPath(options.Value.StorageDirectory), package.StoredFileName);
            // Range desteği: kesilen indirme kaldığı yerden devam eder.
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        agent.MapPost("/report", async (ServerReport report, HttpContext http, BiDeployDb db, TimeProvider time) =>
        {
            var license = http.CurrentLicense();
            license.LastSeenAtUtc = time.GetUtcNow().UtcDateTime;
            license.LastReportJson = JsonSerializer.Serialize(report, JsonDefaults.Options);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
