using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Models;
using Semantico.Core.Models.Accounts;

namespace Semantico.Core.Services
{
    public interface IAccountService
    {
        Task<string> CreateAccountAsync(string username, CancellationToken cancellationToken);

        Task RemoveAccountAsync(string username, CancellationToken cancellationToken);

        Task<List<AccountData>> GetAccountsAsync(CancellationToken cancellationToken);

        Task<AccountData?> GetAccountByApiKeyAsync(string apiKey, CancellationToken cancellationToken);
    }

    internal class AccountService : IAccountService
    {
        private readonly SemanticoContext _context;

        public AccountService(SemanticoContext context)
        {
            _context = context;
        }

        public async Task<string> CreateAccountAsync(string username, CancellationToken cancellationToken)
        {
            var accountExists = await _context.Accounts
            .AnyAsync(x => x.Username == username, cancellationToken);

            if (accountExists)
            {
                throw new SemanticoException($"User with username: '{username}' already exists!");
            }

            var account = new Account
            {
                Username = username,
                Value = Guid.NewGuid().ToString().ToUpper()
            };

            _context.Accounts.Add(account);
            await _context.SaveChangesAsync(cancellationToken);

            return account.Value;
        }

        public async Task<AccountData?> GetAccountByApiKeyAsync(string apiKey, CancellationToken cancellationToken)
        {
            return await _context.Accounts
                .Where(x => x.Value == apiKey)
                .Select(x =>  new AccountData 
                {
                    AccountId = x.Id, 
                    Username = x.Username, 
                    ApiKey = x.Value 
                })
                .SingleOrDefaultAsync(cancellationToken);
        }

        public async Task<List<AccountData>> GetAccountsAsync(CancellationToken cancellationToken)
        {
            return await _context.Accounts
                .Select(x => new AccountData
                {
                    AccountId = x.Id,
                    Username = x.Username
                })
                .ToListAsync(cancellationToken);
        }

        public async Task RemoveAccountAsync(string username, CancellationToken cancellationToken)
        {
            var account = await _context.Accounts
                .Where(x => x.Username == username)
                .SingleAsync(cancellationToken);

            //if (account.Id == currentAccountId)
            //{
            //    throw new SemanticoException("The logged-in user cannot delete themselves.");
            //}

            _context.Accounts.Remove(account);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}