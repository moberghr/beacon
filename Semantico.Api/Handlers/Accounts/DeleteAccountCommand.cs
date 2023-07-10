using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;

namespace Semantico.Api.Handlers.Accounts;

public class DeleteAccountCommand : IRequestHandler<DeleteAccountRequest, DeleteAccountResponse>
{
    private readonly SemanticoContext _context;

    public DeleteAccountCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<CreateAccountResponse> Handle(DeleteAccountRequest request, CancellationToken cancellationToken)
    {
        var username = await _context.Accounts
            .Where(x => x.Id == request.AccountId)
            .SingleAsync();

        return new();
    }
}

public class DeleteAccountRequest : IRequest<DeleteAccountResponse>
{
    public required int AccountId { get; set; }
}

public class DeleteAccountResponse
{
}