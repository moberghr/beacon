using Microsoft.AspNetCore.Identity;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Helpers;

public static class PasswordHasher
{
    public static string Hash(string password)
    {
        var passwordHash = new PasswordHasher<Account>();

        return passwordHash.HashPassword(null!, password);
    }

    public static bool Check(string hashedPassword, string password)
    {
        var passwordHasher = new PasswordHasher<Account>();

        return passwordHasher.VerifyHashedPassword(null!, hashedPassword, password) != PasswordVerificationResult.Failed;
    }
}