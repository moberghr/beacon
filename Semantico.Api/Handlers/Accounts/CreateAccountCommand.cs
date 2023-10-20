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
            .AnyAsync(x => x.Username == request.Username);

        if (accountExists)
        {
            throw new SemanticoException($"User with username:{request.Username} already exists!");
        }

        var account = new Account
        {
            Username = request.Username,
            Value = PasswordHasher.Hash(request.Password)
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class CreateAccountRequest : IRequest<CreateAccountResponse>
{
    public required string Username { get; set; }

    public required string Password { get; set; }
}

public class CreateAccountResponse
{ }