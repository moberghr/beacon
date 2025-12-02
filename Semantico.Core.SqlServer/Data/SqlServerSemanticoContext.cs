using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Core.SqlServer.Data;

internal sealed class SqlServerSemanticoContext(
    DbContextOptions<SqlServerSemanticoContext> options,
    string defaultSchema = "semantico")
    : SemanticoContext(options, defaultSchema)
{
}
