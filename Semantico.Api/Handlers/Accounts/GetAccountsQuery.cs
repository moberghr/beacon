using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;

namespace Semantico.Api.Handlers.Accounts;

public class GetAccountsQuery : IRequestHandler<GetAccountsRequst, GetAccountsResponse>
{
    private readonly SemanticoContext _context;

    public GetAccountsQuery(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<GetAccountsResponse> Handle(GetAccountsRequst requst, CancellationToken cancellationToken)
    {
        var accounts = await _context.Accounts
            .Select(x =>
                new GetAccountsResponseDataList
                {
                    Username = x.Username
                })
            .ToListAsync(cancellationToken);

        return new GetAccountsResponse
        {
            Accounts = accounts
        };
    }
}

public class GetAccountsRequst : IRequest<GetAccountsResponse>
{ }

public class GetAccountsResponse
{
    public required List<GetAccountsResponseDataList> Accounts { get; set; }
}

public class GetAccountsResponseDataList
{
    public required string Username { get; set; }
}