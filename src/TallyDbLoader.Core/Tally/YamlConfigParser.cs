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
                .IgnoreUnmatchedProperties()
                .Build();
            return deserializer.Deserialize<TallyExportConfig>(yamlContent);
        }
    }
}
