using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Services;

public interface IAccountService
{
    Task<Account> GetAccount(string username);
}

public class AccountService : IAccountService
{
    private readonly SemanticoContext _context;

    public AccountService(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<Account> GetAccount(string username)
    {
        var account = await _context.Accounts
            .Where(x => x.Username == username)
            .Select(x =>
              new Account
              {
                  Username = x.Username,
                  Value = x.Value
              })
            .FirstOrDefaultAsync();

        if (account == null)
        {
            throw new Exception($"Account with username: {username} don't exists");
        }

        return account;
    }
}