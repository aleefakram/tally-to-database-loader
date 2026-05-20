using System;
using System.Text;
using TallyDbLoader.Core.Tally;

namespace TallyDbLoader.Core.Data
{
    public static class DynamicTableSchemaGenerator
    {
        public static string GenerateCreateTableSql(TableConfig tableConfig, string technology)
        {
            var tech = technology.ToLower();
            var sb = new StringBuilder();
            
            var isMssql = tech.Contains("mssql") || tech.Contains("sqlserver");
            var isMysql = tech.Contains("mysql");
            var isPostgres = tech.Contains("postgres") || tech.Contains("npgsql");
            
            if (isMssql)
            {
                sb.Append($"IF OBJECT_ID('{tableConfig.Name}', 'U') IS NULL CREATE TABLE {tableConfig.Name} (");
            }
            else
            {
                sb.Append($"CREATE TABLE IF NOT EXISTS {tableConfig.Name} (");
            }
            
            for (int i = 0; i < tableConfig.Fields.Count; i++)
            {
                var field = tableConfig.Fields[i];
                var name = field.Name;
                var type = field.Type.ToLower();
                
                string sqlColumnType;
                if (name.Equals("guid", StringComparison.OrdinalIgnoreCase))
                {
                    sqlColumnType = "varchar(64) not null primary key";
                }
                else if (name.Equals("alterid", StringComparison.OrdinalIgnoreCase))
                {
                    sqlColumnType = "int not null default 0";
                }
                else if (type == "logical")
                {
                    if (isMysql) sqlColumnType = "tinyint default 0";
                    else sqlColumnType = "smallint default 0";
                }
                else if (type == "date")
                {
                    sqlColumnType = "date";
                }
                else if (type == "number" || type == "amount" || type == "quantity" || type == "rate")
                {
                    sqlColumnType = "decimal(17,2) default 0";
                }
                else // text
                {
                    if (isMssql) sqlColumnType = "nvarchar(1024) not null default ''";
                    else sqlColumnType = "varchar(1024) not null default ''";
                }
                
                sb.Append($"{name} {sqlColumnType}");
                if (i < tableConfig.Fields.Count - 1)
                {
                    sb.Append(", ");
                }
            }
            
            sb.Append(");");
            return sb.ToString();
        }
    }
}
