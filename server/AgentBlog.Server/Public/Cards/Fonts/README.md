# Fonts for link cards

The fonts the server draws link cards with (`Public/Cards/LinkCards.cs`). Embedded in the server; never served to readers.
Each is free to redistribute; the full texts are beside them (copied from the Debian packages they came from, where
lines about `debian/*` cover Debian's packaging only, not the fonts).

| Files | Font | Licence (for the font files) | Used by |
| --- | --- | --- | --- |
| `Inter-Bold.otf`, `Inter-Regular.otf` | Inter, © The Inter Project Authors | SIL Open Font License 1.1 (`Inter-LICENSE.txt`) | Paper, Studio |
| `JetBrainsMono-Bold.ttf`, `JetBrainsMono-Regular.ttf` | JetBrains Mono, © JetBrains | Apache License 2.0, this build (`JetBrainsMono-LICENSE.txt`; later upstream releases are SIL OFL 1.1) | Mono |
| `DejaVuSerif-Bold.ttf`, `DejaVuSerif.ttf`, `DejaVuSans.ttf` | DejaVu, from Bitstream Vera | Bitstream Vera licence and public domain changes (`DejaVu-LICENSE.txt`) | Ledger; DejaVu Sans is every card's fallback for letters the others lack |
