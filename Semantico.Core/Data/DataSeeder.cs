using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data.Entities;

namespace Semantico.Core.Data;

public static class DataSeeder
{
    private static readonly DateTime _createdTime = new(2023, 2, 2, 0, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string _apiKey = "00000000-0000-0000-0000-000000000000";

    public static void Seed(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>().HasData(
            new Account { Id = 1, CreatedTime = _createdTime, Username = "moberg", Value = _apiKey });
    }
}