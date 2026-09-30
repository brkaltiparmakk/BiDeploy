using BiDeploy.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BiDeploy.Server.Pages.Yonetim;

public class PaketlerModel(BiDeployDb db) : PanelPageModel
{
    public List<Package> Packages { get; private set; } = new();

    public async Task OnGetAsync() =>
        Packages = await db.Packages.OrderByDescending(p => p.PublishedAtUtc).ToListAsync();

    public async Task<IActionResult> OnPostWithdrawAsync(string packageId)
    {
        var package = await db.Packages.SingleOrDefaultAsync(p => p.PackageId == packageId);
        if (package == null) return Done("Paket bulunamadı.", "");
        package.IsActive = false;
        await db.SaveChangesAsync();
        return Done(null, $"{packageId} geri çekildi.");
    }
}
