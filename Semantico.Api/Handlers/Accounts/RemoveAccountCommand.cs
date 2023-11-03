using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Types;
using Semantico.Api.Web;

namespace Semantico.Api.Handlers.Accounts;

public class RemoveAccountCommand : IRequestHandler<RemoveAccountRequest, RemoveAccountResponse>
{
    private readonly SemanticoContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RemoveAccountCommand(SemanticoContext context, IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<RemoveAccountResponse> Handle(RemoveAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await _context.Accounts
            .Where(x => x.Username == request.Username)
            .SingleAsync(cancellationToken);

        var currentApiKey = _httpContextAccessor.HttpContext.Request.Headers[Constants.SemanticoApiKeyHeaderName].ToString();

        if (account.Value == currentApiKey)
        {
            throw new SemanticoException("The logged-in user cannot delete themselves.");
        }

        _context.Accounts.Remove(account);
        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class RemoveAccountRequest : IRequest<RemoveAccountResponse>
{
    public required string Username { get; set; }
}

public class RemoveAccountResponse
{
}