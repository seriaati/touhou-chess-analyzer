if (args.FirstOrDefault() == "decks")
{
    DeckLab.Run(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5000");
var app = builder.Build();

string gameData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".local/share/Steam/steamapps/common/Touhou Chess/TOUHOU CHESS_Data");
var cards = CardText.Load(Path.Combine(gameData, "resources.assets"));
var images = GameImages.Load(gameData);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/cards", () => cards);
app.MapGet("/api/avatar/{owner}.png", (string owner) => Image(images.Avatars, owner));
app.MapGet("/api/icon/{name}.png", (string name) => Image(images.Icons, name));
app.MapPost("/api/analyze", (AnalyzeRequest req, CancellationToken ct) => Task.Run(() => Analyzer.Run(req, ct), ct));

app.Run();

static IResult Image(Dictionary<string, byte[]> set, string key) =>
    set.TryGetValue(key, out var png) ? Results.File(png, "image/png") : Results.NotFound();
