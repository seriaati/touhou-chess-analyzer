using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

[SupportedOSPlatform("browser")]
public static partial class Api
{
    public static void Main()
    {
    }

    [JSExport]
    public static string Analyze(string request) =>
        JsonSerializer.Serialize(
            Analyzer.Run(JsonSerializer.Deserialize(request, Json.Default.AnalyzeRequest), CancellationToken.None),
            Json.Default.AnalyzeResponse);

    [JSExport]
    public static string Eval(string request) =>
        JsonSerializer.Serialize(
            Analyzer.Eval(JsonSerializer.Deserialize(request, Json.Default.AnalyzeRequest)),
            Json.Default.EvalResponse);

    [JSExport]
    public static string Cast(string request) =>
        JsonSerializer.Serialize(
            Analyzer.Cast(JsonSerializer.Deserialize(request, Json.Default.CastRequest)),
            Json.Default.CastResponse);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AnalyzeRequest))]
[JsonSerializable(typeof(AnalyzeResponse))]
[JsonSerializable(typeof(EvalResponse))]
[JsonSerializable(typeof(CastRequest))]
[JsonSerializable(typeof(CastResponse))]
internal partial class Json : JsonSerializerContext;
