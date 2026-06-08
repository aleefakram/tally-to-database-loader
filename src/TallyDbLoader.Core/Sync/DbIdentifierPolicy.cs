using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Sync
{
    public static class DbIdentifierPolicy
    {
        private static readonly Regex IdentifierRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

        private static readonly HashSet<string> ReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "select", "insert", "update", "delete", "from", "where", "join", "table", "column", "index",
            "create", "drop", "alter", "add", "and", "or", "not", "null", "true", "false", "into", "values",
            "primary", "key", "foreign", "constraint", "references", "default", "unique", "check"
        };

        public static int GetMaxLength(string provider)
        {
            if (string.IsNullOrEmpty(provider)) return 64;
            var lower = provider.ToLowerInvariant();
            if (lower.Contains("sqlite")) return 255;
            if (lower.Contains("postgres") || lower.Contains("npgsql")) return 63;
            if (lower.Contains("mysql")) return 64;
            if (lower.Contains("mssql") || lower.Contains("sqlserver") || lower.Contains("sqlconnection")) return 128;
            return 64;
        }

        public static void Validate(string identifier, string provider)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentException("Identifier cannot be null or empty.");
            }

            // Check pattern
            if (!IdentifierRegex.IsMatch(identifier))
            {
                throw new InvalidOperationException($"Identifier '{identifier}' is invalid. It must start with a letter or underscore and contain only alphanumeric characters and underscores.");
            }

            // Check length
            var maxLength = GetMaxLength(provider);
            if (identifier.Length > maxLength)
            {
                throw new InvalidOperationException($"Identifier '{identifier}' exceeds the maximum allowed length of {maxLength} characters for provider '{provider}'.");
            }

            // Check reserved keywords
            if (ReservedKeywords.Contains(identifier))
            {
                throw new InvalidOperationException($"Identifier '{identifier}' is a reserved SQL keyword and cannot be used.");
            }
        }

        public static void ValidateTableConfig(TableConfig config, string provider)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            // Validate table name
            Validate(config.Name, provider);

            // Staging table name validation
            var stagingTableName = $"__tally_fullsync_staging_{config.Name}";
            var maxLength = GetMaxLength(provider);
            if (stagingTableName.Length > maxLength)
            {
                throw new InvalidOperationException($"Staging table name '{stagingTableName}' exceeds the maximum allowed length of {maxLength} characters for provider '{provider}'.");
            }

            // Validate column/field names and check for collisions
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.Fields != null)
            {
                foreach (var field in config.Fields)
                {
                    Validate(field.Name, provider);

                    if (seenNames.Contains(field.Name))
                    {
                        throw new InvalidOperationException($"Case-insensitive collision detected for column name '{field.Name}' in table '{config.Name}'.");
                    }
                    seenNames.Add(field.Name);
                }
            }
        }
    }
}
