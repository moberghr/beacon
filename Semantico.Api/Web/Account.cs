using Semantico.Api.Types;
using System.Security.Claims;

namespace Semantico.Api.Web;

public interface IAccount
{
    string ApiKey { get; }

    int AccountId { get; }
}

public class AccountClaimsResolver : IAccount
{
    public AccountClaimsResolver(IHttpContextAccessor httpContextAccessor)
    {
        var identity = httpContextAccessor.HttpContext!.User.Identity as ClaimsIdentity;

        if (identity == null)
        {
            throw new SemanticoException($"Identity is null");
        }

        ApiKey = identity.FindFirst(AccountClaimType.ApiKey)?.Value!;
        AccountId = int.Parse(identity.FindFirst(AccountClaimType.AccountId)?.Value!);
    }

    public string ApiKey { get; }

    public int AccountId { get; }
}
