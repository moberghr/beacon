using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Helpers;
using Semantico.Api.Types;

namespace Semantico.Api.Handlers.Accounts;

public class CreateAccountCommand : IRequestHandler<CreateAccountRequest, CreateAccountResponse>
{
    private readonly SemanticoContext _context;

    public CreateAccountCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<CreateAccountResponse> Handle(CreateAccountRequest request, CancellationToken cancellationToken)
    {
        var accountExists = await _context.Accounts
            .AnyAsync(x => x.Username == request.Username, cancellationToken);

        if (accountExists)
        {
            throw new SemanticoException($"User with username: '{request.Username}' already exists!");
        }

        var account = new Account
        {
            Username = request.Username,
            Value = Guid.NewGuid().ToString().ToUpper()
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync(cancellationToken);

        return new CreateAccountResponse
        {
            ApiKey = account.Value
        };
    }
}

public class CreateAccountRequest : IRequest<CreateAccountResponse>
{
    public required string Username { get; init; }
}

public class CreateAccountResponse
{
    public required string ApiKey { get; init; }
}