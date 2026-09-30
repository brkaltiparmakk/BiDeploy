using BiDeploy.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace BiDeploy.Server.Pages.Panel;

public class FirmaModel(LicenseService licenses, TimeProvider time) : PanelPageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    public CompanyStatus? Status { get; private set; }
    public int AvailableLicenses { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var company = await licenses.FindCompanyAsync(DealerId, Id);
        if (company == null) return NotFound();
        Status = CompanyStatus.From(company, time.GetUtcNow().UtcDateTime);
        AvailableLicenses = company.Dealer.AvailableLicenses;
        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync() =>
        Done((await licenses.AssignLicenseAsync(DealerId, Id)).Error, "Lisans atandı.");

    public async Task<IActionResult> OnPostRenewAsync() =>
        Done((await licenses.RenewLicenseAsync(DealerId, Id)).Error, "Lisans 1 yıl uzatıldı.");

    public async Task<IActionResult> OnPostTransferAsync() =>
        Done((await licenses.TransferLicenseAsync(DealerId, Id)).Error, "Lisans sunucudan çözüldü. Yeni sunucuda aynı kodla etkinleştirin.");
}
