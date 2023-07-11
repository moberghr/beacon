using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data.Entities;
using Semantico.Api.Helpers;

namespace Semantico.Api.Data;

public static class DataSeeder
{
    public static void Seed(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>().HasData(
            new Account { Id = 1, Username = "moberg", Value = PasswordHasher.Hash("3Semantico6#") });
    }
}