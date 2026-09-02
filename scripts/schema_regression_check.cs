using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;

class Probe
{
    static int Main()
    {
        string dll = @"E:\onedrive-lin\OneDrive\编程\BalanceViewer\src\bin\Debug\net10.0-windows\win-x64\Haodo.dll";
        var asm = Assembly.LoadFrom(dll);
        Type type = asm.GetType("BalanceViewer.GeminiProtocolTranslator");
        if (type == null)
        {
            try
            {
                type = asm.GetTypes().FirstOrDefault(t => t.Name == "GeminiProtocolTranslator");
            }
            catch (ReflectionTypeLoadException ex)
            {
                type = ex.Types.FirstOrDefault(t => t != null && t.Name == "GeminiProtocolTranslator");
            }
        }
        if (type == null) { Console.WriteLine("FAIL: type not found"); return 1; }

        var m = type.GetMethod("SanitizeGeminiSchema", BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null) { Console.WriteLine("FAIL: method not found"); return 1; }

        string schema = @"{
  ""type"": ""object"",
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""$id"": ""https://example.com/s"",
  ""$defs"": { ""X"": { ""type"": ""string"" } },
  ""definitions"": { ""Y"": { ""type"": ""string"" } },
  ""propertyNames"": { ""pattern"": ""^[a-z]+$"" },
  ""patternProperties"": { ""^a"": { ""type"": ""string"" } },
  ""minProperties"": 1,
  ""maxProperties"": 10,
  ""required"": [""name"", ""metadata""],
  ""properties"": {
    ""name"": {
      ""type"": ""string"",
      ""examples"": [""你好""],
      ""const"": ""你好"",
      ""$ref"": ""#/$defs/X"",
      ""title"": ""姓名"",
      ""default"": ""?"",
      ""format"": ""text"",
      ""minLength"": 1,
      ""maxLength"": 20
    },
    ""metadata"": {
      ""type"": ""object"",
      ""propertyNames"": { ""pattern"": ""^[a-z][a-zA-Z0-9]*$"" },
      ""additionalProperties"": { ""type"": ""string"" },
      ""properties"": {
        ""k"": { ""type"": ""string"" },
        ""propertyNames"": { ""type"": ""string"" }
      }
    },
    ""tags"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"", ""examples"": [""x""] },
      ""uniqueItems"": true,
      ""minItems"": 1,
      ""maxItems"": 5
    },
    ""age"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 120, ""nullable"": true },
    ""anyOfField"": { ""anyOf"": [ { ""type"": ""string"" }, { ""type"": ""number"" } ] },
    ""empty"": {}
  },
  ""additionalProperties"": false,
  ""enum"": [""a"", ""b""],
  ""allOf"": [ { ""type"": ""object"" } ],
  ""not"": { ""type"": ""null"" },
  ""contains"": { ""type"": ""string"" },
  ""minContains"": 1,
  ""maxContains"": 3,
  ""multipleOf"": 2,
  ""exclusiveMinimum"": 0,
  ""exclusiveMaximum"": 100,
  ""dependencies"": { ""name"": [""metadata""] },
  ""deprecated"": true,
  ""readOnly"": true,
  ""writeOnly"": false,
  ""propertyOrdering"": [""name""]
}";

        object? result = m.Invoke(null, new object[] { JsonDocument.Parse(schema).RootElement });
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });

        string[] mustKeep = { "\"type\"", "\"name\"", "\"metadata\"", "\"tags\"", "\"age\"", "\"additionalProperties\":",
                              "\"nullable\":", "\"minLength\":", "\"format\":", "\"propertyNames\": {",
                              "\"enum\"", "\"minimum\":", "\"required\"", "\"items\"" };
        string[] mustRemove = { "\"propertyNames\": { \"pattern\"", "\"const\"", "\"examples\"", "\"$ref\"", "\"$schema\"",
                                "\"$id\"", "\"$defs\"", "\"definitions\"", "\"anyOf\"", "\"patternProperties\"",
                                "\"title\"", "\"default\"", "\"uniqueItems\"", "\"allOf\"", "\"not\"", "\"contains\"",
                                "\"minContains\"", "\"maxContains\"", "\"multipleOf\"", "\"exclusiveMinimum\"",
                                "\"exclusiveMaximum\"", "\"dependencies\"", "\"deprecated\"", "\"readOnly\"",
                                "\"writeOnly\"", "\"propertyOrdering\"" };

        int fail = 0;
        foreach (var k in mustKeep)
        {
            if (!json.Contains(k, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("FAIL(missing): " + k); fail++; }
        }
        foreach (var k in mustRemove)
        {
            if (json.Contains(k, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("FAIL(kept): " + k); fail++; }
        }
        Console.WriteLine("--- 清洗输出 ---");
        Console.WriteLine(json);
        Console.WriteLine(fail == 0 ? "PASS: 白名单收敛验证通过" : $"FAIL: {fail} 项未通过");
        return fail == 0 ? 0 : 1;
    }
}
