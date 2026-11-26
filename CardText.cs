using System.Text;
using TouhouChess.Core.Spell;

public sealed record CardInfo(string Id, string Owner, int Cost, bool Ultimate, string Name, string Desc);

// Card text in the DLL is Korean only; the English strings live in the game's Loc/ui.tsv TextAsset.
public static class CardText
{
    public static List<CardInfo> Load(string resourcesAssetsPath)
    {
        var english = ReadEnglishTable(resourcesAssetsPath);
        return SpellCatalog.All
            .Select(c => new CardInfo(
                c.Id, c.Owner, c.Cost, c.Ultimate,
                english.GetValueOrDefault(c.Name, c.Name),
                english.GetValueOrDefault(c.Desc, c.Desc)))
            .ToList();
    }

    private static Dictionary<string, string> ReadEnglishTable(string path)
    {
        var table = new Dictionary<string, string>();
        if (!File.Exists(path))
        {
            return table;
        }
        byte[] data = File.ReadAllBytes(path);
        // A serialized Unity TextAsset stores its text as an int32 byte length followed by UTF-8.
        int start = data.AsSpan().IndexOf("ko\ten\tja\tzh\twhere"u8);
        if (start < 4)
        {
            return table;
        }
        int length = BitConverter.ToInt32(data, start - 4);
        if (length <= 0 || start + length > data.Length)
        {
            return table;
        }
        string[] lines = Encoding.UTF8.GetString(data, start, length).Split('\n');
        foreach (string raw in lines.Skip(1))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] cols = line.Split('\t');
            if (cols.Length > 1 && cols[1].Length > 0)
            {
                table[Unescape(cols[0])] = Unescape(cols[1]);
            }
        }
        return table;
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");
}
