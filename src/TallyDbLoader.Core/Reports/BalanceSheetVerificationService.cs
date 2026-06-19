using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TallyDbLoader.Core.Data;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Reports
{
    public class BalanceSheetVerificationService
    {
        private readonly IConfigRepository _repo;

        public BalanceSheetVerificationService(IConfigRepository repo)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        }

        public async Task<BalanceSheetReport> GenerateAsync(
            BalanceSheetVerificationRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var company = _repo.GetCompanyProfileById(request.CompanyProfileId);
            if (company == null)
            {
                return Failed(request, string.Empty, $"Sync Job with ID {request.CompanyProfileId} was not found.");
            }

            var db = company.Db ?? _repo.GetDatabaseProfileById(company.DbProfileId);
            if (db == null)
            {
                return Failed(request, company.Name, $"Database Profile with ID {company.DbProfileId} was not found.");
            }

            string provider = GetProviderName(db.Technology);
            var names = BalanceSheetTableNames.Create(company.Schema, company.TablePrefix, provider);
            var adapter = CreateAdapter(db.Technology);
            var targetIdentity = $"{db.Technology}:{company.TargetCatalog}:{company.Schema}:{company.TablePrefix}";

            try
            {
                await using var conn = await DatabaseWriter.GetConnectionAsync(db, company.TargetCatalog, cancellationToken);
                var raw = await adapter.QueryAsync(conn, names, request, cancellationToken);
                var report = BalanceSheetCalculator.Calculate(company.Name, raw, request);
                report.GeneratedAt = DateTime.UtcNow;

                _repo.AddBalanceSheetVerificationRun(ToHistory(report, targetIdentity));
                return report;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var report = Failed(request, company.Name, ex.Message);
                _repo.AddBalanceSheetVerificationRun(ToHistory(report, targetIdentity));
                return report;
            }
        }

        private static BalanceSheetReport Failed(BalanceSheetVerificationRequest request, string companyName, string error)
        {
            return new BalanceSheetReport
            {
                CompanyProfileId = request.CompanyProfileId,
                CompanyName = companyName,
                FinancialYearStart = request.FinancialYearStart,
                AsAtDate = request.AsAtDate,
                BalanceTolerance = request.Options.BalanceTolerance,
                Status = "failed",
                ErrorSummary = error,
                GeneratedAt = DateTime.UtcNow
            };
        }

        private static BalanceSheetVerificationRun ToHistory(BalanceSheetReport report, string targetIdentity)
        {
            return new BalanceSheetVerificationRun
            {
                CompanyProfileId = report.CompanyProfileId,
                TargetIdentity = targetIdentity,
                FinancialYearStart = report.FinancialYearStart,
                AsAtDate = report.AsAtDate,
                GeneratedAt = report.GeneratedAt,
                LiabilityTotal = report.LiabilityTotal,
                AssetTotal = report.AssetTotal,
                Difference = report.Difference,
                BalanceTolerance = report.BalanceTolerance,
                Status = report.Status,
                WarningSummary = report.Warnings.Count == 0 ? null : string.Join("; ", report.Warnings),
                ErrorSummary = report.ErrorSummary
            };
        }

        private static string GetProviderName(string technology)
        {
            if (technology.Equals("sqlite", StringComparison.OrdinalIgnoreCase)) return "SqliteConnection";
            if (technology.Equals("mssql", StringComparison.OrdinalIgnoreCase)) return "SqlConnection";
            if (technology.Equals("postgres", StringComparison.OrdinalIgnoreCase)) return "NpgsqlConnection";
            if (technology.Equals("mysql", StringComparison.OrdinalIgnoreCase)) return "MySqlConnection";
            return technology;
        }

        private static IBalanceSheetQueryAdapter CreateAdapter(string technology)
        {
            if (technology.Equals("sqlite", StringComparison.OrdinalIgnoreCase)) return new SqliteBalanceSheetQueryAdapter();
            if (technology.Equals("mssql", StringComparison.OrdinalIgnoreCase)) return new MssqlBalanceSheetQueryAdapter();
            if (technology.Equals("postgres", StringComparison.OrdinalIgnoreCase)) return new PostgresBalanceSheetQueryAdapter();
            if (technology.Equals("mysql", StringComparison.OrdinalIgnoreCase)) return new MySqlBalanceSheetQueryAdapter();
            throw new NotSupportedException($"Database technology '{technology}' is not supported.");
        }
    }
}
