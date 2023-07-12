using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Data;

public static class DataSeeder
{
    private static readonly DateTime _createdTime = new(2023, 2, 2, 0, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string _passwordHash = "AQAAAAIAAYagAAAAECWEQ1jq8CPkruy8QrQy4eQqwKjFAQ2tt8wW/tH7zCype5L2asjL4W9+uBdvLMvPNQ==";

    public static void Seed(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>().HasData(
            new Account { Id = 1, CreatedTime = _createdTime, Username = "moberg", Value = _passwordHash });
    }
}