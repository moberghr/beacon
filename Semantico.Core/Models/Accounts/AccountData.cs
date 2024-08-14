namespace Semantico.Core.Models.Accounts
{
    public class AccountData
    {
        public required int AccountId { get; init; }

        public required string Username { get; init; }

        public string? ApiKey { get; set; }
    }
}
