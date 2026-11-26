string gameData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".local/share/Steam/steamapps/common/Touhou Chess/TOUHOU CHESS_Data");

switch (args.FirstOrDefault())
{
    case "decks":
        DeckLab.Run(args);
        break;
    case "export":
        Export();
        break;
    default:
        Console.WriteLine("usage: export | decks ...");
        Console.WriteLine("  export  copy the game's engine DLL, card text and images into lib/ and web/wwwroot/game/");
        break;
}

// The website can't read a Steam install, so it ships these copies instead.
void Export()
{
    File.Copy(Path.Combine(gameData, "Managed/TouhouChess.Core.dll"), "lib/TouhouChess.Core.dll", overwrite: true);

    string outDir = "web/wwwroot/game";
    var cards = CardText.Load(Path.Combine(gameData, "resources.assets"));
    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, "cards.json"),
        System.Text.Json.JsonSerializer.Serialize(cards, System.Text.Json.JsonSerializerOptions.Web));

    var images = GameImages.Load(gameData);
    WriteImages(Path.Combine(outDir, "avatars"), images.Avatars);
    WriteImages(Path.Combine(outDir, "icons"), images.Icons);
    Console.WriteLine($"Exported {cards.Count} cards, {images.Avatars.Count} avatars and {images.Icons.Count} icons.");
}

static void WriteImages(string dir, Dictionary<string, byte[]> set)
{
    Directory.CreateDirectory(dir);
    foreach (var (name, png) in set)
    {
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), png);
    }
}
