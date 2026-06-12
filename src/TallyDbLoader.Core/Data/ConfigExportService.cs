using System;
using System.Text.Json;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Data
{
    public sealed class ConfigExportService
    {
        private readonly IConfigRepository _repository;
        private readonly string _applicationVersion;

        public ConfigExportService(IConfigRepository repository, string applicationVersion)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(applicationVersion))
            {
                throw new ArgumentException("Application version cannot be null, empty, or whitespace.", nameof(applicationVersion));
            }
            _applicationVersion = applicationVersion;
        }

        public string ExportJson(DateTimeOffset exportedAt)
        {
            var envelope = new
            {
                format = "tally-db-loader.config-export",
                schema_version = 1,
                application_version = _applicationVersion,
                exported_at = exportedAt.ToString("o"),
                payload = new
                {
                    database_profiles = new object[0],
                    company_profiles = new object[0]
                }
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            return JsonSerializer.Serialize(envelope, options);
        }
    }
}
