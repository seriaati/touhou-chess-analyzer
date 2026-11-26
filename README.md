# Touhou Chess Analyzer

A card-aware move analyzer for [Touhou Chess](https://store.steampowered.com/app/5482350/). Simply set up the current game's condition and click "Analyze", the analyzer will tell you the next best move.

WARNING: This tools is for educational use only, you can use it for analyzing games afterwards, but do NOT use it during online matches, that is cheating!

## How the analyzer works

The analyzer doesn't reimplement the game. Touhou Chess keeps its rules and AI in a plain .NET library, `TouhouChess.Core.dll`, which ships with the game. This project calls that library directly. Move generation, every card's effect and targeting rules, and the search engine are therefore the game's own code.

That engine is a classic chess search: alpha-beta (negamax) with quiescence search, null-move pruning, a transposition table, and killer-move and history heuristics, scored by the game's hand-written evaluation. The in-game bot is built on the same search.

When you click Analyze:

1. It lists every card you can cast, with every legal choice of targets, using the game's own rules. Like the game's AI, it caps how many target combinations it tries for a card.
2. It searches all of these briefly, then gives the most promising ones and the plain no-card move most of your think time for a deeper search.
3. If you entered cards the opponent holds, it checks their best card reply to each of the top lines.
4. It recommends a card only if casting it clearly beats not casting it. It uses the same thresholds as the game's AI, which holds back spirit power when you're saving for an Ultimate.

The game's position hash ignores card effects, so a frozen piece and a free one would share search results. The analyzer keeps a separate search table for positions with effects to avoid that.

The rules are exact, and the search is the same engine the game uses. Still, "best move" means the best move this engine finds in the time it has, not a proven one:

- It searches for a limited time, and depth depends on your device and the think time you pick.
- The evaluation is hand-written, not perfect.
- It only knows the opponent cards you enter, and it doesn't model future card draws.

Longer think times give more reliable answers.

## Deck analysis

The repository also has a command-line tool that rates cards and decks by having the game's own bot play itself at top strength. It runs on your machine, needs the game installed, and writes results to `deck-results/`:

```sh
dotnet run -- decks screen   # thousands of games between random decks, giving each card a rating
dotnet run -- decks league   # round robin of the best-rated custom decks against every character's fixed deck
dotnet run -- decks climb    # improves the league winner one card swap at a time until no swap clearly helps
```

`screen` plays random legal decks (five cards, at most one Ultimate) against each other with both colours. `league` uses those ratings to pick the most promising custom decks, adds each character's fixed deck, and ranks them by score. `climb` starts from the league's best deck and tries every one-card swap against the league's top decks. It adopts a swap only when it clearly beats the current deck, and repeats until nothing does. An interrupted climb resumes where it stopped.

The results show what wins between bots, which may not be the same as what works best for a human player.

## Running it locally

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project web
```

Then open http://localhost:5000.

## Deploying

Pushing to `master` runs `.github/workflows/pages.yml`, which builds the site with AOT compilation and publishes it to GitHub Pages. Under the repository's Settings → Pages, set the source to "GitHub Actions".

To build it yourself, run `dotnet publish web -c Release -o out` and serve `out/wwwroot` from any static file host.

## After a game update

The site ships copies of the game's engine (`lib/TouhouChess.Core.dll`), card text and images (`web/wwwroot/game/`). With the game installed through Steam, refresh them with:

```sh
dotnet run -- export
```

## Credits

[Touhou Chess](https://store.steampowered.com/app/5482350/) is developed by Grandmaster Cirno and published by 月影工房 and [FIVE DUCK GAMES](https://fiveduckgames.com/). The game's engine, card text and artwork in this repository belong to them. Please [get the game on Steam](https://store.steampowered.com/app/5482350/).

Touhou Chess is an unofficial fan work based on Touhou Project by Team Shanghai Alice. This analyzer is an unofficial fan-made tool and is not made or endorsed by the game's developer or publishers.
