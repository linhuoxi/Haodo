using System;
using System.Reflection;
using System.Text.Json;

class Probe
{
    static int Main()
    {
        Type type = typeof(CLIProxyAPI_GUI.GeminiProtocolTranslator);

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
        // Exercise complete request conversion, with the second tool's fourth/fifth
        // properties matching the positions reported by Quicker. No network or credentials.
        string agentSchema = """
        {
          "type": "object",
          "required": ["text", "count", "flags", "optionalText", "optionalList"],
          "properties": {
            "text": {"type":"string"},
            "count": {"type":"integer"},
            "flags": {"type":"array","items":{"type":"boolean"}},
            "optionalText": {"type":["string","null"],"nullable":false,"description":"Optional text"},
            "optionalList": {"type":["null","array"],"items":{"type":["integer","null"]}},
            "nested": {"type":["object","null"],"properties":{
              "type":{"type":["boolean","null"]},
              "properties":{"type":["number","null"]},
              "required":{"type":["string","null"]},
              "nullable":{"type":["string","null"]}
            }},
            "missingItems": {"type":["array","null"]},
            "singleType": {"type":["integer"]},
            "manyTypes": {"type":["number","string","null"]},
            "onlyNull": {"type":["null"]}
          }
        }
        """;
        foreach (bool responses in new[] { false, true })
        {
            foreach (bool stream in new[] { false, true })
            {
                string function = "{\"name\":\"agent_action\",\"parameters\":" + agentSchema + "}";
                string tool = responses
                    ? "{\"type\":\"function\",\"name\":\"agent_action\",\"parameters\":" + agentSchema + "}"
                    : "{\"type\":\"function\",\"function\":" + function + "}";
                string request = "{\"model\":\"gemini-3.7-flash\",\"stream\":" + (stream ? "true" : "false")
                    + (responses ? ",\"input\":\"test\"" : ",\"messages\":[{\"role\":\"user\",\"content\":\"test\"}]")
                    + ",\"tools\":[" + tool + "," + tool + "]}";
                var converted = CLIProxyAPI_GUI.GeminiProtocolTranslator.ConvertOpenAIToGeminiRequest(request);
                using var output = JsonDocument.Parse(converted.geminiJson);
                var parameters = output.RootElement.GetProperty("request").GetProperty("tools")[0]
                    .GetProperty("functionDeclarations")[1].GetProperty("parameters");
                var properties = parameters.GetProperty("properties");
                string label = (responses ? "Responses" : "Chat") + (stream ? " streaming" : " non-streaming");
                Check(converted.isStream == stream, label + " stream flag", ref fail);
                Check(parameters.GetProperty("required").GetArrayLength() == 5, label + " required preserved", ref fail);
                foreach (var item in new[] {
                    ("optionalText", "string"), ("optionalList", "array"), ("nested", "object"),
                    ("missingItems", "array"), ("singleType", "integer"), ("manyTypes", "number"), ("onlyNull", "string") })
                {
                    var property = properties.GetProperty(item.Item1);
                    Check(property.GetProperty("type").ValueKind == JsonValueKind.String
                        && property.GetProperty("type").GetString() == item.Item2, label + " " + item.Item1 + " scalar type", ref fail);
                    if (item.Item1 != "singleType")
                        Check(property.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True,
                            label + " " + item.Item1 + " nullable", ref fail);
                }
                var nested = properties.GetProperty("nested").GetProperty("properties");
                foreach (string name in new[] { "type", "properties", "required", "nullable" })
                    Check(nested.GetProperty(name).GetProperty("type").ValueKind == JsonValueKind.String,
                        label + " schema-named property " + name, ref fail);
                Check(properties.GetProperty("optionalList").GetProperty("items").GetProperty("type").ValueKind == JsonValueKind.String,
                    label + " nested array items", ref fail);
                Check(properties.GetProperty("missingItems").TryGetProperty("items", out var items)
                    && items.GetProperty("type").GetString() == "string", label + " default items", ref fail);
                Check(properties.GetProperty("optionalText").GetProperty("description").GetString() == "Optional text",
                    label + " description preserved", ref fail);
                Console.WriteLine("Checked: " + label);
            }
        }
        Console.WriteLine(fail == 0 ? "PASS: 白名单收敛验证通过" : $"FAIL: {fail} 项未通过");
        return fail == 0 ? 0 : 1;
    }

    static void Check(bool condition, string name, ref int failures)
    {
        if (condition) return;
        Console.WriteLine("FAIL: " + name);
        failures++;
    }
}
