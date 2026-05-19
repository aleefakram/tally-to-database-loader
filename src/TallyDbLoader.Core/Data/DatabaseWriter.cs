using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using Npgsql;
using TallyDbLoader.Core.Models;

namespace TallyDbLoader.Core.Data
{
    public static class DatabaseWriter
    {
        private static IDbConnection GetConnection(DatabaseProfile profile, string catalog)
        {
            if (profile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
            {
                string connStr = $"Host={profile.Server};Port={profile.Port};Username={profile.Username};Password={profile.Password};Database={catalog};";
                var conn = new NpgsqlConnection(connStr);
                conn.Open();
                return conn;
            }
            else if (profile.Technology.Equals("mssql", StringComparison.OrdinalIgnoreCase))
            {
                string connStr = $"Server={profile.Server},{profile.Port};User Id={profile.Username};Password={profile.Password};Database={catalog};TrustServerCertificate=True;";
                var conn = new SqlConnection(connStr);
                conn.Open();
                return conn;
            }
            throw new NotSupportedException($"Database technology '{profile.Technology}' is not supported.");
        }

        public static void InitializeTargetTables(DatabaseProfile profile, string catalog)
        {
            using (var conn = GetConnection(profile, catalog))
            {
                using (var cmd = conn.CreateCommand())
                {
                    if (profile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                    {
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS ledgers (
                                guid TEXT PRIMARY KEY,
                                name TEXT NOT NULL,
                                parent TEXT,
                                opening_balance NUMERIC,
                                closing_balance NUMERIC
                            );
                            CREATE TABLE IF NOT EXISTS vouchers (
                                guid TEXT PRIMARY KEY,
                                date TEXT,
                                voucher_number TEXT,
                                voucher_type TEXT,
                                amount NUMERIC
                            );";
                    }
                    else if (profile.Technology.Equals("mssql", StringComparison.OrdinalIgnoreCase))
                    {
                        cmd.CommandText = @"
                            IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ledgers' AND xtype='U')
                            CREATE TABLE ledgers (
                                guid VARCHAR(100) PRIMARY KEY,
                                name VARCHAR(255) NOT NULL,
                                parent VARCHAR(255),
                                opening_balance DECIMAL(18,2),
                                closing_balance DECIMAL(18,2)
                              );
                            IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='vouchers' AND xtype='U')
                            CREATE TABLE vouchers (
                                guid VARCHAR(100) PRIMARY KEY,
                                date VARCHAR(50),
                                voucher_number VARCHAR(100),
                                voucher_type VARCHAR(100),
                                amount DECIMAL(18,2)
                            );";
                    }
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static void WriteLedgers(DatabaseProfile profile, string catalog, List<Ledger> ledgers)
        {
            using (var conn = GetConnection(profile, catalog))
            {
                foreach (var ledger in ledgers)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        if (profile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                        {
                            cmd.CommandText = @"
                                INSERT INTO ledgers (guid, name, parent, opening_balance, closing_balance)
                                VALUES (@guid, @name, @parent, @opening_balance, @closing_balance)
                                ON CONFLICT (guid) DO UPDATE 
                                SET name = EXCLUDED.name, parent = EXCLUDED.parent, 
                                    opening_balance = EXCLUDED.opening_balance, closing_balance = EXCLUDED.closing_balance;";
                        }
                        else
                        {
                            cmd.CommandText = @"
                                MERGE ledgers AS target
                                USING (SELECT @guid AS guid, @name AS name, @parent AS parent, @opening_balance AS opening_balance, @closing_balance AS closing_balance) AS source
                                ON (target.guid = source.guid)
                                WHEN MATCHED THEN
                                    UPDATE SET name = source.name, parent = source.parent, 
                                               opening_balance = source.opening_balance, closing_balance = source.closing_balance
                                WHEN NOT MATCHED THEN
                                    INSERT (guid, name, parent, opening_balance, closing_balance)
                                    VALUES (source.guid, source.name, source.parent, source.opening_balance, source.closing_balance);";
                        }

                        AddParameter(cmd, "@guid", ledger.Guid);
                        AddParameter(cmd, "@name", ledger.Name);
                        AddParameter(cmd, "@parent", ledger.Parent);
                        AddParameter(cmd, "@opening_balance", ledger.OpeningBalance);
                        AddParameter(cmd, "@closing_balance", ledger.ClosingBalance);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void AddParameter(IDbCommand cmd, string name, object? value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }
    }
}
