using System.Diagnostics;
using TouhouChess.Core.Online;
using TouhouChess.Core.Spell;

public static class DeckLab
{
    // Plies, not full moves. A game still going at this point is scored as a draw.
    private const int MaxPlies = 400;
    // The strongest setting of each of the online bot's six strength dials.
    private static readonly int[] TopDials = { 6, 6, 6, 6, 6, 6 };
    // Elo points per logit, so card ratings print on a familiar scale.
    private const double EloPerLogit = 400 / 2.302585;
    // League seeds start here so they never repeat a screening game.
    private const int LeagueSeedBase = 1_000_000_000;
    // Climb seeds are base + game * 64 + gauntlet slot, so every deck meets a given opponent on the same seeds.
    private const int ClimbSeedBase = 1_500_000_000;
    // Games per gauntlet opponent at each rung of the climb; even, so colours split evenly.
    private static readonly int[] ClimbRungs = { 4, 20, 60 };
    // Swaps kept after each rung but the last. The incumbent always goes on as well.
    private static readonly int[] ClimbKeep = { 10, 3 };
    private const int GauntletSize = 6;

    private const string Dir = "deck-results";
    private static readonly string ScreenFile = Path.Combine(Dir, "screen.csv");
    private static readonly string LeagueFile = Path.Combine(Dir, "league.csv");
    private static readonly string ClimbFile = Path.Combine(Dir, "climb.csv");

    private sealed record Game(int Seed, string[] White, string[] Black, double Score);

    public static void Run(string[] args)
    {
        Directory.CreateDirectory(Dir);
        string mode = args.ElementAtOrDefault(1);
        int Arg(int i, int fallback) => args.Length > i ? int.Parse(args[i]) : fallback;
        if (mode == "screen")
        {
            Screen(Arg(2, 4000));
        }
        else if (mode == "league")
        {
            League(Arg(2, 20), Arg(3, 16));
        }
        else if (mode == "climb")
        {
            Climb(args.ElementAtOrDefault(2) ?? LeagueFile);
        }
        else
        {
            Console.WriteLine("usage: decks screen [games=4000] | decks league [gamesPerPair=20] [candidates=16] | decks climb [leagueFile]");
        }
    }

    // White's score: 1 for a win, 0.5 for a draw, 0 for a loss.
    private static double Play(string[] white, string[] black, int seed)
    {
        var setup = ReplaySetup.CustomDecks(seed, white, black);
        // Chara stays null so both sides get the same default playing style; only the decks differ.
        var bots = new[]
        {
            new OnlineBot(new BotSpec { Seed = seed, Dials = TopDials, Color = 0, Setup = setup }),
            new OnlineBot(new BotSpec { Seed = seed, Dials = TopDials, Color = 1, Setup = setup }),
        };
        var game = bots[0].Replay;
        while (!game.Over && game.MoveCount < MaxPlies)
        {
            var turn = bots[game.SideToMove].Think(CancellationToken.None);
            foreach (string token in turn.Tokens)
            {
                foreach (var bot in bots)
                {
                    if (!bot.Apply(token))
                    {
                        throw new InvalidOperationException($"seed {seed}: token '{token}' rejected: {bot.Error}");
                    }
                }
            }
        }
        if (!game.Over)
        {
            return 0.5;
        }
        return GameReplay.Winner(game.Status) switch { 0 => 1, 1 => 0, _ => 0.5 };
    }

    private static void Screen(int games)
    {
        var normals = SpellCatalog.All.Where(c => !c.Ultimate).Select(c => c.Id).ToArray();
        var ultimates = SpellCatalog.All.Where(c => c.Ultimate).Select(c => c.Id).ToArray();
        // C(n,5) decks have no ultimate and u·C(n,4) have one; the ratio of the two reduces to this.
        double noUltimateShare = (normals.Length - 4.0) / (normals.Length - 4.0 + 5.0 * ultimates.Length);
        string[] RandomDeck(Random rng)
        {
            bool withUltimate = rng.NextDouble() >= noUltimateShare;
            var deck = normals.OrderBy(_ => rng.Next()).Take(withUltimate ? 4 : 5).ToList();
            if (withUltimate)
            {
                deck.Add(ultimates[rng.Next(ultimates.Length)]);
            }
            return Sorted(deck);
        }

        var previous = Load(ScreenFile);
        int firstSeed = previous.Count == 0 ? 0 : previous.Max(g => g.Seed) + 1;
        int pairs = Math.Max(1, games / 2);
        using var log = new GameLog(ScreenFile, pairs * 2);
        Parallel.For(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, pair =>
        {
            int seed = firstSeed + pair * 2;
            var rng = new Random(seed);
            var a = RandomDeck(rng);
            var b = RandomDeck(rng);
            // Both colours with each pairing, so neither deck keeps White's first-move edge.
            log.Add(new Game(seed, a, b, Play(a, b, seed)));
            log.Add(new Game(seed + 1, b, a, Play(b, a, seed + 1)));
        });

        var all = Load(ScreenFile);
        var (edge, ratings) = Fit(all);
        Console.WriteLine($"\n{all.Count} screening games. White edge {edge * EloPerLogit:+0;-0} Elo.");
        var names = CardNames();
        var appearances = all.SelectMany(g => g.White.Concat(g.Black)).CountBy(id => id).ToDictionary();
        Console.WriteLine($"{"card",-28} {"Elo",6} {"±",5} {"games",6}");
        foreach (var (id, r) in ratings.OrderByDescending(p => p.Value))
        {
            int n = appearances.GetValueOrDefault(id);
            // Rough standard error: each appearance carries about p(1-p) ≈ 1/4 of a logit's information.
            double se = n == 0 ? double.NaN : 2 / Math.Sqrt(n) * EloPerLogit;
            Console.WriteLine($"{names[id],-28} {r * EloPerLogit,6:+0;-0} {se,5:0} {n,6}");
        }
    }

    private static void League(int gamesPerPair, int candidates)
    {
        var screened = Load(ScreenFile);
        if (screened.Count == 0)
        {
            Console.WriteLine("Run 'decks screen' first; the league picks its candidates from those results.");
            return;
        }
        var (_, ratings) = Fit(screened);
        var characterDecks = SpellCatalog.All.Select(c => c.Owner).Distinct()
            .ToDictionary(o => Key(Sorted(ReplaySetup.CharaDeck(o))), o => o);
        var decks = BestPredicted(ratings, candidates).Select(Key)
            .Concat(characterDecks.Keys).Distinct().Select(k => k.Split('+')).ToArray();

        var matchups = new List<(int a, int b, int seed)>();
        for (int i = 0; i < decks.Length; i++)
        {
            for (int j = i + 1; j < decks.Length; j++)
            {
                for (int k = 0; k < gamesPerPair; k++)
                {
                    matchups.Add((i, j, LeagueSeedBase + matchups.Count));
                }
            }
        }
        Console.WriteLine($"{decks.Length} decks, {matchups.Count} games.");

        if (File.Exists(LeagueFile))
        {
            File.Delete(LeagueFile);
        }
        var score = new double[decks.Length];
        var played = new int[decks.Length];
        using (var log = new GameLog(LeagueFile, matchups.Count))
        {
            Parallel.ForEach(matchups, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, m =>
            {
                // Alternate colours within each pairing.
                bool aWhite = m.seed % 2 == 0;
                var (white, black) = aWhite ? (decks[m.a], decks[m.b]) : (decks[m.b], decks[m.a]);
                double result = Play(white, black, m.seed);
                double aScore = aWhite ? result : 1 - result;
                lock (score)
                {
                    score[m.a] += aScore;
                    score[m.b] += 1 - aScore;
                    played[m.a]++;
                    played[m.b]++;
                }
                log.Add(new Game(m.seed, white, black, result));
            });
        }

        var names = CardNames();
        Console.WriteLine($"\n{"score",6} {"±",4} {"pred",5}  deck");
        foreach (int i in Enumerable.Range(0, decks.Length).OrderByDescending(i => score[i] / played[i]))
        {
            double p = score[i] / played[i];
            // Binomial standard error, ignoring draws, which only make it smaller.
            double se = Math.Sqrt(p * (1 - p) / played[i]);
            double predicted = decks[i].Sum(id => ratings[id]) * EloPerLogit;
            string label = characterDecks.TryGetValue(Key(decks[i]), out string owner) ? $" [{owner}]" : "";
            Console.WriteLine($"{p * 100,5:0.0}% {se * 100,4:0.0} {predicted,5:+0;-0}  {string.Join(", ", decks[i].Select(id => names[id]))}{label}");
        }
    }

    // Hill-climbs from a league's best deck: each round tries every one-card swap against the league's top decks,
    // narrowing the field rung by rung, and moves to the best swap if it clearly beats the current deck.
    private static void Climb(string leagueFile)
    {
        var league = Load(leagueFile);
        if (league.Count == 0)
        {
            Console.WriteLine($"No games in {leagueFile}; run a league first.");
            return;
        }
        var gauntlet = Standings(league).Take(GauntletSize).ToList();
        var incumbent = gauntlet[0];
        var names = CardNames();
        // Games are deterministic, so earlier results replay from here and an interrupted climb resumes where it stopped.
        var cache = Load(ClimbFile).ToDictionary(g => (Key(g.White), Key(g.Black), g.Seed), g => g.Score);
        var visited = new HashSet<string> { Key(incumbent) };

        for (int round = 1; ; round++)
        {
            Console.WriteLine($"\nRound {round}. Current deck: {string.Join(", ", incumbent.Select(id => names[id]))}");
            var field = Neighbours(incumbent).Where(d => !visited.Contains(Key(d))).Prepend(incumbent).ToList();
            Dictionary<string, double> scores = null;
            for (int rung = 0; rung < ClimbRungs.Length; rung++)
            {
                Console.WriteLine($"Rung {rung + 1}: {field.Count} decks, {ClimbRungs[rung]} games against each of {gauntlet.Count} opponents.");
                scores = Evaluate(field, gauntlet, ClimbRungs[rung], cache);
                if (rung < ClimbKeep.Length)
                {
                    field = field.Skip(1).OrderByDescending(d => scores[Key(d)]).Take(ClimbKeep[rung]).Prepend(incumbent).ToList();
                }
            }

            int games = ClimbRungs[^1] * gauntlet.Count;
            double SE(string[] d) => Math.Sqrt(scores[Key(d)] * (1 - scores[Key(d)]) / games);
            Console.WriteLine($"\n{"score",6} {"±",4}  change");
            foreach (var d in field.OrderByDescending(d => scores[Key(d)]))
            {
                string change = d == incumbent
                    ? "(current deck)"
                    : $"-{names[incumbent.Except(d).Single()]}  +{names[d.Except(incumbent).Single()]}";
                Console.WriteLine($"{scores[Key(d)] * 100,5:0.0}% {SE(d) * 100,4:0.0}  {change}");
            }

            var best = field.Skip(1).MaxBy(d => scores[Key(d)]);
            double lead = best == null ? 0 : scores[Key(best)] - scores[Key(incumbent)];
            // One standard error rather than a full significance test: single swaps move the score by a few points
            // at most, and a stricter bar would stall the climb on swaps that are real but small.
            if (best == null || lead <= Math.Sqrt(SE(best) * SE(best) + SE(incumbent) * SE(incumbent)))
            {
                Console.WriteLine($"\nNo swap clearly beats the current deck. Final deck:");
                foreach (string id in incumbent)
                {
                    Console.WriteLine($"  {names[id]}");
                }
                return;
            }
            Console.WriteLine($"\nSwap adopted (+{lead * 100:0.0} points).");
            incumbent = best;
            visited.Add(Key(best));
            gauntlet.Add(best);
        }
    }

    // Mean score of each deck over its first perOpponent games against every gauntlet deck, playing only uncached games.
    private static Dictionary<string, double> Evaluate(
        List<string[]> decks, List<string[]> gauntlet, int perOpponent, Dictionary<(string, string, int), double> cache)
    {
        var games = new List<(string[] deck, string[] white, string[] black, int seed)>();
        foreach (var deck in decks)
        {
            for (int slot = 0; slot < gauntlet.Count; slot++)
            {
                if (Key(deck) == Key(gauntlet[slot]))
                {
                    continue;
                }
                for (int k = 0; k < perOpponent; k++)
                {
                    var (white, black) = k % 2 == 0 ? (deck, gauntlet[slot]) : (gauntlet[slot], deck);
                    games.Add((deck, white, black, ClimbSeedBase + k * 64 + slot));
                }
            }
        }

        var todo = games.Where(g => !cache.ContainsKey((Key(g.white), Key(g.black), g.seed))).ToList();
        if (todo.Count > 0)
        {
            using var log = new GameLog(ClimbFile, todo.Count);
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, g =>
            {
                double result = Play(g.white, g.black, g.seed);
                lock (cache)
                {
                    cache[(Key(g.white), Key(g.black), g.seed)] = result;
                }
                log.Add(new Game(g.seed, g.white, g.black, result));
            });
        }

        var total = decks.ToDictionary(Key, d => 0.0);
        foreach (var g in games)
        {
            double whiteScore = cache[(Key(g.white), Key(g.black), g.seed)];
            total[Key(g.deck)] += g.deck == g.white ? whiteScore : 1 - whiteScore;
        }
        // A deck facing itself in the gauntlet scores 0.5 by symmetry, so those games are counted without being played.
        return decks.ToDictionary(Key, d => (total[Key(d)] + 0.5 * perOpponent * gauntlet.Count(o => Key(o) == Key(d)))
            / (perOpponent * gauntlet.Count));
    }

    // Every legal deck one card away: any card swapped for any card not in the deck, keeping at most one ultimate.
    private static IEnumerable<string[]> Neighbours(string[] deck)
    {
        var ultimates = SpellCatalog.All.Where(c => c.Ultimate).Select(c => c.Id).ToHashSet();
        foreach (string removed in deck)
        {
            foreach (string added in SpellCatalog.All.Select(c => c.Id).Where(id => !deck.Contains(id)))
            {
                var next = deck.Where(id => id != removed).Append(added).ToArray();
                if (next.Count(ultimates.Contains) <= 1)
                {
                    yield return Sorted(next);
                }
            }
        }
    }

    private static List<string[]> Standings(List<Game> games)
    {
        var score = new Dictionary<string, double>();
        var played = new Dictionary<string, int>();
        foreach (var g in games)
        {
            string w = Key(g.White), b = Key(g.Black);
            score[w] = score.GetValueOrDefault(w) + g.Score;
            score[b] = score.GetValueOrDefault(b) + 1 - g.Score;
            played[w] = played.GetValueOrDefault(w) + 1;
            played[b] = played.GetValueOrDefault(b) + 1;
        }
        return played.Keys.OrderByDescending(k => score[k] / played[k]).Select(k => k.Split('+')).ToList();
    }

    // Logistic model: P(White scores) = σ(edge + Σ White's card ratings − Σ Black's card ratings), draws counting as half.
    private static (double edge, Dictionary<string, double> ratings) Fit(List<Game> games)
    {
        var ids = SpellCatalog.All.Select(c => c.Id).ToArray();
        var index = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
        var rows = games.Select(g => (w: g.White.Select(id => index[id]).ToArray(), b: g.Black.Select(id => index[id]).ToArray(), g.Score)).ToArray();
        var r = new double[ids.Length];
        var grad = new double[ids.Length];
        double edge = 0;
        // Small ridge penalty keeps rarely seen cards near zero instead of drifting.
        const double ridge = 1e-3;
        const double step = 2.0;
        for (int iter = 0; iter < 3000 && rows.Length > 0; iter++)
        {
            Array.Clear(grad);
            double edgeGrad = 0;
            foreach (var (w, b, s) in rows)
            {
                double x = edge;
                foreach (int c in w) x += r[c];
                foreach (int c in b) x -= r[c];
                double err = s - 1 / (1 + Math.Exp(-x));
                edgeGrad += err;
                foreach (int c in w) grad[c] += err;
                foreach (int c in b) grad[c] -= err;
            }
            edge += step * edgeGrad / rows.Length;
            for (int c = 0; c < r.Length; c++)
            {
                r[c] += step * (grad[c] / rows.Length - ridge * r[c]);
            }
        }
        // Only differences between decks are identifiable, so centre the ratings on zero.
        double mean = r.Average();
        return (edge, ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => r[p.i] - mean));
    }

    // Walks every legal deck (five normals, or four normals and one ultimate) and keeps the best by summed rating.
    private static List<string[]> BestPredicted(Dictionary<string, double> ratings, int count)
    {
        var normals = SpellCatalog.All.Where(c => !c.Ultimate).Select(c => c.Id).ToArray();
        var ultimates = SpellCatalog.All.Where(c => c.Ultimate).Select(c => c.Id).ToArray();
        var best = new PriorityQueue<string[], double>();
        void Offer(string[] deck)
        {
            double value = deck.Sum(id => ratings[id]);
            if (best.Count < count)
            {
                best.Enqueue(deck, value);
            }
            else if (best.TryPeek(out _, out double worst) && value > worst)
            {
                best.EnqueueDequeue(deck, value);
            }
        }
        foreach (var five in Combinations(normals, 5))
        {
            Offer(five);
        }
        foreach (var four in Combinations(normals, 4))
        {
            foreach (string u in ultimates)
            {
                Offer(four.Append(u).ToArray());
            }
        }
        return best.UnorderedItems.Select(x => Sorted(x.Element)).ToList();
    }

    private static IEnumerable<string[]> Combinations(string[] items, int k, int start = 0)
    {
        if (k == 0)
        {
            yield return Array.Empty<string>();
            yield break;
        }
        for (int i = start; i <= items.Length - k; i++)
        {
            foreach (var rest in Combinations(items, k - 1, i + 1))
            {
                yield return rest.Prepend(items[i]).ToArray();
            }
        }
    }

    private static string[] Sorted(IEnumerable<string> deck) => deck.Order(StringComparer.Ordinal).ToArray();

    private static string Key(string[] deck) => string.Join('+', deck);

    private static Dictionary<string, string> CardNames()
    {
        string assets = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local/share/Steam/steamapps/common/Touhou Chess/TOUHOU CHESS_Data/resources.assets");
        return CardText.Load(assets).ToDictionary(c => c.Id, c => $"{c.Name}{(c.Ultimate ? "*" : "")} ({c.Owner})");
    }

    private static List<Game> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new();
        }
        return File.ReadLines(path)
            .Select(line => line.Split(','))
            .Where(f => f.Length == 4)
            .Select(f => new Game(int.Parse(f[0]), f[1].Split('+'), f[2].Split('+'), double.Parse(f[3])))
            .ToList();
    }

    // Appends each finished game straight away so an interrupted run keeps its results.
    private sealed class GameLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _total;
        private int _done;

        public GameLog(string path, int total)
        {
            _writer = new StreamWriter(path, append: true);
            _total = total;
        }

        public void Add(Game g)
        {
            lock (_writer)
            {
                _writer.WriteLine($"{g.Seed},{Key(g.White)},{Key(g.Black)},{g.Score}");
                _writer.Flush();
                _done++;
                if (_done % 50 == 0 || _done == _total)
                {
                    double perGame = _clock.Elapsed.TotalSeconds / _done;
                    var eta = TimeSpan.FromSeconds(perGame * (_total - _done));
                    Console.WriteLine($"{_done}/{_total} games, {perGame:0.00}s per game, ~{eta:hh\\:mm} left");
                }
            }
        }

        public void Dispose() => _writer.Dispose();
    }
}
