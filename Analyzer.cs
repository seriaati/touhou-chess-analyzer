using System.Diagnostics;
using TouhouChess.Core;
using TouhouChess.Core.Spell;

public sealed record EffectInput(string Kind, string Square, int Turns, string Owner);

public sealed record AnalyzeRequest(
    string Fen, int Mana, int OpponentMana, List<string> Hand, List<string> OpponentHand,
    List<string> OpponentPlayed, List<EffectInput> Effects, int ThinkMs);

public sealed record OpponentReply(string CardId, string Copy, string TargetText, List<string> Affected);

// Line holds SAN moves plus two markers: "(skip)" for a skipped opponent turn and "(cast)" where Reply is played.
public sealed record PlayOption(
    string CardId, string Copy, List<string> Targets, string TargetText, List<string> Affected, int Cost,
    int Score, string ScoreText, int Gain, int Value, List<string> Line, OpponentReply Reply);

public sealed record AnalyzeResponse(
    string Error, string SideToMove, PlayOption NoCard, List<PlayOption> Cards, string Recommended);

public static class Analyzer
{
    // Same caps the game's SpellPlanner uses when enumerating targets.
    private const int ActionCap = 160;
    private const int ChainTargets = 16;
    private const int ChainLeaves = 240;
    private const int ShallowPerCard = 8;
    private const int DeepPerCard = 2;
    private const int MaxLinePlies = 12;
    private const int EffectDepthCap = 16;
    // Opponent card replies are searched shallowly: our top few first moves, their top few targets per card.
    private const int ReplyMoves = 4;
    private const int ReplyPerCard = 4;
    private static readonly SearchLimits ReplyLimits = new() { MaxDepth = 4, MaxNodes = 12000 };
    // Fraction of the think time left for reply checks when the opponent holds cards.
    private const double ReplyBudgetShare = 0.35;

    private static readonly Dictionary<string, EffectKind> EffectKinds = new()
    {
        ["freeze"] = EffectKind.Freeze,
        ["protect"] = EffectKind.Ward,
        ["barrier"] = EffectKind.Barrier,
        ["leap"] = EffectKind.Leap,
        ["disarm"] = EffectKind.Disarm,
        ["pierce"] = EffectKind.Pierce,
        ["ice"] = EffectKind.Slip,
        ["counter"] = EffectKind.Counter,
        ["fate"] = EffectKind.Doom,
        ["clone"] = EffectKind.Phantom,
    };

    private sealed class Candidate
    {
        public SpellAction Action;
        public Board Start;
        public Board Board;
        public SpellState State;
        // Moves already decided before Board (The World's first move, a slash chain); Move.Null = the opponent's skipped turn.
        public List<Move> Steps = new();
        public bool OpponentToMove;
        public int Quick;
        public int Score;
        public List<Move> Pv = new();
        // The opponent's best card answer to the end of Pv, when it beats their best plain move.
        public Candidate Reply;
    }

    private sealed class Response
    {
        public Move Move;
        public int Value;
        public List<Move> Pv;
        public Candidate Reply;
    }

    public static AnalyzeResponse Run(AnalyzeRequest req, CancellationToken ct)
    {
        if (!LooksLikeFen(req.Fen))
        {
            return Fail("That FEN could not be read. It needs 8 ranks and one king per side.");
        }
        Board board;
        try
        {
            board = new Board(req.Fen);
        }
        catch (Exception)
        {
            return Fail("That FEN could not be read.");
        }
        int us = board.SideToMove;
        SpellState state;
        try
        {
            state = BuildState(board, req, us);
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }

        var searcher = new Searcher(20);
        var clock = Stopwatch.StartNew();
        int budget = Math.Clamp(req.ThinkMs, 500, 60000);

        var baseline = new Candidate { Start = board, Board = board.Clone(), State = state.Clone() };
        var groups = CardCandidates(searcher, board, state, us, ct);
        bool checkReplies = state.Sides[us ^ 1].Hand.Count > 0;

        var shallow = new SearchLimits { MaxDepth = 4, MaxNodes = 20000 };
        Evaluate(searcher, baseline, shallow, ct);
        var deep = new List<Candidate> { baseline };
        foreach (var group in groups.Values)
        {
            var top = group.OrderByDescending(c => c.Quick).Take(ShallowPerCard).ToList();
            foreach (var c in top)
            {
                if (clock.ElapsedMilliseconds > budget / 2)
                {
                    break;
                }
                Evaluate(searcher, c, shallow, ct);
            }
            deep.AddRange(top.OrderByDescending(c => c.Score).Take(DeepPerCard));
        }

        double deepEnd = budget * (checkReplies ? 1 - ReplyBudgetShare : 1);
        int slice = (int)Math.Max(100, (deepEnd - clock.ElapsedMilliseconds) / deep.Count);
        foreach (var c in deep)
        {
            Evaluate(searcher, c, new SearchLimits { MaxDepth = 64, TimeMs = slice }, ct);
        }
        if (checkReplies)
        {
            foreach (var c in deep)
            {
                CheckReplies(searcher, c, us, ct);
            }
        }

        var options = groups.Values
            .Select(g => g.Where(deep.Contains).MaxBy(c => c.Score))
            .Where(c => c != null)
            .Select(c => ToOption(c, baseline.Score, state, us))
            .OrderByDescending(o => o.Value)
            .ToList();

        return new AnalyzeResponse(
            null, us == 0 ? "white" : "black", ToOption(baseline, baseline.Score, state, us),
            options, Recommend(options, baseline, state, us));
    }

    private static SpellState BuildState(Board board, AnalyzeRequest req, int us)
    {
        var st = SpellState.Create(0, Array.Empty<string>(), Array.Empty<string>());
        foreach (var side in st.Sides)
        {
            side.Hand.Clear();
            side.Deck.Clear();
            side.Used.Clear();
            side.Played.Clear();
        }
        var hand = (req.Hand ?? new()).Distinct().ToList();
        var opponentHand = (req.OpponentHand ?? new()).Distinct().ToList();
        if (hand.Count > SpellState.HandCap || opponentHand.Count > SpellState.HandCap)
        {
            throw new ArgumentException($"A hand holds at most {SpellState.HandCap} cards.");
        }
        foreach (string id in hand.Concat(opponentHand).Concat(req.OpponentPlayed ?? new()))
        {
            if (SpellCatalog.Get(id) == null)
            {
                throw new ArgumentException($"Unknown card '{id}'.");
            }
        }
        st.Sides[us].Hand.AddRange(hand);
        st.Sides[us ^ 1].Hand.AddRange(opponentHand);
        foreach (var side in st.Sides)
        {
            // BeginTurn draws whenever FullmoveNumber passes 15 * (Scheduled + 1); there is no deck to draw from here.
            side.Scheduled = 1000;
        }
        st.Sides[us].Mana = Math.Clamp(req.Mana, 0, SpellState.ManaCap);
        st.Sides[us ^ 1].Mana = Math.Clamp(req.OpponentMana, 0, SpellState.ManaCap);
        st.Sides[us ^ 1].Played.AddRange((req.OpponentPlayed ?? new()).Distinct());
        st.TurnSide = us;

        foreach (var e in req.Effects ?? new())
        {
            if (!EffectKinds.TryGetValue(e.Kind ?? "", out var kind))
            {
                throw new ArgumentException($"Unknown effect '{e.Kind}'.");
            }
            int sq = Sq.Parse(e.Square ?? "");
            if (sq < 0 || sq > 63)
            {
                throw new ArgumentException($"Bad square '{e.Square}'.");
            }
            int owner = e.Owner == "black" ? 1 : 0;
            int turns = Math.Clamp(e.Turns, 1, 9);
            // Effects tick down once per ply and the game shows (plies+1)/2 turns left. On your turn an effect
            // you cast has ticked an even number of times, one the opponent cast an odd number.
            int plies = owner == us ? 2 * turns : 2 * turns - 1;
            st.Effects.Add(new SpellEffect { Kind = kind, Owner = owner, Square = sq, Plies = plies });
        }
        st.SyncMasks(board);
        return st;
    }

    private static Dictionary<string, List<Candidate>> CardCandidates(Searcher searcher, Board board, SpellState state, int side, CancellationToken ct)
    {
        var groups = new Dictionary<string, List<Candidate>>();
        foreach (string id in state.Sides[side].Hand.Distinct())
        {
            SpellCard card = SpellCatalog.Get(id);
            if (!state.Affordable(side, card))
            {
                continue;
            }
            IEnumerable<string> copies = card.Kind == SpellKind.Copy ? state.Copyable(side) : new string[] { null };
            foreach (string copy in copies)
            {
                var found = Collect(searcher, board, state, side, card, copy, ct);
                if (found.Count > 0)
                {
                    groups[copy == null ? id : id + ":" + copy] = found;
                }
            }
        }
        return groups;
    }

    // The engine's search knows nothing about cards, so check whether one in the opponent's hand punishes the
    // move we'd play, and if so look for a first move that holds up better. Cards can only help the opponent,
    // so the result never raises the deep score.
    private static void CheckReplies(Searcher searcher, Candidate c, int us, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }
        if (c.OpponentToMove)
        {
            var b = c.Board.Clone();
            var s = c.State.Clone();
            s.BeginTurn(b);
            var (plain, _) = SearchPosition(searcher, b, s, ReplyLimits, ct);
            var reply = BestReply(searcher, b, s, us ^ 1, ct);
            if (reply != null && reply.Score > plain)
            {
                c.Score = Math.Min(c.Score, -reply.Score);
                c.Reply = reply;
                c.Pv = new List<Move>();
            }
            return;
        }
        if (c.Pv.Count == 0)
        {
            return;
        }
        var best = Respond(searcher, c.Board, c.State, c.Pv[0], us, ct);
        if (best?.Reply == null)
        {
            return;
        }
        var allowed = c.State.TurnMoves(c.Board);
        var ranked = searcher.ScoreRootMoves(c.Board, 2, 2 * Evaluation.Mate, ReplyLimits, ct, 1, allowed).RootScores ?? new();
        foreach (var root in ranked.OrderByDescending(x => x.Score).Take(ReplyMoves))
        {
            if (root.Move.Equals(best.Move) || !allowed.Contains(root.Move))
            {
                continue;
            }
            var r = Respond(searcher, c.Board, c.State, root.Move, us, ct);
            if (r != null && r.Value > best.Value)
            {
                best = r;
            }
        }
        c.Score = Math.Min(c.Score, best.Value);
        c.Reply = best.Reply;
        c.Pv = best.Reply == null ? best.Pv.Prepend(best.Move).ToList() : new List<Move> { best.Move };
    }

    // Our move m, then the opponent's turn with their cards. Value is from our side.
    private static Response Respond(Searcher searcher, Board board, SpellState state, Move m, int us, CancellationToken ct)
    {
        var b = board.Clone();
        var s = state.Clone();
        b.MakeMove(m);
        s.AfterMove(b, m, us);
        if (s.ExtraStage == 2)
        {
            return null;
        }
        s.BeginTurn(b);
        var (plain, pv) = SearchPosition(searcher, b, s, ReplyLimits, ct);
        var reply = BestReply(searcher, b, s, us ^ 1, ct);
        if (reply != null && reply.Score <= plain)
        {
            reply = null;
        }
        return new Response { Move = m, Value = -(reply?.Score ?? plain), Pv = pv, Reply = reply };
    }

    private static Candidate BestReply(Searcher searcher, Board b, SpellState s, int side, CancellationToken ct)
    {
        Candidate best = null;
        foreach (var group in CardCandidates(searcher, b, s, side, ct).Values)
        {
            foreach (var r in group.OrderByDescending(x => x.Quick).Take(ReplyPerCard))
            {
                Evaluate(searcher, r, ReplyLimits, ct);
                if (best == null || r.Score > best.Score)
                {
                    best = r;
                }
            }
        }
        return best;
    }

    private static List<Candidate> Collect(Searcher searcher, Board board, SpellState state, int us, SpellCard card, string copy, CancellationToken ct)
    {
        var found = new List<Candidate>();
        SpellCard effect = state.EffectOf(us, card, copy);
        if (effect == null)
        {
            return found;
        }
        if (effect.Kind == SpellKind.Extra)
        {
            var cast = Cast(board, state, us, card, copy, new int[0]);
            if (cast == null)
            {
                return found;
            }
            foreach (Move first in cast.State.TurnMoves(cast.Board))
            {
                var b = cast.Board.Clone();
                var s = cast.State.Clone();
                b.MakeMove(first);
                s.AfterMove(b, first, us);
                b.MakeNullMove();
                s.AfterSkip(b);
                found.Add(new Candidate
                {
                    Action = cast.Action, Start = cast.Board, Board = b, State = s,
                    Steps = new List<Move> { first, Move.Null }, Quick = searcher.QuickEval(b),
                });
            }
            return found;
        }
        if (effect.Kind == SpellKind.HitAndRun || effect.Kind == SpellKind.Slashes)
        {
            foreach (int[] t in state.Actions(board, us, card, ChainTargets, copy))
            {
                var cast = Cast(board, state, us, card, copy, t);
                if (cast == null)
                {
                    continue;
                }
                foreach (Move m in cast.State.TurnMoves(cast.Board))
                {
                    if (m.From == t[0] && m.IsCapture)
                    {
                        Walk(cast, cast.State, cast.Board, m, new List<Move>(), 0);
                    }
                }
            }
            return found;
        }
        foreach (int[] t in state.Actions(board, us, card, ActionCap, copy))
        {
            var cast = Cast(board, state, us, card, copy, t);
            if (cast != null)
            {
                cast.Quick = searcher.QuickEval(cast.Board);
                found.Add(cast);
            }
        }
        return found;

        // Mirrors SpellPlanner.PlanChain: follow every capture chain the card allows, up to 4 extra captures.
        void Walk(Candidate cast, SpellState s0, Board b0, Move m, List<Move> line, int depth)
        {
            if (found.Count >= ChainLeaves || ct.IsCancellationRequested)
            {
                return;
            }
            var s = s0.Clone();
            var b = b0.Clone();
            b.MakeMove(m);
            s.AfterMove(b, m, us);
            var steps = new List<Move>(line) { m };
            if (s.ExtraStage == 2 && depth < 4)
            {
                b.MakeNullMove();
                s.AfterSkip(b);
                steps.Add(Move.Null);
                foreach (Move next in s.TurnMoves(b))
                {
                    Walk(cast, s, b, next, steps, depth + 1);
                }
                return;
            }
            found.Add(new Candidate
            {
                Action = cast.Action, Start = cast.Board, Board = b, State = s,
                Steps = steps, OpponentToMove = true, Quick = -searcher.QuickEval(b),
            });
        }
    }

    private static Candidate Cast(Board board, SpellState state, int us, SpellCard card, string copy, int[] targets)
    {
        var b = board.Clone();
        var s = state.Clone();
        var action = new SpellAction { CardId = card.Id, Copy = copy, Targets = (int[])targets.Clone() };
        if (s.Play(b, us, action) == null)
        {
            return null;
        }
        return new Candidate { Action = action, Start = b, Board = b, State = s };
    }

    private static void Evaluate(Searcher searcher, Candidate c, SearchLimits limits, CancellationToken ct)
    {
        var (score, pv) = SearchPosition(searcher, c.Board, c.State, limits, ct);
        c.Score = c.OpponentToMove ? -score : score;
        c.Pv = pv;
    }

    private static (int score, List<Move> pv) SearchPosition(Searcher searcher, Board b, SpellState st, SearchLimits limits, CancellationToken ct)
    {
        List<Move> allowed = st.TurnMoves(b);
        if (allowed.Count == 0)
        {
            return (b.InCheck() ? -Evaluation.Mate : 0, new List<Move>());
        }
        if (b.Masks.Any)
        {
            // Effects never expire inside the game's search, so very deep lines treat a 2-turn freeze as permanent
            // (a K+Q vs K with a frozen queen drops from +10 to 0.00 past depth ~24).
            limits.MaxDepth = Math.Min(limits.MaxDepth, EffectDepthCap);
        }
        var r = searcher.Search(b, limits, ct);
        if (allowed.Contains(r.BestMove))
        {
            return (r.Score, r.Pv);
        }
        // The plain move generator doesn't know every card rule (e.g. moves exposed by an expiring barrier).
        var rr = searcher.ScoreRootMoves(b, Math.Max(1, r.Depth - 1), 0, limits, ct, 1, allowed);
        return (rr.Score, new List<Move> { rr.BestMove });
    }

    private static PlayOption ToOption(Candidate c, int baselineScore, SpellState state, int us)
    {
        SpellCard card = c.Action == null ? null : SpellCatalog.Get(c.Action.CardId);
        var (targets, targetText, affected) = Describe(c.Action);
        int gain = Clamp(c.Score) - Clamp(baselineScore);
        var line = Line(c.Start, c.Steps, c.Pv);
        OpponentReply reply = null;
        if (c.Reply != null)
        {
            var (_, replyText, replyAffected) = Describe(c.Reply.Action);
            reply = new OpponentReply(c.Reply.Action.CardId, c.Reply.Action.Copy, replyText, replyAffected);
            line.Add("(cast)");
            line.AddRange(Line(c.Reply.Start, c.Reply.Steps, c.Reply.Pv));
        }
        return new PlayOption(
            c.Action?.CardId, c.Action?.Copy, targets, targetText, affected,
            card?.Cost ?? 0, c.Score, ScoreText(c.Score), gain,
            card == null ? 0 : Value(card, gain, state, us),
            line.Take(MaxLinePlies + 1).ToList(), reply);
    }

    private static (List<string> Targets, string Text, List<string> Affected) Describe(SpellAction action)
    {
        SpellCard effect = action == null ? null : SpellCatalog.Get(action.Copy ?? action.CardId);
        int[] targets = action?.Targets ?? new int[0];
        var affected = targets.SelectMany(t => SpellState.Reach(effect, t) ?? new[] { t }).Distinct().Select(Sq.Name).ToList();
        return (targets.Select(Sq.Name).ToList(), effect == null ? "" : TargetText(effect, targets, affected), affected);
    }

    // Area cards only use the target square to pick a file or region, so name the region instead.
    private static string TargetText(SpellCard effect, int[] targets, List<string> affected)
    {
        if (targets.Length == 0)
        {
            return "";
        }
        string first = Sq.Name(targets[0]);
        var files = affected.Select(s => s[0]).Distinct().OrderBy(f => f).ToList();
        return effect.Kind switch
        {
            SpellKind.FreezeFile or SpellKind.BanishFile => $"{first[0]}-file",
            SpellKind.March => $"{files.First()}–{files.Last()} files",
            SpellKind.FreezeArea or SpellKind.BanishArea or SpellKind.BanishAdjacent => $"around {first}",
            SpellKind.BarrierBlock => $"2×2 at {string.Join(", ", affected)}",
            _ => string.Join(", ", targets.Select(Sq.Name)),
        };
    }

    // SpellPlanner.Plan's worth-it formula at the game's default Eagerness of 0.5, without its random noise.
    private static int Value(SpellCard card, int gain, SpellState state, int us)
    {
        SpellCard saving = SavingFor(state, us);
        bool atCap = state.Sides[us].Mana >= 49;
        float penalty = card.Cost * 4 * (atCap ? 0.4f : 1f) * (card.Ultimate ? 0.5f : 1f)
            + (saving != null && saving != card ? card.Cost * 3 : 0);
        return (int)(gain - penalty + (card.Ultimate && gain >= 40 ? 120 : 0));
    }

    private static string Recommend(List<PlayOption> options, Candidate baseline, SpellState state, int us)
    {
        var best = options.FirstOrDefault();
        if (best == null)
        {
            return null;
        }
        if (baseline.State.TurnMoves(baseline.Board).Count == 0)
        {
            return best.CardId;
        }
        SpellCard saving = SavingFor(state, us);
        if (saving != null && best.CardId != saving.Id)
        {
            return best.Gain >= 500 ? best.CardId : null;
        }
        return best.Gain >= 300 || best.Value >= 55 ? best.CardId : null;
    }

    private static SpellCard SavingFor(SpellState state, int us)
    {
        return state.Sides[us].Hand
            .Select(SpellCatalog.Get)
            .LastOrDefault(c => c.Ultimate && c.Cost > state.Sides[us].Mana);
    }

    private static int Clamp(int score) => Math.Clamp(score, -30000, 30000);

    private static string ScoreText(int s)
    {
        if (!Evaluation.IsMateScore(s))
        {
            return (s / 100.0).ToString("+0.00;-0.00;0.00");
        }
        int moves = (Evaluation.Mate - Math.Abs(s) + 1) / 2;
        if (moves == 0)
        {
            return s > 0 ? "Mate" : "Mated";
        }
        return (s > 0 ? "M" : "-M") + moves;
    }

    private static List<string> Line(Board start, List<Move> steps, List<Move> pv)
    {
        var b = start.Clone();
        var line = new List<string>();
        foreach (Move m in steps.Concat(pv).Take(MaxLinePlies))
        {
            if (m.IsNull)
            {
                line.Add("(skip)");
                b.MakeNullMove();
                continue;
            }
            line.Add(Notation.ToSan(b, m));
            b.MakeMove(m);
        }
        return line;
    }

    // The game's FEN parser silently accepts garbage, so check the basics first.
    private static bool LooksLikeFen(string fen)
    {
        string placement = fen?.Trim().Split(' ')[0] ?? "";
        string[] ranks = placement.Split('/');
        if (ranks.Length != 8 || placement.Count(ch => ch == 'K') != 1 || placement.Count(ch => ch == 'k') != 1)
        {
            return false;
        }
        foreach (string rank in ranks)
        {
            int width = 0;
            foreach (char ch in rank)
            {
                if (char.IsDigit(ch))
                {
                    width += ch - '0';
                }
                else if ("pnbrqkPNBRQK".Contains(ch))
                {
                    width++;
                }
                else
                {
                    return false;
                }
            }
            if (width != 8)
            {
                return false;
            }
        }
        return true;
    }

    private static AnalyzeResponse Fail(string error) => new(error, null, null, new List<PlayOption>(), null);
}
