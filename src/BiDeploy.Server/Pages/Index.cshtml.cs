using BiDeploy.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BiDeploy.Server.Pages;

public class IndexModel : PageModel
{
    public IActionResult OnGet() => Redirect(User.IsInRole(Roles.Admin) ? "/Yonetim" : "/Panel");
}
