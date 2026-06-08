using System;
using System.Collections.Generic;
using Xunit;
using TallyDbLoader.Core.Sync;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Tests
{
    public class DbIdentifierPolicyTests
    {
        [Theory]
        [InlineData("valid_name", "SqliteConnection")]
        [InlineData("validName123", "NpgsqlConnection")]
        [InlineData("_another_one", "SqlConnection")]
        public void Validate_WithValidIdentifiers_DoesNotThrow(string identifier, string provider)
        {
            DbIdentifierPolicy.Validate(identifier, provider);
        }

        [Theory]
        [InlineData("select", "SqliteConnection")]
        [InlineData("from", "NpgsqlConnection")]
        [InlineData("table", "SqlConnection")]
        public void Validate_WithReservedKeywords_ThrowsInvalidOperationException(string identifier, string provider)
        {
            Assert.Throws<InvalidOperationException>(() => DbIdentifierPolicy.Validate(identifier, provider));
        }

        [Theory]
        [InlineData("123invalid", "SqliteConnection")]
        [InlineData("invalid-hyphen", "NpgsqlConnection")]
        [InlineData("invalid spaces", "SqlConnection")]
        [InlineData("invalid.dot", "MySqlConnection")]
        public void Validate_WithInvalidCharacters_ThrowsInvalidOperationException(string identifier, string provider)
        {
            Assert.Throws<InvalidOperationException>(() => DbIdentifierPolicy.Validate(identifier, provider));
        }

        [Fact]
        public void Validate_ExceedingLengthLimit_ThrowsInvalidOperationException()
        {
            // PostgreSQL limit is 63 chars
            var longPostgresIdentifier = new string('a', 64);
            Assert.Throws<InvalidOperationException>(() => DbIdentifierPolicy.Validate(longPostgresIdentifier, "NpgsqlConnection"));

            // MySQL limit is 64 chars
            var longMysqlIdentifier = new string('a', 65);
            Assert.Throws<InvalidOperationException>(() => DbIdentifierPolicy.Validate(longMysqlIdentifier, "MySqlConnection"));

            // MSSQL limit is 128 chars
            var longMssqlIdentifier = new string('a', 129);
            Assert.Throws<InvalidOperationException>(() => DbIdentifierPolicy.Validate(longMssqlIdentifier, "SqlConnection"));

            // MSSQL 128-char identifier should NOT throw for SqlConnection
            var validMssqlIdentifier = new string('a', 128);
            DbIdentifierPolicy.Validate(validMssqlIdentifier, "SqlConnection");
        }

        [Fact]
        public void ValidateTableConfig_WithColumnCollisions_ThrowsInvalidOperationException()
        {
            var config = new TableConfig
            {
                Name = "valid_table",
                Fields = new List<FieldConfig>
                {
                    new() { Name = "my_column" },
                    new() { Name = "MY_COLUMN" } // collision
                }
            };

            Assert.Throws<InvalidOperationException>(() => DbIdentifierPolicy.ValidateTableConfig(config, "SqliteConnection"));
        }

        [Fact]
        public void ValidateTableConfig_StagingTableNameExceedingLimit_ThrowsInvalidOperationException()
        {
            // For PostgreSQL, limit is 63.
            // Staging prefix is "__tally_fullsync_staging_" (25 chars).
            // So a table name of 39 chars makes staging table name 64 chars, exceeding 63.
            var tableName = new string('a', 39);
            var config = new TableConfig
            {
                Name = tableName,
                Fields = new List<FieldConfig> { new() { Name = "id" } }
            };

            Assert.Throws<InvalidOperationException>(() => DbIdentifierPolicy.ValidateTableConfig(config, "NpgsqlConnection"));
        }
    }
}
