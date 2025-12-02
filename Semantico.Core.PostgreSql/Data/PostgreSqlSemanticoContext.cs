using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Core.PostgreSql.Data;

internal sealed class PostgreSqlSemanticoContext(
    DbContextOptions<PostgreSqlSemanticoContext> options,
    string defaultSchema = "semantico")
    : SemanticoContext(options, defaultSchema)
{
}
