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

        Username = identity.FindFirst("name")?.Value!;
    }

    public string Username { get; }
}

public interface IAccount
{
    public string Username { get; }
}