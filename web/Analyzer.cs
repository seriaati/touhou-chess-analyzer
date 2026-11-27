using System.Diagnostics;
using TouhouChess.Core;
using TouhouChess.Core.Spell;

public sealed record EffectInput(string Kind, string Square, int Turns, string Owner);

// A Youmu slash (SpellKind.HitAndRun or Slashes) in progress for the side to move. Before the first cut (Extra false)
// Square is the armed piece; after one it is the piece that cut and moves again. Kind and Left are SpellState's
// ArmKind/ArmLeft or ChainMode/ChainLeft. Cuts is only in responses: each square the piece can move to that cuts
// and keeps the turn, mapped to the slash after that cut.
public sealed record SlashChain(string Square, string Kind, int Left, bool Extra, Dictionary<string, SlashChain> Cuts = null);

public sealed record AnalyzeRequest(
    string Fen, int Mana, int OpponentMana, List<string> Hand, List<string> OpponentHand,
    List<string> OpponentPlayed, List<EffectInput> Effects, int ThinkMs, SlashChain Chain = null);

public sealed record OpponentReply(string CardId, string Copy, string TargetText, List<string> Affected);

// San is a SAN move or one of two markers: "(skip)" for a skipped opponent turn and "(cast)" where Reply is played.
// From/To are null for markers; Fen is the position after the ply, so it includes any card's changes to the board.
// Effects is set on the ply right after a card is cast: every effect on the board once the card resolved.
public sealed record Ply(string San, string From, string To, string Fen, List<EffectInput> Effects = null);

public sealed record PlayOption(
    string CardId, string Copy, List<string> Targets, string TargetText, List<string> Affected, int Cost,
    int Score, string ScoreText, int Gain, int Value, List<Ply> Line, OpponentReply Reply);

// Saving is the Ultimate being saved for when that alone kept the best card from being recommended.
public sealed record AnalyzeResponse(
    string Error, string SideToMove, PlayOption NoCard, List<PlayOption> Cards, string Recommended, string Saving = null);

// The card is cast by the side to move in Fen. Targets are the squares picked so far.
public sealed record CastRequest(
    string Fen, int Mana, int OpponentMana, List<string> OpponentPlayed, List<EffectInput> Effects,
    string CardId, string Copy, List<string> Targets);

// One of: Error; Copies (pick the card to copy first); Next (the squares the next target can be, StepKind says what
// they are); or the resolved position: Fen, every effect on the board, both sides' spirit power after the cast, and
// Chain when the card armed a piece.
public sealed record CastResponse(
    string Error, List<string> Copies = null, List<string> Next = null, string StepKind = null, int Step = 0, int Steps = 0,
    string Fen = null, List<EffectInput> Effects = null, int WhiteMana = 0, int BlackMana = 0, SlashChain Chain = null);

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
    // Opponent card replies are checked for our top few first moves and their top few targets per card.
    private const int ReplyMoves = 4;
    private const int ReplyPerCard = 4;
    // The floor for a reply search; a fixed 4 plies costs about 10-35 ms, so time slices below MinReplyMs fall back to it.
    private static readonly SearchLimits ReplyLimits = new() { MaxDepth = 4, MaxNodes = 12000 };
    private const int MinReplyMs = 20;
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
        // Effects on the board right after the card resolved, before any move.
        public List<SpellEffect> CastEffects;
    }

    private sealed class Response
    {
        public Move Move;
        public int Value;
        public List<Move> Pv;
        public Candidate Reply;
    }

    // The game's position hash leaves out card effects, so a single transposition table hands a search with a
    // frozen or warded piece the scores from a search where it was free (and back). Boards without effects share
    // one table; boards with effects get their own, cleared (about 0.35 ms) whenever the effects change.
    private sealed class Engine
    {
        private readonly Searcher _plain = new(20);
        private readonly Searcher _effects = new(20);
        private Board.SpellMasks _effectsFor;

        private Searcher For(Board b)
        {
            var masks = b.Masks;
            if (!masks.Any)
            {
                return _plain;
            }
            if (!masks.Equals(_effectsFor))
            {
                _effects.ClearTT();
                _effectsFor = masks;
            }
            return _effects;
        }

        public SearchResult Search(Board b, SearchLimits limits, CancellationToken ct) => For(b).Search(b, limits, ct);

        public SearchResult ScoreRootMoves(Board b, int depth, int margin, SearchLimits limits, CancellationToken ct, int minDepth, IList<Move> rootMoves) =>
            For(b).ScoreRootMoves(b, depth, margin, limits, ct, minDepth, rootMoves);

        // QuickEval runs only the quiescence search, which never reads the table.
        public int QuickEval(Board b) => _plain.QuickEval(b);
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

        var searcher = new Engine();
        var clock = Stopwatch.StartNew();
        int budget = Math.Clamp(req.ThinkMs, 500, 60000);

        var baseline = new Candidate { Start = board, Board = board.Clone(), State = state.Clone() };
        var groups = CardCandidates(searcher, board, state, us, ct);
        // A piece armed by a slash cast earlier this turn can still cut; those lines are plain moves, not card options.
        var chains = new List<Candidate>();
        if (state.ArmSq >= 0)
        {
            AddChains(searcher, baseline, us, chains, ct);
        }
        bool checkReplies = state.Sides[us ^ 1].Hand.Count > 0;

        var shallow = new SearchLimits { MaxDepth = 4, MaxNodes = 20000 };
        Evaluate(searcher, baseline, shallow, ct);
        var deep = new List<Candidate> { baseline };
        foreach (var group in groups.Values.Append(chains))
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
        double deepMs = deepEnd - clock.ElapsedMilliseconds;
        // The plain move is what we play unless a card clearly beats it, so it gets half the deep time
        // rather than an even share with every card candidate.
        int baselineMs = (int)Math.Max(100, deep.Count == 1 ? deepMs : deepMs / 2);
        int cardMs = (int)Math.Max(100, (deepMs - baselineMs) / Math.Max(1, deep.Count - 1));
        foreach (var c in deep)
        {
            Evaluate(searcher, c, new SearchLimits { MaxDepth = 64, TimeMs = c == baseline ? baselineMs : cardMs }, ct);
        }
        if (checkReplies)
        {
            var replyLimits = ReplyLimitsFor(budget - clock.ElapsedMilliseconds, deep.Count, state.Sides[us ^ 1].Hand.Distinct().Count());
            for (int i = 0; i < deep.Count; i++)
            {
                long end = clock.ElapsedMilliseconds + (budget - clock.ElapsedMilliseconds) / (deep.Count - i);
                CheckReplies(searcher, deep[i], us, replyLimits, () => clock.ElapsedMilliseconds >= end, ct);
            }
        }

        var noCard = deep.Where(c => c.Action == null).MaxBy(c => c.Score);
        var options = groups.Values
            .Select(g => g.Where(deep.Contains).MaxBy(c => c.Score))
            .Where(c => c != null)
            .Select(c => ToOption(c, noCard.Score, state, us))
            .OrderByDescending(o => o.Value)
            .ToList();

        var (recommended, saving) = Recommend(options, baseline, state, us);
        return new AnalyzeResponse(
            null, us == 0 ? "white" : "black", ToOption(noCard, noCard.Score, state, us),
            options, recommended, saving);
    }

    public static CastResponse Cast(CastRequest req)
    {
        if (!LooksLikeFen(req.Fen))
        {
            return new CastResponse("That position could not be read.");
        }
        SpellCard card = SpellCatalog.Get(req.CardId ?? "");
        if (card == null)
        {
            return new CastResponse($"Unknown card '{req.CardId}'.");
        }
        Board board;
        SpellState state;
        try
        {
            board = new Board(req.Fen);
            // The caster always gets to pay: the spirit power shown in the app may lag behind the real game.
            var analyzeReq = new AnalyzeRequest(req.Fen, Math.Max(req.Mana, card.Cost), req.OpponentMana,
                new() { card.Id }, new(), req.OpponentPlayed, req.Effects, 0);
            state = BuildState(board, analyzeReq, board.SideToMove);
        }
        catch (ArgumentException e)
        {
            return new CastResponse(e.Message);
        }
        catch (Exception)
        {
            return new CastResponse("That position could not be read.");
        }
        int us = board.SideToMove;
        if (card.Kind == SpellKind.Copy && req.Copy == null)
        {
            var copies = state.Copyable(us);
            return copies.Count > 0 ? new CastResponse(null, Copies: copies) : new CastResponse("There is no played card to copy.");
        }
        SpellCard effect = state.EffectOf(us, card, req.Copy);
        if (effect == null)
        {
            return new CastResponse("That card can't be copied.");
        }
        var targets = (req.Targets ?? new()).Select(Sq.Parse).ToList();
        int steps = SpellState.StepCount(effect);
        if (targets.Count < steps)
        {
            var next = state.NextTargets(board, us, card, targets, req.Copy);
            if (next.Count == 0)
            {
                return new CastResponse("It has nothing it can target on this board.");
            }
            return new CastResponse(null, Next: next.Select(Sq.Name).ToList(),
                StepKind: SpellState.StepKind(effect, targets.Count).ToString(), Step: targets.Count, Steps: steps);
        }
        var action = new SpellAction { CardId = card.Id, Copy = req.Copy, Targets = targets.ToArray() };
        if (state.Play(board, us, action) == null)
        {
            return new CastResponse("The game wouldn't allow it on this board.");
        }
        return new CastResponse(null, Fen: board.ToFen(), Effects: EffectsOut(state.Effects),
            WhiteMana: state.Sides[0].Mana, BlackMana: state.Sides[1].Mana,
            Chain: state.ArmSq >= 0 ? ChainOf(board, state, us) : null);
    }

    // Every cut the armed or chaining piece can make from here, each played through the game's own rules
    // (SpellState.ContinueChain decides whether a capture keeps the turn).
    private static SlashChain ChainOf(Board board, SpellState state, int us)
    {
        bool extra = state.ExtraStage == 2;
        int sq = extra ? state.ChainSq : state.ArmSq;
        var cuts = new Dictionary<string, SlashChain>();
        foreach (Move m in state.TurnMoves(board))
        {
            // Pawns on the analyzer's board always promote to a queen.
            if (m.From != sq || (m.IsPromotion && m.PromotionType != Piece.Queen))
            {
                continue;
            }
            var b = board.Clone();
            var s = state.Clone();
            b.MakeMove(m);
            s.AfterMove(b, m, us);
            if (s.ExtraStage == 2)
            {
                b.MakeNullMove();
                s.AfterSkip(b);
                cuts[Sq.Name(m.To)] = ChainOf(b, s, us);
            }
        }
        return new SlashChain(Sq.Name(sq), (extra ? state.ChainMode : state.ArmKind).ToString(),
            extra ? state.ChainLeft : state.ArmLeft, extra, cuts);
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
        if (req.Chain != null)
        {
            int sq = Sq.Parse(req.Chain.Square ?? "");
            if (sq < 0 || sq > 63 || !Enum.TryParse(req.Chain.Kind, out ChainKind kind) || kind == ChainKind.None)
            {
                throw new ArgumentException("That slash could not be read.");
            }
            // The slash card was cast this turn.
            st.UsedThisTurn = true;
            if (req.Chain.Extra)
            {
                st.ExtraStage = 2;
                st.ChainSq = sq;
                st.ChainMode = kind;
                st.ChainLeft = req.Chain.Left;
            }
            else
            {
                st.ArmSq = sq;
                st.ArmKind = kind;
                st.ArmLeft = req.Chain.Left;
            }
        }
        st.SyncMasks(board);
        return st;
    }

    private static Dictionary<string, List<Candidate>> CardCandidates(Engine searcher, Board board, SpellState state, int side, CancellationToken ct)
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

    // Splits the time left over the most reply searches CheckReplies can run: per candidate, up to 1 + ReplyMoves
    // first moves, each with a plain search plus ReplyPerCard targets per opponent card.
    private static SearchLimits ReplyLimitsFor(double msLeft, int candidates, int opponentCards)
    {
        double searches = candidates * (1 + ReplyMoves) * (1 + ReplyPerCard * opponentCards);
        int slice = (int)(msLeft / searches);
        return slice < MinReplyMs ? ReplyLimits : new SearchLimits { MaxDepth = 64, TimeMs = slice };
    }

    // The engine's search knows nothing about cards, so check whether one in the opponent's hand punishes the
    // move we'd play, and if so look for a first move that holds up better. Cards can only help the opponent,
    // so the result never raises the deep score. The fixed-size reply searches can run far slower in WebAssembly
    // than ReplyLimitsFor plans for, so other first moves are only tried until outOfTime.
    private static void CheckReplies(Engine searcher, Candidate c, int us, SearchLimits limits, Func<bool> outOfTime, CancellationToken ct)
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
            var (plain, _) = SearchPosition(searcher, b, s, limits, ct);
            var reply = BestReply(searcher, b, s, us ^ 1, limits, ct);
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
        var best = Respond(searcher, c.Board, c.State, c.Pv[0], us, limits, ct);
        if (best?.Reply == null)
        {
            return;
        }
        var allowed = c.State.TurnMoves(c.Board);
        var ranked = searcher.ScoreRootMoves(c.Board, 2, 2 * Evaluation.Mate, ReplyLimits, ct, 1, allowed).RootScores ?? new();
        foreach (var root in ranked.OrderByDescending(x => x.Score).Take(ReplyMoves))
        {
            if (outOfTime())
            {
                break;
            }
            if (root.Move.Equals(best.Move) || !allowed.Contains(root.Move))
            {
                continue;
            }
            var r = Respond(searcher, c.Board, c.State, root.Move, us, limits, ct);
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
    private static Response Respond(Engine searcher, Board board, SpellState state, Move m, int us, SearchLimits limits, CancellationToken ct)
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
        var (plain, pv) = SearchPosition(searcher, b, s, limits, ct);
        var reply = BestReply(searcher, b, s, us ^ 1, limits, ct);
        if (reply != null && reply.Score <= plain)
        {
            reply = null;
        }
        return new Response { Move = m, Value = -(reply?.Score ?? plain), Pv = pv, Reply = reply };
    }

    private static Candidate BestReply(Engine searcher, Board b, SpellState s, int side, SearchLimits limits, CancellationToken ct)
    {
        Candidate best = null;
        foreach (var group in CardCandidates(searcher, b, s, side, ct).Values)
        {
            foreach (var r in group.OrderByDescending(x => x.Quick).Take(ReplyPerCard))
            {
                Evaluate(searcher, r, limits, ct);
                if (best == null || r.Score > best.Score)
                {
                    best = r;
                }
            }
        }
        return best;
    }

    private static List<Candidate> Collect(Engine searcher, Board board, SpellState state, int us, SpellCard card, string copy, CancellationToken ct)
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
                    Action = cast.Action, Start = cast.Board, Board = b, State = s, CastEffects = cast.CastEffects,
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
                if (cast != null)
                {
                    AddChains(searcher, cast, us, found, ct);
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
    }

    // Mirrors SpellPlanner.PlanChain: follow every capture chain the armed piece allows, up to 4 extra captures.
    private static void AddChains(Engine searcher, Candidate armed, int us, List<Candidate> found, CancellationToken ct)
    {
        foreach (Move m in armed.State.TurnMoves(armed.Board))
        {
            if (m.From == armed.State.ArmSq && m.IsCapture)
            {
                Walk(armed.State, armed.Board, m, new List<Move>(), 0);
            }
        }

        void Walk(SpellState s0, Board b0, Move m, List<Move> line, int depth)
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
                    Walk(s, b, next, steps, depth + 1);
                }
                return;
            }
            found.Add(new Candidate
            {
                Action = armed.Action, Start = armed.Board, Board = b, State = s, CastEffects = armed.CastEffects,
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
        return new Candidate
        {
            Action = action, Start = b, Board = b, State = s, CastEffects = s.Effects.Select(e => e.Copy()).ToList(),
        };
    }

    private static void Evaluate(Engine searcher, Candidate c, SearchLimits limits, CancellationToken ct)
    {
        var (score, pv) = SearchPosition(searcher, c.Board, c.State, limits, ct);
        c.Score = c.OpponentToMove ? -score : score;
        c.Pv = pv;
    }

    private static (int score, List<Move> pv) SearchPosition(Engine searcher, Board b, SpellState st, SearchLimits limits, CancellationToken ct)
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
        if (st.Effects.Count == 0)
        {
            return SearchOnce(searcher, b, allowed, limits, ct);
        }
        var clock = Stopwatch.StartNew();
        var first = limits;
        if (limits.TimeMs > 0)
        {
            // Keep half the time for the search from where an effect wears off.
            first.TimeMs = Math.Max(1, limits.TimeMs / 2);
        }
        var (score, pv) = SearchOnce(searcher, b, allowed, first, ct);
        var rest = limits;
        if (limits.TimeMs > 0)
        {
            rest.TimeMs = Math.Max(1, limits.TimeMs - (int)clock.ElapsedMilliseconds);
        }
        var past = PastExpiry(searcher, b, st, pv, rest, ct);
        if (past != null)
        {
            return past.Value;
        }
        // No effect wore off along the line, so spend the rest of the time on the same position; the
        // transposition table lets this pick up where the first search stopped.
        return limits.TimeMs > 0 ? SearchOnce(searcher, b, allowed, rest, ct) : (score, pv);
    }

    private static (int score, List<Move> pv) SearchOnce(Engine searcher, Board b, List<Move> allowed, SearchLimits limits, CancellationToken ct)
    {
        var r = searcher.Search(b, limits, ct);
        if (allowed.Contains(r.BestMove))
        {
            return (r.Score, r.Pv);
        }
        // The plain move generator doesn't know every card rule (e.g. moves exposed by an expiring barrier).
        var rr = searcher.ScoreRootMoves(b, Math.Max(1, r.Depth - 1), 0, limits, ct, 1, allowed);
        return (rr.Score, new List<Move> { rr.BestMove });
    }

    // The game's search never ticks effects down, so a 2-turn ward or freeze lasts the whole line (a warded knight
    // eats a piece and is never recaptured). Play the line through the real rules up to the first ply where an
    // effect wears off and search again from there. Returns null when no effect wears off along the line.
    private static (int score, List<Move> pv)? PastExpiry(Engine searcher, Board board, SpellState state, List<Move> pv, SearchLimits limits, CancellationToken ct)
    {
        var b = board.Clone();
        var s = state.Clone();
        int effects = s.Effects.Count;
        for (int ply = 0; ply < pv.Count; ply++)
        {
            Move m = pv[ply];
            if (!s.TurnMoves(b).Contains(m))
            {
                return null;
            }
            int mover = b.SideToMove;
            b.MakeMove(m);
            s.AfterMove(b, m, mover);
            if (s.ExtraStage != 0)
            {
                return null;
            }
            s.BeginTurn(b);
            if (s.Effects.Count < effects)
            {
                int walked = ply + 1;
                var (score, line) = SearchPosition(searcher, b, s, limits, ct);
                // A mate found from the later position is that many plies further from the root.
                if (Evaluation.IsMateScore(score))
                {
                    score += score > 0 ? -walked : walked;
                }
                return (walked % 2 == 0 ? score : -score, pv.Take(walked).Concat(line).ToList());
            }
        }
        return null;
    }

    private static PlayOption ToOption(Candidate c, int baselineScore, SpellState state, int us)
    {
        SpellCard card = c.Action == null ? null : SpellCatalog.Get(c.Action.CardId);
        var (targets, targetText, affected) = Describe(c.Action);
        int gain = Clamp(c.Score) - Clamp(baselineScore);
        var line = Line(c.Start, c.Steps, c.Pv);
        if (c.Action != null && line.Count > 0)
        {
            line[0] = line[0] with { Effects = EffectsOut(c.CastEffects) };
        }
        OpponentReply reply = null;
        if (c.Reply != null)
        {
            var (_, replyText, replyAffected) = Describe(c.Reply.Action);
            reply = new OpponentReply(c.Reply.Action.CardId, c.Reply.Action.Copy, replyText, replyAffected);
            line.Add(new Ply("(cast)", null, null, c.Reply.Start.ToFen(), EffectsOut(c.Reply.CastEffects)));
            line.AddRange(Line(c.Reply.Start, c.Reply.Steps, c.Reply.Pv));
        }
        return new PlayOption(
            c.Action?.CardId, c.Action?.Copy, targets, targetText, affected,
            card?.Cost ?? 0, c.Score, ScoreText(c.Score), gain,
            card == null ? 0 : Value(card, gain, state, us),
            line.Take(MaxLinePlies + 1).ToList(), reply);
    }

    // Inverse of BuildState: the game shows (plies + 1) / 2 turns left.
    private static List<EffectInput> EffectsOut(List<SpellEffect> effects)
    {
        return effects
            .Select(e => (Name: EffectKinds.FirstOrDefault(k => k.Value == e.Kind).Key, Effect: e))
            .Where(x => x.Name != null)
            .Select(x => new EffectInput(x.Name, Sq.Name(x.Effect.Square), (x.Effect.Plies + 1) / 2, x.Effect.Owner == 1 ? "black" : "white"))
            .ToList();
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

    private static (string Recommended, string Saving) Recommend(List<PlayOption> options, Candidate baseline, SpellState state, int us)
    {
        var best = options.FirstOrDefault();
        if (best == null)
        {
            return (null, null);
        }
        if (baseline.State.TurnMoves(baseline.Board).Count == 0)
        {
            return (best.CardId, null);
        }
        SpellCard saving = SavingFor(state, us);
        bool worthIt = best.Gain >= 300 || best.Value >= 55;
        if (saving != null && best.CardId != saving.Id)
        {
            if (best.Gain >= 500)
            {
                return (best.CardId, null);
            }
            return (null, worthIt ? saving.Id : null);
        }
        return (worthIt ? best.CardId : null, null);
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

    private static List<Ply> Line(Board start, List<Move> steps, List<Move> pv)
    {
        var b = start.Clone();
        var line = new List<Ply>();
        foreach (Move m in steps.Concat(pv).Take(MaxLinePlies))
        {
            if (m.IsNull)
            {
                b.MakeNullMove();
                line.Add(new Ply("(skip)", null, null, b.ToFen()));
                continue;
            }
            string san = Notation.ToSan(b, m);
            b.MakeMove(m);
            line.Add(new Ply(san, Sq.Name(m.From), Sq.Name(m.To), b.ToFen()));
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
