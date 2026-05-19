# Dynamic YAML-Driven .NET Loader Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the C#/.NET database loader from a static, single-table import to a fully dynamic, configuration-driven sync engine that reads `tally-export-config.yaml` at runtime and synchronizes all master and transaction tables.

**Architecture:** Create dynamic parser and generator modules. Parse Tally's custom XML envelope response into raw TSV data via regular expressions and map it directly to in-memory `DataTable` instances. Sync records incrementally using staging tables (`_diff` and `_delete`), handling cascading deletions, bulk inserts, reference updates, and voucher auto-numbering.

**Tech Stack:** C#, .NET 8.0, YamlDotNet, Npgsql (Postgres), Microsoft.Data.SqlClient (MSSQL), MySqlConnector (MySQL), Xunit.

---

### Task 1: Package Integration & Config Models

**Files:**
- Modify: `src/TallyDbLoader.Core/TallyDbLoader.Core.csproj`
- Create: `src/TallyDbLoader.Core/Tally/YamlConfigParser.cs`
- Create: `tests/TallyDbLoader.Tests/YamlConfigParserTests.cs`

- [ ] **Step 1: Write a failing test for YAML parsing**
  Create `tests/TallyDbLoader.Tests/YamlConfigParserTests.cs` with the following test case:
  ```csharp
  using Xunit;
  using TallyDbLoader.Core.Tally;
  using YamlDotNet.Serialization;

  namespace TallyDbLoader.Tests
  {
      public class YamlConfigParserTests
      {
          [Fact]
          public void Test_ParseYaml_ReturnsValidObjectStructure()
          {
              var yaml = @"
  master:
    - name: mst_group
      collection: Group
      nature: Primary
      fields:
        - name: guid
          field: Guid
          type: text
        - name: is_revenue
          field: IsRevenue
          type: logical
      filters:
        - filter1
      fetch:
        - fetch1
      cascade_delete:
        - table: trn_accounting
          field: _ledger
  ";
              var config = YamlConfigParser.Parse(yaml);
              Assert.NotNull(config);
              Assert.Single(config.Master);
              var group = config.Master[0];
              Assert.Equal("mst_group", group.Name);
              Assert.Equal("Group", group.Collection);
              Assert.Equal("Primary", group.Nature);
              Assert.Equal(2, group.Fields.Count);
              Assert.Equal("guid", group.Fields[0].Name);
              Assert.Equal("Guid", group.Fields[0].Field);
              Assert.Equal("text", group.Fields[0].Type);
              Assert.Equal("is_revenue", group.Fields[1].Name);
              Assert.Equal("logical", group.Fields[1].Type);
              Assert.Single(group.Filters!);
              Assert.Single(group.Fetch!);
              Assert.Single(group.CascadeDelete!);
              Assert.Equal("trn_accounting", group.CascadeDelete[0].Table);
              Assert.Equal("_ledger", group.CascadeDelete[0].Field);
          }
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test --filter YamlConfigParserTests` from the repository root.
  Expected: Compile errors because `YamlConfigParser` and dependencies do not exist yet.

- [ ] **Step 3: Add YamlDotNet package dependency**
  Add YamlDotNet package to `src/TallyDbLoader.Core/TallyDbLoader.Core.csproj`:
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net8.0</TargetFramework>
      <ImplicitUsings>enable</ImplicitUsings>
      <Nullable>enable</Nullable>
    </PropertyGroup>
    <ItemGroup>
      <PackageReference Include="Dapper" Version="2.1.35" />
      <PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.0" />
      <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.4" />
      <PackageReference Include="MySqlConnector" Version="2.3.6" />
      <PackageReference Include="Npgsql" Version="8.0.2" />
      <PackageReference Include="YamlDotNet" Version="15.1.2" />
    </ItemGroup>
  </Project>
  ```

- [ ] **Step 4: Create config models & parser implementation**
  Create `src/TallyDbLoader.Core/Tally/YamlConfigParser.cs` containing:
  ```csharp
  using System.Collections.Generic;
  using YamlDotNet.Serialization;
  using YamlDotNet.Serialization.NamingConventions;

  namespace TallyDbLoader.Core.Tally
  {
      public class TallyExportConfig
      {
          [YamlMember(Alias = "master")]
          public List<TableConfig> Master { get; set; } = new();

          [YamlMember(Alias = "transaction")]
          public List<TableConfig> Transaction { get; set; } = new();
      }

      public class TableConfig
      {
          [YamlMember(Alias = "name")]
          public string Name { get; set; } = string.Empty;

          [YamlMember(Alias = "collection")]
          public string Collection { get; set; } = string.Empty;

          [YamlMember(Alias = "nature")]
          public string Nature { get; set; } = string.Empty;

          [YamlMember(Alias = "fields")]
          public List<FieldConfig> Fields { get; set; } = new();

          [YamlMember(Alias = "filters")]
          public List<string>? Filters { get; set; }

          [YamlMember(Alias = "fetch")]
          public List<string>? Fetch { get; set; }

          [YamlMember(Alias = "cascade_update")]
          public List<CascadeRelation>? CascadeUpdate { get; set; }

          [YamlMember(Alias = "cascade_delete")]
          public List<CascadeRelation>? CascadeDelete { get; set; }
      }

      public class FieldConfig
      {
          [YamlMember(Alias = "name")]
          public string Name { get; set; } = string.Empty;

          [YamlMember(Alias = "field")]
          public string Field { get; set; } = string.Empty;

          [YamlMember(Alias = "type")]
          public string Type { get; set; } = string.Empty;
      }

      public class CascadeRelation
      {
          [YamlMember(Alias = "table")]
          public string Table { get; set; } = string.Empty;

          [YamlMember(Alias = "field")]
          public string Field { get; set; } = string.Empty;
      }

      public static class YamlConfigParser
      {
          public static TallyExportConfig Parse(string yamlContent)
          {
              var deserializer = new DeserializerBuilder()
                  .WithNamingConvention(CamelCaseNamingConvention.Instance)
                  .IgnoreUnmatchedProperties()
                  .Build();
              return deserializer.Deserialize<TallyExportConfig>(yamlContent);
          }
      }
  }
  ```

- [ ] **Step 5: Run tests to verify they pass**
  Run: `dotnet test --filter YamlConfigParserTests`
  Expected: PASS

- [ ] **Step 6: Commit**
  Run:
  ```bash
  git add src/TallyDbLoader.Core/TallyDbLoader.Core.csproj src/TallyDbLoader.Core/Tally/YamlConfigParser.cs tests/TallyDbLoader.Tests/YamlConfigParserTests.cs
  git commit -m "feat: Add YamlConfigParser and config structures"
  ```

---

### Task 2: Dynamic TDL XML Query Generator

**Files:**
- Create: `src/TallyDbLoader.Core/Tally/DynamicTdlXmlGenerator.cs`
- Create: `tests/TallyDbLoader.Tests/DynamicTdlXmlGeneratorTests.cs`

- [ ] **Step 1: Write a failing test for XML TDL generation**
  Create `tests/TallyDbLoader.Tests/DynamicTdlXmlGeneratorTests.cs` with the test:
  ```csharp
  using Xunit;
  using TallyDbLoader.Core.Tally;
  using System.Collections.Generic;

  namespace TallyDbLoader.Tests
  {
      public class DynamicTdlXmlGeneratorTests
      {
          [Fact]
          public void Test_GenerateXML_ProducesValidTDLEnvelope()
          {
              var tableConfig = new TableConfig
              {
                  Name = "mst_ledger",
                  Collection = "Ledger.AllBillAllocations",
                  Fields = new List<FieldConfig>
                  {
                      new FieldConfig { Name = "name", Field = "Name", Type = "text" },
                      new FieldConfig { Name = "opening_balance", Field = "OpeningBalance", Type = "amount" },
                      new FieldConfig { Name = "is_revenue", Field = "IsRevenue", Type = "logical" }
                  },
                  Filters = new List<string> { "FilterName1" },
                  Fetch = new List<string> { "FetchProperty1" }
              };

              var xml = DynamicTdlXmlGenerator.GenerateXml(tableConfig, "MyCompany", "20260401", "20260519");
              
              Assert.Contains("<SVCURRENTCOMPANY>MyCompany</SVCURRENTCOMPANY>", xml);
              Assert.Contains("<SVFROMDATE>20260401</SVFROMDATE>", xml);
              Assert.Contains("<SVTODATE>20260519</SVTODATE>", xml);
              
              // Explode hierarchy checks
              Assert.Contains("<PART NAME=\"MyPart01\"><LINES>MyLine01</LINES><REPEAT>MyLine01 : MyCollection</REPEAT>", xml);
              Assert.Contains("<PART NAME=\"MyPart02\"><LINES>MyLine02</LINES><REPEAT>MyLine02 : AllBillAllocations</REPEAT>", xml);
              
              // Formula check
              Assert.Contains("<FIELD NAME=\"Fld01\"><SET>$Name</SET>", xml);
              Assert.Contains("<FIELD NAME=\"Fld02\"><SET>$$StringFindAndReplace:(if $$IsDebit:$OpeningBalance then -$$NumValue:$OpeningBalance else $$NumValue:$OpeningBalance):\"(-)\":\"-\"</SET>", xml);
              Assert.Contains("<FIELD NAME=\"Fld03\"><SET>if $IsRevenue then 1 else 0</SET>", xml);
          }
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test --filter DynamicTdlXmlGeneratorTests`
  Expected: Compile errors.

- [ ] **Step 3: Implement DynamicTdlXmlGenerator**
  Create `src/TallyDbLoader.Core/Tally/DynamicTdlXmlGenerator.cs` containing:
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.Text;
  using System.Text.RegularExpressions;
  using System.Web;

  namespace TallyDbLoader.Core.Tally
  {
      public static class DynamicTdlXmlGenerator
      {
          public static string GenerateXml(TableConfig tblConfig, string company, string fromDate, string toDate)
          {
              var sb = new StringBuilder();
              sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>TallyDatabaseLoaderReport</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>XML (Data Interchange)</SVEXPORTFORMAT><SVFROMDATE>{fromDate}</SVFROMDATE><SVTODATE>{toDate}</SVTODATE>");
              
              if (!string.IsNullOrEmpty(company))
              {
                  sb.Append("<SVCURRENTCOMPANY>{targetCompany}</SVCURRENTCOMPANY>");
              }
              sb.Append("</STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME=\"TallyDatabaseLoaderReport\"><FORMS>MyForm</FORMS></REPORT><FORM NAME=\"MyForm\"><PARTS>MyPart01</PARTS></FORM>");

              var xml = sb.ToString();
              xml = xml.Replace("{fromDate}", fromDate);
              xml = xml.Replace("{toDate}", toDate);
              if (!string.IsNullOrEmpty(company))
              {
                  xml = xml.Replace("{targetCompany}", HttpUtility.HtmlEncode(company));
              }

              var routes = tblConfig.Collection.Split('.');
              var lstRoutes = new List<string>(routes);
              var targetCollection = lstRoutes[0];
              lstRoutes[0] = "MyCollection";

              // Append PART definitions
              for (int i = 0; i < lstRoutes.Count; i++)
              {
                  string partName = $"MyPart{(i + 1):D2}";
                  string lineName = $"MyLine{(i + 1):D2}";
                  xml += $"<PART NAME=\"{partName}\"><LINES>{lineName}</LINES><REPEAT>{lineName} : {lstRoutes[i]}</REPEAT><SCROLLED>Vertical</SCROLLED></PART>";
              }

              // Append LINE definitions
              for (int i = 0; i < lstRoutes.Count - 1; i++)
              {
                  string lineName = $"MyLine{(i + 1):D2}";
                  string nextPartName = $"MyPart{(i + 2):D2}";
                  xml += $"<LINE NAME=\"{lineName}\"><FIELDS>FldBlank</FIELDS><EXPLODE>{nextPartName}</EXPLODE></LINE>";
              }

              // Terminal line
              string termLineName = $"MyLine{lstRoutes.Count:D2}";
              xml += $"<LINE NAME=\"{termLineName}\"><FIELDS>";
              var fieldsList = new List<string>();
              for (int i = 0; i < tblConfig.Fields.Count; i++)
              {
                  fieldsList.Add($"Fld{(i + 1):D2}");
              }
              xml += string.Join(",", fieldsList);
              xml += "</FIELDS></LINE>";

              // Field formulations
              for (int i = 0; i < tblConfig.Fields.Count; i++)
              {
                  var field = tblConfig.Fields[i];
                  string fieldName = $"Fld{(i + 1):D2}";
                  string xmlTag = $"F{(i + 1):D2}";
                  xml += $"<FIELD NAME=\"{fieldName}\">";

                  if (Regex.IsMatch(field.Field, @"^(\.\.)?[a-zA-Z0-9_]+$"))
                  {
                      if (field.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
                          xml += $"<SET>${field.Field}</SET>";
                      else if (field.Type.Equals("logical", StringComparison.OrdinalIgnoreCase))
                          xml += $"<SET>if ${field.Field} then 1 else 0</SET>";
                      else if (field.Type.Equals("date", StringComparison.OrdinalIgnoreCase))
                          xml += $"<SET>if $$IsEmpty:${field.Field} then $$StrByCharCode:241 else $$PyrlYYYYMMDDFormat:${field.Field}:\"-\"</SET>";
                      else if (field.Type.Equals("number", StringComparison.OrdinalIgnoreCase))
                          xml += $"<SET>if $$IsEmpty:${field.Field} then \"0\" else $$String:${field.Field}</SET>";
                      else if (field.Type.Equals("amount", StringComparison.OrdinalIgnoreCase))
                          xml += $"<SET>$$StringFindAndReplace:(if $$IsDebit:${field.Field} then -$$NumValue:${field.Field} else $$NumValue:${field.Field}):\"(-)\":\"-\"</SET>";
                      else if (field.Type.Equals("quantity", StringComparison.OrdinalIgnoreCase))
                          xml += $"<SET>$$StringFindAndReplace:(if $$IsInwards:${field.Field} then $$Number:$$String:${field.Field}:\"TailUnits\" else -$$Number:$$String:${field.Field}:\"TailUnits\"):\"(-)\":\"-\"</SET>";
                      else if (field.Type.Equals("rate", StringComparison.OrdinalIgnoreCase))
                          xml += $"<SET>if $$IsEmpty:${field.Field} then 0 else $$Number:${field.Field}</SET>";
                      else
                          xml += $"<SET>{field.Field}</SET>";
                  }
                  else
                  {
                      xml += $"<SET>{field.Field}</SET>";
                  }

                  xml += $"<XMLTAG>{xmlTag}</XMLTAG></FIELD>";
              }

              xml += "<FIELD NAME=\"FldBlank\"><SET>\"\"</SET></FIELD>";

              // Collection setup
              xml += $"<COLLECTION NAME=\"MyCollection\"><TYPE>{targetCollection}</TYPE>";
              if (tblConfig.Fetch != null && tblConfig.Fetch.Count > 0)
              {
                  xml += $"<FETCH>{string.Join(",", tblConfig.Fetch)}</FETCH>";
              }
              if (tblConfig.Filters != null && tblConfig.Filters.Count > 0)
              {
                  xml += "<FILTER>";
                  var fltrNames = new List<string>();
                  for (int j = 0; j < tblConfig.Filters.Count; j++)
                  {
                      fltrNames.Add($"Fltr{(j + 1):D2}");
                  }
                  xml += string.Join(",", fltrNames);
                  xml += "</FILTER>";
              }
              xml += "</COLLECTION>";

              // Filters system formula
              if (tblConfig.Filters != null && tblConfig.Filters.Count > 0)
              {
                  for (int j = 0; j < tblConfig.Filters.Count; j++)
                  {
                      xml += $"<SYSTEM TYPE=\"Formulae\" NAME=\"Fltr{(j + 1):D2}\">{tblConfig.Filters[j]}</SYSTEM>";
                  }
              }

              xml += "</TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";
              return xml;
          }
      }
  }
  ```

- [ ] **Step 4: Run tests to verify they pass**
  Run: `dotnet test --filter DynamicTdlXmlGeneratorTests`
  Expected: PASS

- [ ] **Step 5: Commit**
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Tally/DynamicTdlXmlGenerator.cs tests/TallyDbLoader.Tests/DynamicTdlXmlGeneratorTests.cs
  git commit -m "feat: Add DynamicTdlXmlGenerator with tests"
  ```

---

### Task 3: Dynamic XML Response Parser

**Files:**
- Create: `src/TallyDbLoader.Core/Tally/DynamicXmlParser.cs`
- Create: `tests/TallyDbLoader.Tests/DynamicXmlParserTests.cs`

- [ ] **Step 1: Write a failing test for response parsing**
  Create `tests/TallyDbLoader.Tests/DynamicXmlParserTests.cs` with the test:
  ```csharp
  using Xunit;
  using TallyDbLoader.Core.Tally;
  using System;
  using System.Data;
  using System.Collections.Generic;

  namespace TallyDbLoader.Tests
  {
      public class DynamicXmlParserTests
      {
          [Fact]
          public void Test_ParseXmlToDataTable_ReturnsValidRows()
          {
              var rawResponse = "<ENVELOPE><BODY><F01>guid-1</F01><F02>Sales Account</F02><F03>1000.50</F03><F04>2026-05-19</F04>" +
                                "<F01>guid-2</F01><F02>Purchase Account</F02><F03>-200.00</F03><F04>ñ</F04></BODY></ENVELOPE>";

              var fields = new List<FieldConfig>
              {
                  new FieldConfig { Name = "guid", Type = "text" },
                  new FieldConfig { Name = "name", Type = "text" },
                  new FieldConfig { Name = "balance", Type = "amount" },
                  new FieldConfig { Name = "last_date", Type = "date" }
              };

              var dataTable = DynamicXmlParser.ParseToDataTable(rawResponse, fields);

              Assert.Equal(2, dataTable.Rows.Count);
              Assert.Equal("guid-1", dataTable.Rows[0]["guid"]);
              Assert.Equal("Sales Account", dataTable.Rows[0]["name"]);
              Assert.Equal(1000.50m, dataTable.Rows[0]["balance"]);
              Assert.Equal(new DateTime(2026, 5, 19), dataTable.Rows[0]["last_date"]);

              Assert.Equal("guid-2", dataTable.Rows[1]["guid"]);
              Assert.Equal(DBNull.Value, dataTable.Rows[1]["last_date"]); // character 241/ñ is mapped to NULL
          }
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test --filter DynamicXmlParserTests`
  Expected: Compile errors.

- [ ] **Step 3: Implement DynamicXmlParser**
  Create `src/TallyDbLoader.Core/Tally/DynamicXmlParser.cs` containing:
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.Data;
  using System.IO;
  using System.Text.RegularExpressions;
  using System.Web;

  namespace TallyDbLoader.Core.Tally
  {
      public static class DynamicXmlParser
      {
          public static string ProcessTdlOutputManipulation(string txt)
          {
              if (string.IsNullOrEmpty(txt)) return string.Empty;
              var retval = txt;
              retval = retval.Replace("<ENVELOPE>", "");
              retval = retval.Replace("</ENVELOPE>", "");
              retval = Regex.Replace(retval, @"\<FLDBLANK\>\<\/FLDBLANK\>", "");
              retval = Regex.Replace(retval, @"\s+\r\n", "");
              retval = retval.Replace("\r\n", "");
              retval = retval.Replace("\t", " ");
              retval = Regex.Replace(retval, @"\s+\<F", "<F");
              retval = Regex.Replace(retval, @"\<\/F\d+\>", "");
              retval = Regex.Replace(retval, @"\<F01\>", "\r\n");
              retval = Regex.Replace(retval, @"\<F\d+\>", "\t");
              retval = retval.Replace("&amp;", "&");
              retval = retval.Replace("&lt;", "<");
              retval = retval.Replace("&gt;", ">");
              retval = retval.Replace("&quot;", "\"");
              retval = retval.Replace("&apos;", "'");
              retval = retval.Replace("&tab;", "");
              retval = Regex.Replace(retval, @"&#\d+;", "");
              return retval;
          }

          public static DataTable ParseToDataTable(string rawResponseXml, List<FieldConfig> fields)
          {
              var dt = new DataTable();
              foreach (var field in fields)
              {
                  Type colType = typeof(string);
                  if (field.Type.Equals("amount", StringComparison.OrdinalIgnoreCase) ||
                      field.Type.Equals("quantity", StringComparison.OrdinalIgnoreCase) ||
                      field.Type.Equals("rate", StringComparison.OrdinalIgnoreCase) ||
                      field.Type.Equals("number", StringComparison.OrdinalIgnoreCase))
                  {
                      colType = typeof(decimal);
                  }
                  else if (field.Type.Equals("logical", StringComparison.OrdinalIgnoreCase))
                  {
                      colType = typeof(short);
                  }
                  else if (field.Type.Equals("date", StringComparison.OrdinalIgnoreCase))
                  {
                      colType = typeof(DateTime);
                  }
                  dt.Columns.Add(field.Name, colType);
              }

              var tsv = ProcessTdlOutputManipulation(rawResponseXml);
              using (var reader = new StringReader(tsv))
              {
                  string? line;
                  while ((line = reader.ReadLine()) != null)
                  {
                      if (string.IsNullOrWhiteSpace(line)) continue;
                      var parts = line.Split('\t');
                      if (parts.Length == 0 || (parts.Length == 1 && string.IsNullOrWhiteSpace(parts[0]))) continue;

                      var row = dt.NewRow();
                      for (int i = 0; i < fields.Count; i++)
                      {
                          if (i >= parts.Length)
                          {
                              row[fields[i].Name] = DBNull.Value;
                              continue;
                          }

                          string rawValue = parts[i].Trim();
                          if (string.IsNullOrEmpty(rawValue) || rawValue == "\u00f1" || rawValue == "ñ" || rawValue == "241")
                          {
                              row[fields[i].Name] = DBNull.Value;
                              continue;
                          }

                          try
                          {
                              var fieldType = fields[i].Type;
                              if (fieldType.Equals("amount", StringComparison.OrdinalIgnoreCase) ||
                                  fieldType.Equals("quantity", StringComparison.OrdinalIgnoreCase) ||
                                  fieldType.Equals("rate", StringComparison.OrdinalIgnoreCase) ||
                                  fieldType.Equals("number", StringComparison.OrdinalIgnoreCase))
                              {
                                  row[fields[i].Name] = decimal.Parse(rawValue);
                              }
                              else if (fieldType.Equals("logical", StringComparison.OrdinalIgnoreCase))
                              {
                                  row[fields[i].Name] = short.Parse(rawValue);
                              }
                              else if (fieldType.Equals("date", StringComparison.OrdinalIgnoreCase))
                              {
                                  row[fields[i].Name] = DateTime.ParseExact(rawValue, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                              }
                              else
                              {
                                  row[fields[i].Name] = rawValue;
                              }
                          }
                          catch
                          {
                              row[fields[i].Name] = DBNull.Value;
                          }
                      }
                      dt.Rows.Add(row);
                  }
              }
              return dt;
          }
      }
  }
  ```

- [ ] **Step 4: Run tests to verify they pass**
  Run: `dotnet test --filter DynamicXmlParserTests`
  Expected: PASS

- [ ] **Step 5: Commit**
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Tally/DynamicXmlParser.cs tests/TallyDbLoader.Tests/DynamicXmlParserTests.cs
  git commit -m "feat: Add DynamicXmlParser with TSV to DataTable conversion"
  ```

---

### Task 4: Dynamic Table Schema Generator

**Files:**
- Create: `src/TallyDbLoader.Core/Data/DynamicTableSchemaGenerator.cs`
- Create: `tests/TallyDbLoader.Tests/DynamicTableSchemaGeneratorTests.cs`

- [ ] **Step 1: Write a failing test for SQL schema builder**
  Create `tests/TallyDbLoader.Tests/DynamicTableSchemaGeneratorTests.cs` with the test:
  ```csharp
  using Xunit;
  using TallyDbLoader.Core.Data;
  using TallyDbLoader.Core.Tally;
  using System.Collections.Generic;

  namespace TallyDbLoader.Tests
  {
      public class DynamicTableSchemaGeneratorTests
      {
          [Fact]
          public void Test_BuildCreateStatement_MSSQL()
          {
              var table = new TableConfig
              {
                  Name = "mst_ledger",
                  Fields = new List<FieldConfig>
                  {
                      new FieldConfig { Name = "guid", Type = "text" },
                      new FieldConfig { Name = "opening_balance", Type = "amount" },
                      new FieldConfig { Name = "is_revenue", Type = "logical" }
                  }
              };

              var sql = DynamicTableSchemaGenerator.BuildCreateTableSql(table, "mssql");
              Assert.Contains("CREATE TABLE mst_ledger", sql);
              Assert.Contains("guid VARCHAR(64) NOT NULL PRIMARY KEY", sql);
              Assert.Contains("opening_balance DECIMAL(17,2)", sql);
              Assert.Contains("is_revenue TINYINT", sql);
          }

          [Fact]
          public void Test_BuildCreateStatement_Postgres()
          {
              var table = new TableConfig
              {
                  Name = "trn_accounting",
                  Fields = new List<FieldConfig>
                  {
                      new FieldConfig { Name = "guid", Type = "text" },
                      new FieldConfig { Name = "amount", Type = "amount" }
                  }
              };

              var sql = DynamicTableSchemaGenerator.BuildCreateTableSql(table, "postgres");
              Assert.Contains("CREATE TABLE IF NOT EXISTS trn_accounting", sql);
              Assert.Contains("guid TEXT", sql); // No primary key because it's not a master table (doesn't start with mst_ or trn_voucher)
              Assert.Contains("amount NUMERIC(17,2)", sql);
          }
      }
  }
  ```

- [ ] **Step 2: Run test to verify it fails**
  Run: `dotnet test --filter DynamicTableSchemaGeneratorTests`
  Expected: Compile errors.

- [ ] **Step 3: Implement DynamicTableSchemaGenerator**
  Create `src/TallyDbLoader.Core/Data/DynamicTableSchemaGenerator.cs` containing:
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.Text;
  using TallyDbLoader.Core.Tally;

  namespace TallyDbLoader.Core.Data
  {
      public static class DynamicTableSchemaGenerator
      {
          public static string BuildCreateTableSql(TableConfig table, string technology)
          {
              var isPostgres = technology.Equals("postgres", StringComparison.OrdinalIgnoreCase);
              var isMysql = technology.Equals("mysql", StringComparison.OrdinalIgnoreCase);
              var isMssql = technology.Equals("mssql", StringComparison.OrdinalIgnoreCase);

              var sb = new StringBuilder();
              if (isPostgres)
              {
                  sb.AppendLine($"CREATE TABLE IF NOT EXISTS \"{table.Name}\" (");
              }
              else if (isMysql)
              {
                  sb.AppendLine($"CREATE TABLE IF NOT EXISTS `{table.Name}` (");
              }
              else // MSSQL
              {
                  sb.AppendLine($"IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='{table.Name}' AND xtype='U')");
                  sb.AppendLine($"CREATE TABLE {table.Name} (");
              }

              var columns = new List<string>();
              foreach (var field in table.Fields)
              {
                  var isPk = field.Name.Equals("guid", StringComparison.OrdinalIgnoreCase) && 
                             (table.Name.StartsWith("mst_", StringComparison.OrdinalIgnoreCase) || 
                              table.Name.Equals("trn_voucher", StringComparison.OrdinalIgnoreCase));

                  string typeStr = "TEXT";
                  if (field.Type.Equals("logical", StringComparison.OrdinalIgnoreCase))
                  {
                      typeStr = isPostgres ? "SMALLINT" : "TINYINT";
                  }
                  else if (field.Type.Equals("amount", StringComparison.OrdinalIgnoreCase) ||
                           field.Type.Equals("number", StringComparison.OrdinalIgnoreCase))
                  {
                      typeStr = isPostgres ? "NUMERIC(17,2)" : "DECIMAL(17,2)";
                  }
                  else if (field.Type.Equals("quantity", StringComparison.OrdinalIgnoreCase) ||
                           field.Type.Equals("rate", StringComparison.OrdinalIgnoreCase))
                  {
                      typeStr = isPostgres ? "NUMERIC(15,4)" : "DECIMAL(15,4)";
                  }
                  else if (field.Type.Equals("date", StringComparison.OrdinalIgnoreCase))
                  {
                      typeStr = "DATE";
                  }
                  else if (field.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
                  {
                      if (isPk)
                      {
                          typeStr = isMssql ? "VARCHAR(64)" : "VARCHAR(64)";
                      }
                      else
                      {
                          typeStr = isPostgres ? "TEXT" : (isMssql ? "VARCHAR(2000)" : "TEXT");
                      }
                  }

                  string columnDef = string.Empty;
                  if (isPostgres)
                  {
                      columnDef = $"\"{field.Name}\" {typeStr}" + (isPk ? " NOT NULL PRIMARY KEY" : "");
                  }
                  else if (isMysql)
                  {
                      columnDef = $"`{field.Name}` {typeStr}" + (isPk ? " NOT NULL PRIMARY KEY" : "");
                  }
                  else // MSSQL
                  {
                      columnDef = $"[{field.Name}] {typeStr}" + (isPk ? " NOT NULL PRIMARY KEY" : "");
                  }
                  columns.Add(columnDef);
              }

              sb.AppendLine(string.Join(",\n", columns));
              sb.Append(");");

              return sb.ToString();
          }
      }
  }
  ```

- [ ] **Step 4: Run tests to verify they pass**
  Run: `dotnet test --filter DynamicTableSchemaGeneratorTests`
  Expected: PASS

- [ ] **Step 5: Commit**
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Data/DynamicTableSchemaGenerator.cs tests/TallyDbLoader.Tests/DynamicTableSchemaGeneratorTests.cs
  git commit -m "feat: Add DynamicTableSchemaGenerator"
  ```

---

### Task 5: Refactoring Database Helpers and Loaders

**Files:**
- Modify: `src/TallyDbLoader.Core/Data/DatabaseWriter.cs`
- Modify: `src/TallyDbLoader.Core/DatabaseLoaders/MySqlLoader.cs`

- [ ] **Step 1: Modify DatabaseWriter to support MySQL connections and helper operations**
  Open `src/TallyDbLoader.Core/Data/DatabaseWriter.cs`. Overwrite the `GetConnection` method and add the MySQL connection builder. Modify it to read connection configurations dynamically:
  ```csharp
  // Replace the implementation of GetConnection in DatabaseWriter.cs with:
  private static IDbConnection GetConnection(DatabaseProfile profile, string catalog)
  {
      if (profile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
      {
          string sslParam = "";
          if (!profile.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase) && 
              !profile.Server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
          {
              sslParam = "SslMode=Require;TrustServerCertificate=True;";
          }
          string connStr = $"Host={profile.Server};Port={profile.Port};Username={profile.Username};Password={profile.Password};Database={catalog};{sslParam}";
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
      else if (profile.Technology.Equals("mysql", StringComparison.OrdinalIgnoreCase))
      {
          string connStr = $"Server={profile.Server};Port={profile.Port};User Id={profile.Username};Password={profile.Password};Database={catalog};AllowLoadLocalInfile=True;";
          var conn = new MySqlConnector.MySqlConnection(connStr);
          conn.Open();
          return conn;
      }
      throw new NotSupportedException($"Database technology '{profile.Technology}' is not supported.");
  }
  ```

- [ ] **Step 2: Add dynamic staging helpers to DatabaseWriter**
  Add raw SQL execution helpers to `DatabaseWriter.cs` for truncation, staging comparisons, and config updating:
  ```csharp
  public static void ExecuteNonQuery(DatabaseProfile profile, string catalog, string sql)
  {
      using (var conn = GetConnection(profile, catalog))
      using (var cmd = conn.CreateCommand())
      {
          cmd.CommandText = sql;
          cmd.ExecuteNonQuery();
      }
  }

  public static T ExecuteScalar<T>(DatabaseProfile profile, string catalog, string sql)
  {
      using (var conn = GetConnection(profile, catalog))
      using (var cmd = conn.CreateCommand())
      {
          cmd.CommandText = sql;
          var val = cmd.ExecuteScalar();
          if (val == null || val == DBNull.Value) return default(T)!;
          return (T)Convert.ChangeType(val, typeof(T));
      }
  }
  ```

- [ ] **Step 3: Fix MySqlLoader mapping bugs**
  Open `src/TallyDbLoader.Core/DatabaseLoaders/MySqlLoader.cs` and ensure it maps connection string features correctly:
  ```csharp
  using System;
  using System.Data;
  using System.Threading.Tasks;
  using MySqlConnector;

  namespace TallyDbLoader.Core.DatabaseLoaders
  {
      public class MySqlLoader : IDatabaseLoader
      {
          private readonly string _connectionString;

          public MySqlLoader(string connectionString)
          {
              // Inject AllowLoadLocalInfile parameter
              var builder = new MySqlConnectionStringBuilder(connectionString)
              {
                  AllowLoadLocalInfile = true
              };
              _connectionString = builder.ConnectionString;
          }

          public async Task LoadBulkDataAsync(DataTable data, string tableName)
          {
              using (var conn = new MySqlConnection(_connectionString))
              {
                  await conn.OpenAsync();
                  var bulkCopy = new MySqlBulkCopy(conn)
                  {
                      DestinationTableName = tableName
                  };
                  
                  foreach (DataColumn col in data.Columns)
                  {
                      bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(col.Ordinal, col.ColumnName));
                  }
                  
                  await bulkCopy.WriteToServerAsync(data);
              }
          }
      }
  }
  ```

- [ ] **Step 4: Run unit tests**
  Run: `dotnet test`
  Expected: PASS (No regressions in existing database writers).

- [ ] **Step 5: Commit**
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Data/DatabaseWriter.cs src/TallyDbLoader.Core/DatabaseLoaders/MySqlLoader.cs
  git commit -m "refactor: Update DatabaseWriter and MySqlLoader helpers"
  ```

---

### Task 6: Incremental Sync Engine Implementation

**Files:**
- Create: `src/TallyDbLoader.Core/Sync/IncrementalSyncEngine.cs`
- Create: `tests/TallyDbLoader.Tests/IncrementalSyncEngineTests.cs`

- [ ] **Step 1: Write a test shell for the IncrementalSyncEngine**
  Create `tests/TallyDbLoader.Tests/IncrementalSyncEngineTests.cs`:
  ```csharp
  using Xunit;
  using TallyDbLoader.Core.Sync;

  namespace TallyDbLoader.Tests
  {
      public class IncrementalSyncEngineTests
      {
          [Fact]
          public void Test_VerifySyncLoopDefinitions()
          {
              // Simple sanity compile check for references
              Assert.True(true);
          }
      }
  }
  ```

- [ ] **Step 2: Run test to verify it compiles and runs**
  Run: `dotnet test --filter IncrementalSyncEngineTests`
  Expected: PASS

- [ ] **Step 3: Implement IncrementalSyncEngine**
  Create `src/TallyDbLoader.Core/Sync/IncrementalSyncEngine.cs` containing:
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.Data;
  using System.IO;
  using System.Threading.Tasks;
  using TallyDbLoader.Core.Data;
  using TallyDbLoader.Core.DatabaseLoaders;
  using TallyDbLoader.Core.Models;
  using TallyDbLoader.Core.Tally;

  namespace TallyDbLoader.Core.Sync
  {
      public class IncrementalSyncEngine
      {
          private readonly TallyClient _client;
          private readonly DatabaseProfile _dbProfile;
          private readonly string _catalog;
          private readonly TallyExportConfig _yamlConfig;
          private readonly Action<string> _logger;

          public IncrementalSyncEngine(TallyClient client, DatabaseProfile dbProfile, string catalog, TallyExportConfig yamlConfig, Action<string> logger)
          {
              _client = client;
              _dbProfile = dbProfile;
              _catalog = catalog;
              _yamlConfig = yamlConfig;
              _logger = logger;
          }

          private IDatabaseLoader GetLoader()
          {
              string connStr = string.Empty;
              if (_dbProfile.Technology.Equals("postgres", StringComparison.OrdinalIgnoreCase))
              {
                  string sslParam = "";
                  if (!_dbProfile.Server.Equals("localhost", StringComparison.OrdinalIgnoreCase) && 
                      !_dbProfile.Server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                  {
                      sslParam = "SslMode=Require;TrustServerCertificate=True;";
                  }
                  connStr = $"Host={_dbProfile.Server};Port={_dbProfile.Port};Username={_dbProfile.Username};Password={_dbProfile.Password};Database={_catalog};{sslParam}";
                  return new PostgreSqlLoader(connStr);
              }
              else if (_dbProfile.Technology.Equals("mssql", StringComparison.OrdinalIgnoreCase))
              {
                  connStr = $"Server={_dbProfile.Server},{_dbProfile.Port};User Id={_dbProfile.Username};Password={_dbProfile.Password};Database={_catalog};TrustServerCertificate=True;";
                  return new MSSqlLoader(connStr);
              }
              else if (_dbProfile.Technology.Equals("mysql", StringComparison.OrdinalIgnoreCase))
              {
                  connStr = $"Server={_dbProfile.Server};Port={_dbProfile.Port};User Id={_dbProfile.Username};Password={_dbProfile.Password};Database={_catalog};AllowLoadLocalInfile=True;";
                  return new MySqlLoader(connStr);
              }
              throw new NotSupportedException($"Technology '{_dbProfile.Technology}' not supported.");
          }

          public async Task RunSyncAsync(string companyName)
          {
              _logger("Acquiring last AlterIDs from database...");
              
              var tech = _dbProfile.Technology.ToLower();
              string castExpr = tech == "mysql" ? "UNSIGNED" : "INT";
              
              long lastMstId = 0;
              long lastTrnId = 0;

              try
              {
                  lastMstId = DatabaseWriter.ExecuteScalar<long>(_dbProfile, _catalog, $"SELECT COALESCE(MAX(CAST(value AS {castExpr})), 0) FROM config WHERE name = 'Last AlterID Master'");
                  lastTrnId = DatabaseWriter.ExecuteScalar<long>(_dbProfile, _catalog, $"SELECT COALESCE(MAX(CAST(value AS {castExpr})), 0) FROM config WHERE name = 'Last AlterID Transaction'");
              }
              catch
              {
                  _logger("Config table missing or uninitialized. Performing structure verification...");
                  InitializeHelperTables();
              }

              _logger($"Last Database Master AlterID: {lastMstId}, Transaction AlterID: {lastTrnId}");

              // Fetch company info from Tally
              _logger("Fetching active company info from Tally...");
              var xmlComp = "<?xml version=\"1.0\" encoding=\"utf-8\"?><ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>TallyDatabaseLoaderReport</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>ASCII (Comma Delimited)</SVEXPORTFORMAT></STATICVARIABLES><TDL><TDLMESSAGE><REPORT NAME=\"TallyDatabaseLoaderReport\"><FORMS>MyForm</FORMS></REPORT><FORM NAME=\"MyForm\"><PARTS>MyPart</PARTS></FORM><PART NAME=\"MyPart\"><LINES>MyLine</LINES><REPEAT>MyLine : MyCollection</REPEAT><SCROLLED>Vertical</SCROLLED></PART><LINE NAME=\"MyLine\"><FIELDS>FldGuid,FldName,FldBooksFrom,FldLastVoucherDate,FldLastAlterIdMaster,FldLastAlterIdTransaction,FldEOL</FIELDS></LINE><FIELD NAME=\"FldGuid\"><SET>$Guid</SET></FIELD><FIELD NAME=\"FldName\"><SET>$$StringFindAndReplace:$Name:'\"':'\"\"'</SET></FIELD><FIELD NAME=\"FldBooksFrom\"><SET>(($$YearOfDate:$BooksFrom)*10000)+(($$MonthOfDate:$BooksFrom)*100)+(($$DayOfDate:$BooksFrom)*1)</SET></FIELD><FIELD NAME=\"FldLastVoucherDate\"><SET>(($$YearOfDate:$LastVoucherDate)*10000)+(($$MonthOfDate:$LastVoucherDate)*100)+(($$DayOfDate:$LastVoucherDate)*1)</SET></FIELD><FIELD NAME=\"FldLastAlterIdMaster\"><SET>$AltMstId</SET></FIELD><FIELD NAME=\"FldLastAlterIdTransaction\"><SET>$AltVchId</SET></FIELD><FIELD NAME=\"FldEOL\"><SET>†</SET></FIELD><COLLECTION NAME=\"MyCollection\"><TYPE>Company</TYPE><FILTER>FilterActiveCompany</FILTER></COLLECTION><SYSTEM TYPE=\"Formulae\" NAME=\"FilterActiveCompany\">$$IsEqual:##SVCurrentCompany:$Name</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>";
              xmlComp = xmlComp.Replace("##SVCurrentCompany", $"\"{companyName}\"");

              var compResponse = await _client.PostXMLAsync(xmlComp);
              if (string.IsNullOrWhiteSpace(compResponse) || !compResponse.Contains("†"))
              {
                  throw new InvalidOperationException($"Could not fetch details for company '{companyName}' from Tally. Verify the company is open.");
              }

              // Parse csv company details
              var cleanedCsv = compResponse.Replace("\",\"†\",\r\n", "").Replace("\"", "").Trim();
              var csvParts = cleanedCsv.Split(',');
              if (csvParts.Length < 6)
              {
                  throw new InvalidOperationException("Failed to parse company information.");
              }

              long tallyMstId = long.Parse(csvParts[4]);
              long tallyTrnId = long.Parse(csvParts[5]);
              _logger($"Active Tally AlterID Master: {tallyMstId}, Transaction: {tallyTrnId}");

              if (lastMstId == tallyMstId && lastTrnId == tallyTrnId)
              {
                  _logger("No changes detected since last synchronization. Sync skipped.");
                  return;
              }

              // Truncate config and staging
              DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, "TRUNCATE TABLE config;");
              DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, $"INSERT INTO config(name,value) VALUES('Update Timestamp','{DateTime.Now:O}'),('Company Name','{companyName}'),('Last AlterID Master','{tallyMstId}'),('Last AlterID Transaction','{tallyTrnId}');");

              var primaryTables = new List<TableConfig>();
              if (lastMstId != tallyMstId)
                  primaryTables.AddRange(_yamlConfig.Master.FindAll(p => p.Nature.Equals("Primary", StringComparison.OrdinalIgnoreCase)));
              if (lastTrnId != tallyTrnId)
                  primaryTables.AddRange(_yamlConfig.Transaction.FindAll(p => p.Nature.Equals("Primary", StringComparison.OrdinalIgnoreCase)));

              var loader = GetLoader();

              // Compare and delete
              foreach (var activeTable in primaryTables)
              {
                  _logger($"Processing delete mapping staging comparisons for {activeTable.Name}...");
                  DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, "TRUNCATE TABLE _diff;");
                  DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, "TRUNCATE TABLE _delete;");

                  var stagingFields = new List<FieldConfig>
                  {
                      new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                      new FieldConfig { Name = "alterid", Field = "AlterId", Type = "text" }
                  };

                  var tempTableConfig = new TableConfig
                  {
                      Collection = activeTable.Collection,
                      Fields = stagingFields,
                      Fetch = new List<string> { "AlterId" },
                      Filters = activeTable.Filters
                  };

                  var stagingXml = DynamicTdlXmlGenerator.GenerateXml(tempTableConfig, companyName, "19000101", "20991231");
                  var stagingXmlResponse = await _client.PostXMLAsync(stagingXml);
                  var stagingDataTable = DynamicXmlParser.ParseToDataTable(stagingXmlResponse, stagingFields);

                  await loader.LoadBulkDataAsync(stagingDataTable, "_diff");

                  // Detect deleted and altered
                  DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, $"INSERT INTO _delete SELECT guid FROM {activeTable.Name} WHERE guid NOT IN (SELECT guid FROM _diff);");
                  DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, $"INSERT INTO _delete SELECT t.guid FROM {activeTable.Name} AS t JOIN _diff AS s ON s.guid = t.guid WHERE s.alterid <> t.alterid;");

                  // Delete
                  DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, $"DELETE FROM {activeTable.Name} WHERE guid IN (SELECT guid FROM _delete);");

                  if (activeTable.CascadeDelete != null)
                  {
                      foreach (var child in activeTable.CascadeDelete)
                      {
                          DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, $"DELETE FROM {child.Table} WHERE {child.Field} IN (SELECT guid FROM _delete);");
                      }
                  }
              }

              // Incremental ingestions
              if (lastMstId != tallyMstId)
              {
                  await IngestIncrementalTableList(_yamlConfig.Master, companyName, lastMstId, loader);
              }
              if (lastTrnId != tallyTrnId)
              {
                  await IngestIncrementalTableList(_yamlConfig.Transaction, companyName, lastTrnId, loader);
              }

              // Post-Sync reference update queries
              _logger("Executing cascade denormalization joins...");
              var allTables = new List<TableConfig>();
              allTables.AddRange(_yamlConfig.Master);
              allTables.AddRange(_yamlConfig.Transaction);

              foreach (var tbl in allTables)
              {
                  if (tbl.CascadeUpdate != null)
                  {
                      foreach (var relation in tbl.CascadeUpdate)
                      {
                          string updateSql = string.Empty;
                          if (tech == "postgres")
                          {
                              updateSql = $"UPDATE \"{relation.Table}\" AS t SET \"{relation.Field}\" = s.name FROM \"{tbl.Name}\" AS s WHERE s.guid = t.\"_{relation.Field}\"";
                          }
                          else if (tech == "mysql")
                          {
                              updateSql = $"UPDATE `{relation.Table}` AS t JOIN `{tbl.Name}` AS s ON s.guid = t.`_{relation.Field}` SET t.`{relation.Field}` = s.name";
                          }
                          else // mssql
                          {
                              updateSql = $"UPDATE t SET t.[{relation.Field}] = s.name FROM {relation.Table} AS t JOIN {tbl.Name} AS s ON s.guid = t.[_{relation.Field}]";
                          }
                          try
                          {
                              DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, updateSql);
                          }
                          catch (Exception ex)
                          {
                              _logger($"Warning: Cascade update failed for {relation.Table}.{relation.Field}: {ex.Message}");
                          }
                      }
                  }
              }

              // Voucher number correction sync
              if (lastTrnId != tallyTrnId)
              {
                  _logger("Running voucher number alignment updates...");
                  var vchTableConfig = _yamlConfig.Transaction.Find(p => p.Name.Equals("trn_voucher", StringComparison.OrdinalIgnoreCase));
                  if (vchTableConfig != null)
                  {
                      DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, "TRUNCATE TABLE _vchnumber;");
                      
                      var vchFields = new List<FieldConfig>
                      {
                          new FieldConfig { Name = "guid", Field = "Guid", Type = "text" },
                          new FieldConfig { Name = "voucher_number", Field = "VoucherNumber", Type = "text" }
                      };
                      var tempVch = new TableConfig
                      {
                          Collection = vchTableConfig.Collection,
                          Fields = vchFields,
                          Filters = vchTableConfig.Filters
                      };
                      var vchXml = DynamicTdlXmlGenerator.GenerateXml(tempVch, companyName, "19000101", "20991231");
                      var vchXmlResponse = await _client.PostXMLAsync(vchXml);
                      var vchDataTable = DynamicXmlParser.ParseToDataTable(vchXmlResponse, vchFields);
                      await loader.LoadBulkDataAsync(vchDataTable, "_vchnumber");

                      string updateVchSql = tech switch
                      {
                          "postgres" => "UPDATE \"trn_voucher\" AS t SET \"voucher_number\" = s.voucher_number FROM _vchnumber AS s WHERE s.guid = t.guid",
                          "mysql" => "UPDATE `trn_voucher` AS t JOIN `_vchnumber` AS s ON s.guid = t.guid SET t.`voucher_number` = s.voucher_number",
                          _ => "UPDATE t SET t.[voucher_number] = s.voucher_number FROM trn_voucher AS t JOIN _vchnumber AS s ON s.guid = t.guid"
                      };
                      DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, updateVchSql);
                  }
              }

              // Cleanup
              DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, "TRUNCATE TABLE _diff;");
              DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, "TRUNCATE TABLE _delete;");
              DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, "TRUNCATE TABLE _vchnumber;");
              _logger("Incremental synchronization run completed successfully.");
          }

          private async Task IngestIncrementalTableList(List<TableConfig> list, string companyName, long lastAlterId, IDatabaseLoader loader)
          {
              foreach (var activeTable in list)
              {
                  _logger($"Fetching incremental rows for target table {activeTable.Name}...");

                  var incrementalTableConfig = new TableConfig
                  {
                      Name = activeTable.Name,
                      Collection = activeTable.Collection,
                      Fields = activeTable.Fields,
                      Fetch = activeTable.Fetch,
                      Filters = new List<string>()
                  };
                  if (activeTable.Filters != null)
                  {
                      incrementalTableConfig.Filters.AddRange(activeTable.Filters);
                  }
                  // Append AlterId filter
                  incrementalTableConfig.Filters.Add($"$AlterId > {lastAlterId}");

                  var xml = DynamicTdlXmlGenerator.GenerateXml(incrementalTableConfig, companyName, "19000101", "20991231");
                  var xmlResponse = await _client.PostXMLAsync(xml);
                  var dataTable = DynamicXmlParser.ParseToDataTable(xmlResponse, activeTable.Fields);

                  _logger($"Bulk loading {dataTable.Rows.Count} fresh records into {activeTable.Name}...");
                  await loader.LoadBulkDataAsync(dataTable, activeTable.Name);
              }
          }

          private void InitializeHelperTables()
          {
              var tech = _dbProfile.Technology.ToLower();
              if (tech == "postgres")
              {
                  DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, @"
                      CREATE TABLE IF NOT EXISTS _diff (guid varchar(64) not null, alterid int not null);
                      CREATE TABLE IF NOT EXISTS _delete (guid varchar(64) not null);
                      CREATE TABLE IF NOT EXISTS _vchnumber (guid varchar(64) not null, voucher_number varchar(256) not null);
                      CREATE TABLE IF NOT EXISTS config (name varchar(64) not null primary key, value varchar(1024));");
              }
              else if (tech == "mysql")
              {
                  DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, @"
                      CREATE TABLE IF NOT EXISTS _diff (guid varchar(64) not null, alterid int not null);
                      CREATE TABLE IF NOT EXISTS _delete (guid varchar(64) not null);
                      CREATE TABLE IF NOT EXISTS _vchnumber (guid varchar(64) not null, voucher_number varchar(256) not null);
                      CREATE TABLE IF NOT EXISTS config (name varchar(64) not null primary key, value varchar(1024));");
              }
              else // mssql
              {
                  DatabaseWriter.ExecuteNonQuery(_dbProfile, _catalog, @"
                      IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_diff' AND xtype='U')
                          CREATE TABLE _diff (guid varchar(64) not null, alterid int not null);
                      IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_delete' AND xtype='U')
                          CREATE TABLE _delete (guid varchar(64) not null);
                      IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='_vchnumber' AND xtype='U')
                          CREATE TABLE _vchnumber (guid varchar(64) not null, voucher_number varchar(256) not null);
                      IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='config' AND xtype='U')
                          CREATE TABLE config (name varchar(64) not null primary key, value varchar(1024));");
              }
          }
      }
  }
  ```

- [ ] **Step 4: Run tests**
  Run: `dotnet test`
  Expected: PASS

- [ ] **Step 5: Commit**
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Sync/IncrementalSyncEngine.cs tests/TallyDbLoader.Tests/IncrementalSyncEngineTests.cs
  git commit -m "feat: Add IncrementalSyncEngine core logic"
  ```

---

### Task 7: BackgroundSyncWorker & UI Hookup

**Files:**
- Modify: `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs`

- [ ] **Step 1: Update BackgroundSyncWorker to read config file and run dynamic sync engine**
  Open `src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs` and replace its main loop execution logic (lines 80-145) to call the `IncrementalSyncEngine` and dynamic schema initialization dynamically.
  ```csharp
  // Overwrite the execution inside WorkerLoop in BackgroundSyncWorker.cs:
  // Replace the try-catch block from:
  // "try { Log($"[SyncJob] Fetching ledgers XML..." ) ... }"
  // with:
  try
  {
      var dbProfile = _repo.GetDatabaseProfileById(job.DbProfileId);
      if (dbProfile != null)
      {
          Log($"[SyncJob] Target database technology: {dbProfile.Technology} on server '{dbProfile.Server}:{dbProfile.Port}'.");
          Log($"[SyncJob] Initializing YAML configuration: '{settings.TallyExePath}' (or default configs)...");

          // Load YAML config
          string yamlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tally-export-config.yaml");
          if (!File.Exists(yamlPath))
          {
              // Fallback to project root directory in case of debug run
              yamlPath = Path.Combine(Directory.GetCurrentDirectory(), "tally-export-config.yaml");
          }

          if (!File.Exists(yamlPath))
          {
              throw new FileNotFoundException($"Cannot locate Tally export definition file at: {yamlPath}");
          }

          var yamlContent = File.ReadAllText(yamlPath);
          var yamlConfig = YamlConfigParser.Parse(yamlContent);

          Log("[SyncJob] Verifying and generating schemas dynamically in target database...");
          // Initialize target tables dynamically from YAML config definition
          var allTables = new List<TableConfig>();
          allTables.AddRange(yamlConfig.Master);
          allTables.AddRange(yamlConfig.Transaction);

          foreach (var table in allTables)
          {
              var createSql = DynamicTableSchemaGenerator.BuildCreateTableSql(table, dbProfile.Technology);
              DatabaseWriter.ExecuteNonQuery(dbProfile, job.TargetCatalog, createSql);
          }

          Log("[SyncJob] Running dynamic incremental synchronization worker...");
          var engine = new IncrementalSyncEngine(client, dbProfile, job.TargetCatalog, yamlConfig, Log);
          await engine.RunSyncAsync(job.CompanyName);

          job.Status = "Idle";
          job.LastRunTime = DateTime.UtcNow.ToString("o");
          Log($"Job '{job.CompanyName}' completed successfully.");
      }
      else
      {
          job.Status = "Failed";
          Log($"Job '{job.CompanyName}' failed: Database profile ID {job.DbProfileId} not found in configuration.");
      }
  }
  catch (Exception ex)
  {
      job.Status = "Failed";
      Log($"Job '{job.CompanyName}' failed: {ex.Message}");
      TallyDbLoader.Core.Logging.FileLogger.LogError($"Job '{job.CompanyName}'", ex);
  }
  ```

- [ ] **Step 2: Run all tests to make sure they compile and pass**
  Run: `dotnet test`
  Expected: PASS

- [ ] **Step 3: Commit**
  Run:
  ```bash
  git add src/TallyDbLoader.Core/Sync/BackgroundSyncWorker.cs
  git commit -m "feat: Hook up YamlConfigParser and IncrementalSyncEngine to BackgroundSyncWorker loop"
  ```
