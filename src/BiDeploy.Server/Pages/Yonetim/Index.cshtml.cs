using BiDeploy.Server.Data;
using BiDeploy.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BiDeploy.Server.Pages.Yonetim;

public class YonetimIndexModel(BiDeployDb db, LicenseService licenses) : PanelPageModel
{
    public record DealerRow(int Id, string Name, int AvailableLicenses, int Companies, int ActiveServers);

    public List<DealerRow> Dealers { get; private set; } = new();
    [TempData] public string? NewApiKey { get; set; }

    public async Task OnGetAsync()
    {
        Dealers = await db.Dealers.OrderBy(d => d.Name)
            .Select(d => new DealerRow(d.Id, d.Name, d.AvailableLicenses, d.Companies.Count,
                d.Companies.Count(c => c.License != null && c.License.DeviceTokenHash != null)))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateDealerAsync(string name, string email, string password)
    {
        var result = await licenses.CreateDealerAsync(name, email, password);
        if (result.Ok) NewApiKey = result.Value.ApiKey;
        return Done(result.Error, $"{name} oluşturuldu. {email} ile giriş yapabilir.");
    }

    public async Task<IActionResult> OnPostAddLicensesAsync(int dealerId, int count)
    {
        var result = await licenses.AddLicensesAsync(dealerId, count);
        return Done(result.Error, $"{result.Value?.Name} havuzuna {count} lisans eklendi (toplam {result.Value?.AvailableLicenses}).");
    }
}
