using System;
using System.Data;
using System.Xml.Linq;
using System.Globalization;
using System.Linq;

namespace TallyDbLoader.Core.Tally
{
    public static class DynamicXmlParser
    {
        public static DataTable ParseXml(string xmlContent, TableConfig tableConfig)
        {
            var dataTable = new DataTable(tableConfig.Name);
            
            // Build the DataTable schema based on Fields
            foreach (var field in tableConfig.Fields)
            {
                var columnType = typeof(string);
                if (field.Type == "logical")
                {
                    columnType = typeof(bool);
                }
                else if (field.Type == "date")
                {
                    columnType = typeof(DateTime);
                }
                else if (field.Type == "number" || field.Type == "amount" || field.Type == "quantity" || field.Type == "rate")
                {
                    columnType = typeof(decimal);
                }
                
                var column = new DataColumn(field.Name, columnType);
                if (field.Type == "date")
                {
                    column.AllowDBNull = true;
                }
                dataTable.Columns.Add(column);
            }
            
            if (string.IsNullOrEmpty(xmlContent))
            {
                return dataTable;
            }
            
            try
            {
                var doc = XDocument.Parse(xmlContent);
                // Find all elements that contain a child element named "F01"
                var rowElements = doc.Descendants().Where(e => e.Element("F01") != null);
                
                foreach (var rowEl in rowElements)
                {
                    var row = dataTable.NewRow();
                    for (int i = 0; i < tableConfig.Fields.Count; i++)
                    {
                        var field = tableConfig.Fields[i];
                        var tag = $"F{(i + 1):D2}";
                        var valStr = rowEl.Element(tag)?.Value;
                        
                        if (valStr == null)
                        {
                            row[field.Name] = DBNull.Value;
                            continue;
                        }
                        
                        // Parse values based on type
                        if (field.Type == "logical")
                        {
                            row[field.Name] = valStr == "1" || valStr.Equals("true", StringComparison.OrdinalIgnoreCase) || valStr.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (field.Type == "date")
                        {
                            if (string.IsNullOrEmpty(valStr) || valStr.Contains("ñ") || valStr == "0")
                            {
                                row[field.Name] = DBNull.Value;
                            }
                            else if (DateTime.TryParse(valStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                            {
                                row[field.Name] = parsedDate;
                            }
                            else
                            {
                                row[field.Name] = DBNull.Value;
                            }
                        }
                        else if (field.Type == "number" || field.Type == "amount" || field.Type == "quantity" || field.Type == "rate")
                        {
                            if (decimal.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDecimal))
                            {
                                row[field.Name] = parsedDecimal;
                            }
                            else
                            {
                                row[field.Name] = 0m;
                            }
                        }
                        else // text
                        {
                            row[field.Name] = valStr;
                        }
                    }
                    dataTable.Rows.Add(row);
                }
            }
            catch
            {
                // Fallback gracefully on parsing errors
            }
            
            return dataTable;
        }
    }
}
