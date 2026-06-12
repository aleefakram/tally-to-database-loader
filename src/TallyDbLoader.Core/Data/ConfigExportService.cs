using System;
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
            throw new NotImplementedException();
        }
    }
}
