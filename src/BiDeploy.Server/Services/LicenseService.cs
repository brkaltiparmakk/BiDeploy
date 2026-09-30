using BiDeploy.Core;
using BiDeploy.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BiDeploy.Server.Services;

/// <summary>İşlem sonucu: başarılıysa değer, değilse kullanıcıya gösterilecek Türkçe hata.</summary>
public record OpResult<T>(T? Value, string? Error, OpError Kind = OpError.None)
{
    public bool Ok => Error == null;
    public static OpResult<T> Success(T value) => new(value, null);
    public static OpResult<T> Fail(string error, OpError kind = OpError.Invalid) => new(default, error, kind);

    public IResult ToHttp(Func<T, object>? map = null) => Kind switch
    {
        OpError.None => Results.Ok(map == null ? Value : map(Value!)),
        OpError.NotFound => Results.NotFound(Error),
        OpError.Conflict => Results.Conflict(Error),
        _ => Results.BadRequest(Error),
    };
}

public enum OpError { None, Invalid, NotFound, Conflict }

/// <summary>Bayi, müşteri ve lisans iş kuralları. Hem API hem web paneli bu servisi kullanır.</summary>
public class LicenseService(BiDeployDb db, TimeProvider time, IOptions<ServerOptions> options)
{
    private static readonly PasswordHasher<User> Hasher = new();
    public const int MinPasswordLength = 8;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<OpResult<(Dealer Dealer, string ApiKey)>> CreateDealerAsync(string name, string? userEmail, string? userPassword)
    {
        if (string.IsNullOrWhiteSpace(name)) return OpResult<(Dealer, string)>.Fail("Bayi adı gerekli.");
        var apiKey = Secrets.NewToken();
        var dealer = new Dealer { Name = name.Trim(), ApiKeyHash = Secrets.Hash(apiKey), CreatedAtUtc = Now };
        db.Dealers.Add(dealer);
        if (!string.IsNullOrWhiteSpace(userEmail))
        {
            var user = await NewUserAsync(userEmail, userPassword, Roles.Dealer);
            if (!user.Ok) return OpResult<(Dealer, string)>.Fail(user.Error!, user.Kind);
            user.Value!.Dealer = dealer;
        }
        await db.SaveChangesAsync();
        return OpResult<(Dealer, string)>.Success((dealer, apiKey));
    }

    public async Task<OpResult<User>> CreateUserAsync(string email, string? password, string role, int? dealerId)
    {
        var user = await NewUserAsync(email, password, role);
        if (!user.Ok) return user;
        user.Value!.DealerId = dealerId;
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<OpResult<User>> NewUserAsync(string email, string? password, string role)
    {
        email = email.Trim().ToLowerInvariant();
        if (!email.Contains('@')) return OpResult<User>.Fail("Geçerli bir e-posta girin.");
        if (password == null || password.Length < MinPasswordLength) return OpResult<User>.Fail($"Şifre en az {MinPasswordLength} karakter olmalı.");
        if (await db.Users.AnyAsync(u => u.Email == email)) return OpResult<User>.Fail("Bu e-posta zaten kayıtlı.", OpError.Conflict);
        var user = new User { Email = email, Role = role, CreatedAtUtc = Now };
        user.PasswordHash = Hasher.HashPassword(user, password);
        db.Users.Add(user);
        return OpResult<User>.Success(user);
    }

    public async Task<User?> ValidateLoginAsync(string email, string password)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (user == null) return null;
        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (result == PasswordVerificationResult.Failed) return null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, password!);
            await db.SaveChangesAsync();
        }
        return user;
    }

    public async Task<OpResult<Dealer>> AddLicensesAsync(int dealerId, int count)
    {
        if (count == 0) return OpResult<Dealer>.Fail("Adet sıfır olamaz.");
        var dealer = await db.Dealers.FindAsync(dealerId);
        if (dealer == null) return OpResult<Dealer>.Fail("Bayi bulunamadı.", OpError.NotFound);
        if (dealer.AvailableLicenses + count < 0) return OpResult<Dealer>.Fail("Havuzda yeterli lisans yok.");
        dealer.AvailableLicenses += count;
        await db.SaveChangesAsync();
        return OpResult<Dealer>.Success(dealer);
    }

    public async Task<OpResult<Company>> CreateCompanyAsync(int dealerId, string? vkn, string? title)
    {
        vkn = (vkn ?? "").Trim();
        if (!TaxId.IsValid(vkn)) return OpResult<Company>.Fail("VKN (10 hane) veya TCKN (11 hane) geçersiz.");
        if (string.IsNullOrWhiteSpace(title)) return OpResult<Company>.Fail("Firma unvanı gerekli.");
        if (await db.Companies.AnyAsync(c => c.Vkn == vkn)) return OpResult<Company>.Fail("Bu VKN zaten kayıtlı.", OpError.Conflict);
        var company = new Company { DealerId = dealerId, Vkn = vkn, Title = title.Trim(), CreatedAtUtc = Now };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return OpResult<Company>.Success(company);
    }

    public Task<Company?> FindCompanyAsync(int dealerId, int companyId) =>
        db.Companies.Include(c => c.License).Include(c => c.Dealer)
            .SingleOrDefaultAsync(c => c.Id == companyId && c.DealerId == dealerId);

    public Task<List<Company>> ListCompaniesAsync(int dealerId) =>
        db.Companies.Include(c => c.License).Where(c => c.DealerId == dealerId).OrderBy(c => c.Title).ToListAsync();

    /// <summary>Havuzdan 1 lisans düşer, aktivasyon kodu üretilir. Süre, sunucu ajanı etkinleştirildiğinde başlar.</summary>
    public async Task<OpResult<Company>> AssignLicenseAsync(int dealerId, int companyId)
    {
        var company = await FindCompanyAsync(dealerId, companyId);
        if (company == null) return OpResult<Company>.Fail("Firma bulunamadı.", OpError.NotFound);
        if (company.License != null) return OpResult<Company>.Fail("Bu firmaya zaten lisans atanmış.", OpError.Conflict);
        if (company.Dealer.AvailableLicenses <= 0) return OpResult<Company>.Fail("Lisans havuzunuz boş.");
        company.Dealer.AvailableLicenses--;
        company.License = new License { ActivationCode = Secrets.NewActivationCode(), IssuedAtUtc = Now };
        await db.SaveChangesAsync();
        return OpResult<Company>.Success(company);
    }

    /// <summary>Yıllık yenileme: havuzdan 1 lisans düşer, süre bitiş tarihinden (geçmişse bugünden) itibaren uzar.</summary>
    public async Task<OpResult<Company>> RenewLicenseAsync(int dealerId, int companyId)
    {
        var company = await FindCompanyAsync(dealerId, companyId);
        if (company?.License == null) return OpResult<Company>.Fail("Lisans bulunamadı.", OpError.NotFound);
        if (company.License.ActivatedAtUtc == null) return OpResult<Company>.Fail("Lisans henüz etkinleştirilmemiş.");
        if (company.Dealer.AvailableLicenses <= 0) return OpResult<Company>.Fail("Lisans havuzunuz boş.");
        var from = company.License.ExpiresAtUtc > Now ? company.License.ExpiresAtUtc.Value : Now;
        company.License.ExpiresAtUtc = from.AddDays(options.Value.LicenseDays);
        company.Dealer.AvailableLicenses--;
        await db.SaveChangesAsync();
        return OpResult<Company>.Success(company);
    }

    /// <summary>Sunucu değişimi: lisansı mevcut makineden çözer; aynı kodla yeni sunucuda etkinleştirilebilir. Süre korunur.</summary>
    public async Task<OpResult<Company>> TransferLicenseAsync(int dealerId, int companyId)
    {
        var company = await FindCompanyAsync(dealerId, companyId);
        if (company?.License == null) return OpResult<Company>.Fail("Lisans bulunamadı.", OpError.NotFound);
        company.License.MachineId = null;
        company.License.MachineName = null;
        company.License.DeviceTokenHash = null;
        await db.SaveChangesAsync();
        return OpResult<Company>.Success(company);
    }
}
