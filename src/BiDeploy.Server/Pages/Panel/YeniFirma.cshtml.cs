using BiDeploy.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace BiDeploy.Server.Pages.Panel;

public class YeniFirmaModel(LicenseService licenses) : PanelPageModel
{
    [BindProperty] public string Title { get; set; } = "";
    [BindProperty] public string Vkn { get; set; } = "";
    [BindProperty] public bool AssignLicense { get; set; }
    public string? FormError { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var created = await licenses.CreateCompanyAsync(DealerId, Vkn, Title);
        if (!created.Ok)
        {
            FormError = created.Error;
            return Page();
        }

        var id = created.Value!.Id;
        if (AssignLicense)
        {
            var assigned = await licenses.AssignLicenseAsync(DealerId, id);
            if (!assigned.Ok) Error = "Firma eklendi ancak lisans atanamadı: " + assigned.Error;
            else Message = "Firma eklendi ve lisans atandı. Aktivasyon kodunu sunucu ajanı kurulumunda kullanın.";
        }
        else Message = "Firma eklendi.";
        return Redirect($"/Panel/Firma/{id}");
    }
}
