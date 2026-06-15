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

        private class ExportEnvelope
        {
            public string Format { get; set; } = "";
            public int Schema_Version { get; set; }
            public string Application_Version { get; set; } = "";
            public ExportPayload? Payload { get; set; }
        }

        private class ExportPayload
        {
            public List<ExportDatabaseProfile>? Database_Profiles { get; set; }
            public List<ExportCompanyProfile>? Company_Profiles { get; set; }
        }

        private class ExportDatabaseProfile
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string Technology { get; set; } = "postgres";
            public string Server { get; set; } = "";
            public int Port { get; set; }
            public string Username { get; set; } = "";
            public bool Has_Password { get; set; }
        }

        private class ExportCompanyProfile
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string? Tally_Guid { get; set; }
            public bool Consolidated { get; set; }
            public string? Books_From { get; set; }
            public string? Books_To { get; set; }
            public int Db_Profile_Id { get; set; }
            public string Target_Catalog { get; set; } = "";
            public string Schema { get; set; } = "public";
            public string Table_Prefix { get; set; } = "";
            public string Mode { get; set; } = "full";
            public int Interval_Minutes { get; set; }
            public bool Enabled { get; set; }
            public bool Notify_On_Error { get; set; }
            public bool Pause_On_Tally_Close { get; set; }
            public int Entity_Flags { get; set; }
        }

        public void ImportJson(string json, ImportDecision decision, string actor, string reason)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON content cannot be null or empty.", nameof(json));
            if (decision == null)
                throw new ArgumentNullException(nameof(decision));
            if (string.IsNullOrWhiteSpace(actor))
                throw new ArgumentException("Actor cannot be null or empty.", nameof(actor));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Reason cannot be null or empty.", nameof(reason));

            ExportEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? throw new InvalidOperationException("Failed to deserialize JSON.");
            }
            catch (Exception ex)
            {
                throw new ConfigImportValidationException(new[] { $"Invalid JSON content: {ex.Message}" });
            }

            var errors = new List<string>();

            if (envelope.Format != "tally-db-loader.config-export")
            {
                errors.Add("Unsupported or invalid format string.");
            }
            if (envelope.Schema_Version != 1)
            {
                errors.Add("Unsupported schema version. Only version 1 is supported.");
            }
            if (string.IsNullOrWhiteSpace(envelope.Application_Version))
            {
                errors.Add("Application version must be a non-empty string.");
            }
            if (envelope.Payload == null)
            {
                errors.Add("Configuration payload is missing or empty.");
            }

            if (errors.Count > 0)
                throw new ConfigImportValidationException(errors);

            throw new NotImplementedException("Task 4 completed, Task 5 pending.");
        }
    }
}
