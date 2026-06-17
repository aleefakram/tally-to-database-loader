using System;
using System.Data;
using System.Xml;
using System.IO;
using System.Globalization;
using System.Text;
using TallyDbLoader.Core.Logging;

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

            xmlContent = XmlSanitizer.Sanitize(xmlContent);
            
            using (var sr = new StringReader(xmlContent))
            using (var reader = XmlReader.Create(sr))
            {
                string?[] rowValues = new string?[tableConfig.Fields.Count];
                bool inRow = false;
                int activeFieldIdx = -1;
                
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        string name = reader.Name;
                        
                        if (name == "F01")
                        {
                            if (inRow)
                            {
                                AddRowToTable(dataTable, rowValues, tableConfig);
                            }
                            // Reset row values
                            for (int j = 0; j < rowValues.Length; j++) rowValues[j] = null;
                            inRow = true;
                        }
                        
                        if (inRow && name.Length > 1 && name.StartsWith("F") && char.IsDigit(name[1]))
                        {
                            if (int.TryParse(name.Substring(1), out int fieldIdx))
                            {
                                if (fieldIdx >= 1 && fieldIdx <= tableConfig.Fields.Count)
                                {
                                    activeFieldIdx = fieldIdx - 1;
                                    if (reader.IsEmptyElement)
                                    {
                                        rowValues[activeFieldIdx] = "";
                                        activeFieldIdx = -1;
                                    }
                                }
                                else
                                {
                                    activeFieldIdx = -1;
                                }
                            }
                            else
                            {
                                activeFieldIdx = -1;
                            }
                        }
                        else
                        {
                            activeFieldIdx = -1;
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                    {
                        if (activeFieldIdx >= 0 && activeFieldIdx < rowValues.Length)
                        {
                            rowValues[activeFieldIdx] = reader.Value;
                        }
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        string name = reader.Name;
                        if (inRow && !name.StartsWith("F"))
                        {
                            AddRowToTable(dataTable, rowValues, tableConfig);
                            inRow = false;
                        }
                        activeFieldIdx = -1;
                    }
                }
                
                if (inRow)
                {
                    bool hasAnyData = false;
                    for (int j = 0; j < rowValues.Length; j++)
                    {
                        if (rowValues[j] != null) { hasAnyData = true; break; }
                    }
                    if (hasAnyData)
                    {
                        AddRowToTable(dataTable, rowValues, tableConfig);
                    }
                }
            }
            
            return dataTable;
        }



        private static void AddRowToTable(DataTable dataTable, string?[] rowValues, TableConfig tableConfig)
        {
            var row = dataTable.NewRow();
            bool hasData = false;
            
            for (int i = 0; i < tableConfig.Fields.Count; i++)
            {
                var field = tableConfig.Fields[i];
                var valStr = rowValues[i];
                
                if (valStr == null)
                {
                    if (field.Type == "logical")
                    {
                        row[field.Name] = false;
                    }
                    else if (field.Type == "number" || field.Type == "amount" || field.Type == "quantity" || field.Type == "rate")
                    {
                        row[field.Name] = 0m;
                    }
                    else if (field.Type == "date")
                    {
                        row[field.Name] = DBNull.Value;
                    }
                    else // text
                    {
                        row[field.Name] = "";
                    }
                    continue;
                }
                
                hasData = true;
                
                if (field.Type == "logical")
                {
                    row[field.Name] = valStr == "1" || valStr.Equals("true", StringComparison.OrdinalIgnoreCase) || valStr.Equals("yes", StringComparison.OrdinalIgnoreCase);
                }
                else if (field.Type == "date")
                {
                    if (string.IsNullOrEmpty(valStr) || valStr.Trim() == "" || valStr.Contains("ñ") || valStr == "0")
                    {
                        row[field.Name] = DBNull.Value;
                    }
                    else if (DateTime.TryParse(valStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    {
                        row[field.Name] = parsedDate;
                    }
                    else
                    {
                        throw new FormatException($"Failed to parse date value '{valStr}' for field '{field.Name}'.");
                    }
                }
                else if (field.Type == "number" || field.Type == "amount" || field.Type == "quantity" || field.Type == "rate")
                {
                    if (string.IsNullOrEmpty(valStr) || valStr.Trim() == "")
                    {
                        row[field.Name] = DBNull.Value;
                    }
                    else if (decimal.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDecimal))
                    {
                        row[field.Name] = parsedDecimal;
                    }
                    else
                    {
                        throw new FormatException($"Failed to parse numeric value '{valStr}' for field '{field.Name}'.");
                    }
                }
                else
                {
                    row[field.Name] = valStr;
                }
            }
            
            if (hasData)
            {
                dataTable.Rows.Add(row);
            }
        }
    }
}
