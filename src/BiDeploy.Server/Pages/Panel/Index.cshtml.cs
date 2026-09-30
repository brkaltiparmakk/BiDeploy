using BiDeploy.Server.Data;
using BiDeploy.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace BiDeploy.Server.Pages.Panel;

public class PanelIndexModel(LicenseService licenses, BiDeployDb db, TimeProvider time) : PanelPageModel
{
    public string DealerName { get; private set; } = "";
    public int AvailableLicenses { get; private set; }
    public List<CompanyStatus> Companies { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var dealer = await db.Dealers.FindAsync(DealerId);
        if (dealer == null) return Forbid();
        DealerName = dealer.Name;
        AvailableLicenses = dealer.AvailableLicenses;
        var now = time.GetUtcNow().UtcDateTime;
        Companies = (await licenses.ListCompaniesAsync(DealerId)).Select(c => CompanyStatus.From(c, now)).ToList();
        return Page();
    }
}
