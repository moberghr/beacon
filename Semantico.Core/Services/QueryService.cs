using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.QueryExecutionHistory;
using Semantico.Core.Validators;

namespace Semantico.Core.Services
{
    public interface IQueryService
    {
        Task CreateQueryAsync(QueryData queryData, CancellationToken cancellationToken);

        Task UpdateQueryAsync(QueryData queryData, CancellationToken cancellationToken);

        Task DeleteQueryAsync(int queryId, CancellationToken cancellationToken);

        Task<List<QueryData>> GetQueriesAsync(int? queryId, int? projectId, CancellationToken cancellationToken);
    }

    internal class QueryService : IQueryService
    {
        private readonly SemanticoContext _context;

        public QueryService(SemanticoContext context)
        {
            _context = context;
        }

        public async Task CreateQueryAsync(QueryData queryData, CancellationToken cancellationToken)
        {
            QueryValidator.CheckForFlaggedWords(queryData.SqlValue);

            QueryValidator.CheckForParameters(queryData.SqlValue, queryData.Parameters);

            var query = new Query
            {
                SqlValue = queryData.SqlValue,
                ProjectId = queryData.ProjectId
            };

            _context.Queries.Add(query);

            foreach (var queryParameter in queryData.Parameters)
            {
                var parameter = new QueryParameter
                {
                    QueryId = query.Id,
                    Description = queryParameter.Description,
                    Name = queryParameter.Name,
                    Type = queryParameter.Type,
                    Placeholder = queryParameter.Placeholder,
                };

                _context.QueryParameters.Add(parameter);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task DeleteQueryAsync(int queryId, CancellationToken cancellationToken)
        {
            var query = await _context.Queries
                .Include(x => x.Parameters)
                .Where(x => x.Id == queryId)
                .SingleAsync(cancellationToken);

            if (query.Subscriptions.Count > 0)
            {
                throw new SemanticoException($"Unable to remove query due to active subscriptions.");
            }

            query.Archive();

            foreach (var param in query.Parameters)
            {
                param.Archive();
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<QueryData>> GetQueriesAsync(int? queryId, int? projectId, CancellationToken cancellationToken)
        {
            return await _context.Queries
                .WhereIf(queryId.HasValue, x => x.Id == queryId)
                .WhereIf(projectId.HasValue, x => x.ProjectId == projectId)
                .Select(x =>
                    new QueryData
                    {
                        QueryId = x.Id,
                        SqlValue = x.SqlValue,
                        ProjectId = x.ProjectId,
                        Parameters = x.Parameters.Select(y =>
                            new QueryParameterData
                            {
                                Name = y.Name,
                                Type = y.Type,
                                Description = y.Description,
                                Placeholder = y.Placeholder
                            }).ToList()
                    })
                .ToListAsync(cancellationToken);
        }

        public async Task UpdateQueryAsync(QueryData queryData, CancellationToken cancellationToken)
        {
            var query = await _context.Queries
                .Include(query => query.Parameters)
                .Where(x => x.Id == queryData.QueryId)
                .SingleAsync(cancellationToken);

            QueryValidator.CheckForFlaggedWords(queryData.SqlValue);
            QueryValidator.CheckForParameters(queryData.SqlValue, queryData.Parameters);

            query.SqlValue = queryData.SqlValue;

            foreach (var queryParameter in query.Parameters)
            {
                queryParameter.Archive();
            }

            foreach (var queryParameter in queryData.Parameters)
            {
                var queryParam = new QueryParameter
                {
                    QueryId = query.Id,
                    Type = queryParameter.Type,
                    Name = queryParameter.Name,
                    Placeholder = queryParameter.Placeholder,
                    Description = queryParameter.Description,
                };

                _context.QueryParameters.Add(queryParam);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
