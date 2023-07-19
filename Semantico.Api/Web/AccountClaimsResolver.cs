using Semantico.Api.Types;
using System.Security.Claims;

namespace Semantico.Api.Web;

public class AccountClaimsResolver : IAccount
{
    public AccountClaimsResolver(IHttpContextAccessor httpContextAccessor)
    {
        var identity = httpContextAccessor.HttpContext!.User.Identity as ClaimsIdentity;

        if (identity == null)
        {
            throw new Exception(nameof(identity));
        }

        Username = identity.FindFirst(AccountClaimType.Name)?.Value!;
        AccountId = int.Parse(identity.FindFirst(AccountClaimType.AccountId)?.Value!);
    }

    public string Username { get; }

    public int AccountId { get; }
}

public interface IAccount
{
    public string Username { get; }

    public int AccountId { get; }
}