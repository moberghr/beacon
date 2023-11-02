using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Types;

namespace Semantico.Api.Services;

public interface IAccountService
{
    Task<bool> ValidateApiKeyAsync(string apiKey);
}

public class AccountService : IAccountService
{
    private readonly SemanticoContext _context;

    public AccountService(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<bool> ValidateApiKeyAsync(string apiKey)
    {
        var accountExists = await _context.Accounts
            .Where(x => x.Value == apiKey)
            .AnyAsync();

        return accountExists;
    }
}