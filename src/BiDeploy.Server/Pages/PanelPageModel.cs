using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BiDeploy.Server.Pages;

/// <summary>Panel sayfalarının ortak tabanı: giriş yapan kullanıcının bayisi ve tek seferlik bildirim mesajları.</summary>
public abstract class PanelPageModel : PageModel
{
    public const string DealerIdClaim = "dealer_id";

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    protected int DealerId => int.Parse(User.FindFirstValue(DealerIdClaim)
        ?? throw new InvalidOperationException("Kullanıcı bir bayiye bağlı değil."));

    protected IActionResult Done(string? error, string success)
    {
        if (error != null) Error = error;
        else Message = success;
        return LocalRedirect(Request.Path);
    }
}
