using System.Text;
using BiDeploy.Core;
using BiDeploy.Server.Data;
using BiDeploy.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BiDeploy.Server.Endpoints;

/// <summary>BiYazılım yönetimi: bayiler, lisans havuzu, paket yayını.</summary>
public static class AdminEndpoints
{
    public record CreateDealerRequest(string Name, string? UserEmail = null, string? UserPassword = null);
    public record CreateDealerResponse(int Id, string Name, string ApiKey);
    public record AddLicensesRequest(int Count);

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAdmin();

        admin.MapPost("/dealers", async (CreateDealerRequest req, LicenseService licenses) =>
        {
            var result = await licenses.CreateDealerAsync(req.Name, req.UserEmail, req.UserPassword);
            // API anahtarı yalnızca bu yanıtta görünür; veritabanında özeti tutulur.
            return result.ToHttp(r => new CreateDealerResponse(r.Dealer.Id, r.Dealer.Name, r.ApiKey));
        });

        admin.MapGet("/dealers", async (BiDeployDb db) =>
            await db.Dealers.OrderBy(d => d.Name)
                .Select(d => new { d.Id, d.Name, d.AvailableLicenses, Companies = d.Companies.Count })
                .ToListAsync());

        admin.MapPost("/dealers/{id:int}/licenses", async (int id, AddLicensesRequest req, LicenseService licenses) =>
            (await licenses.AddLicensesAsync(id, req.Count)).ToHttp(d => new { d.Id, d.AvailableLicenses }));

        // Paket yükleme: Yayıncı aracı manifest.json + manifest.sig + setup dosyasını gönderir.
        // VPS imzayı ve dosya özetini kendisi de doğrular; imzasız/bozuk paket kabul edilmez.
        admin.MapPost("/packages", async (HttpRequest request, BiDeployDb db, IOptions<ServerOptions> options, TimeProvider time) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data bekleniyor.");
            var form = await request.ReadFormAsync();
            var manifestFile = form.Files["manifest"];
            var setupFile = form.Files["setup"];
            var signature = form["signature"].ToString().Trim();
            if (manifestFile == null || setupFile == null || signature.Length == 0)
                return Results.BadRequest("manifest, signature ve setup alanları gerekli.");

            string manifestJson;
            using (var reader = new StreamReader(manifestFile.OpenReadStream(), new UTF8Encoding(false)))
                manifestJson = await reader.ReadToEndAsync();

            var signed = new SignedManifest { ManifestJson = manifestJson, Signature = signature };
            PackageManifest manifest;
            try
            {
                manifest = PackageSigning.VerifyManifest(signed, options.Value.LoadPublisherKey());
            }
            catch (PackageVerificationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            if (await db.Packages.AnyAsync(p => p.PackageId == manifest.PackageId))
                return Results.Conflict($"{manifest.PackageId} zaten yayınlanmış.");

            var storage = Path.GetFullPath(options.Value.StorageDirectory);
            Directory.CreateDirectory(storage);
            var storedName = $"{manifest.PackageId}-{Guid.NewGuid():N}.bin";
            var storedPath = Path.Combine(storage, storedName);
            await using (var target = File.Create(storedPath))
                await setupFile.CopyToAsync(target);

            try
            {
                PackageSigning.VerifyFile(storedPath, manifest);
            }
            catch (PackageVerificationException ex)
            {
                File.Delete(storedPath);
                return Results.BadRequest(ex.Message);
            }

            var package = new Package
            {
                PackageId = manifest.PackageId,
                Product = manifest.Product,
                Architecture = manifest.Architecture,
                Version = manifest.Version,
                VersionSortKey = Secrets.VersionSortKey(manifest.Version),
                ManifestJson = manifestJson,
                Signature = signature,
                StoredFileName = storedName,
                FileSize = manifest.FileSize,
                PublishedAtUtc = time.GetUtcNow().UtcDateTime,
            };
            db.Packages.Add(package);
            await db.SaveChangesAsync();
            return Results.Ok(new { package.PackageId, package.Version, package.FileSize });
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        admin.MapGet("/packages", async (BiDeployDb db) =>
            await db.Packages.OrderByDescending(p => p.PublishedAtUtc)
                .Select(p => new { p.PackageId, p.Product, p.Architecture, p.Version, p.FileSize, p.PublishedAtUtc, p.IsActive })
                .ToListAsync());

        admin.MapPost("/packages/{packageId}/withdraw", async (string packageId, BiDeployDb db) =>
        {
            var package = await db.Packages.SingleOrDefaultAsync(p => p.PackageId == packageId);
            if (package == null) return Results.NotFound();
            package.IsActive = false;
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }
}
