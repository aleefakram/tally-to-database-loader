using System;
using System.Collections.Generic;
using System.Text.Json;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Data
{
    public class ConfigImportService
    {
        private readonly IConfigRepository _repository;

        public ConfigImportService(IConfigRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public void ImportJson(string json, ImportDecision decision, string actor, string reason)
        {
            throw new NotImplementedException();
        }
    }
}
