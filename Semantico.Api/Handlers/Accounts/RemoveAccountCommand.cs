using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Web;

namespace Semantico.Api.Handlers.Accounts;

public class RemoveAccountCommand : IRequestHandler<RemoveAccountRequest, RemoveAccountResponse>
{
    private readonly SemanticoContext _context;
    private readonly IAccount _account;

    public RemoveAccountCommand(SemanticoContext context, IAccount account)
    {
        _context = context;
        _account = account;
    }

    public async Task<RemoveAccountResponse> Handle(RemoveAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await _context.Accounts
            .Where(x => x.Username == request.Username)
            .SingleAsync(cancellationToken);

        if (account.Id == _account.AccountId)
        {
            throw new Exception("The logged-in user cannot delete themselves.");
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