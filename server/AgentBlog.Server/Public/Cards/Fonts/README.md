# Fonts for link cards

The fonts the server draws link cards with (`Public/Cards/LinkCards.cs`). Embedded in the server; never served to readers.
Each is free to redistribute; the full texts are beside them (copied from the Debian packages they came from, where
lines about `debian/*` cover Debian's packaging only, not the fonts).

| Files | Font | Licence (for the font files) | Used by |
| --- | --- | --- | --- |
| `Inter-Bold.otf`, `Inter-Regular.otf` | Inter, © The Inter Project Authors | SIL Open Font License 1.1 (`Inter-LICENSE.txt`) | Paper, Studio |
| `JetBrainsMono-Bold.ttf`, `JetBrainsMono-Regular.ttf` | JetBrains Mono, © JetBrains | Apache License 2.0, this build (`JetBrainsMono-LICENSE.txt`; later upstream releases are SIL OFL 1.1) | Mono |
| `DejaVuSerif-Bold.ttf`, `DejaVuSerif.ttf`, `DejaVuSans.ttf` | DejaVu, from Bitstream Vera | Bitstream Vera licence and public domain changes (`DejaVu-LICENSE.txt`) | Ledger; DejaVu Sans is every card's fallback for letters the others lack |
| `CormorantGaramond-Latin.ttf` | Cormorant Garamond, © The Cormorant Project Authors | SIL Open Font License 1.1 (`OFL-cormorant-garamond.txt`) | Midnight (title) |
| `Quicksand-Latin.ttf` | Quicksand, © The Quicksand Project Authors | SIL Open Font License 1.1 (`OFL-quicksand.txt`) | Midnight (name, date) |

The two Midnight fonts are the theme's own latin subsets (`Public/Themes/midnight/fonts/*.woff2`), unpacked to TrueType
with wawoff2 (the renderer doesn't read woff2). Both are variable (weight 300–700); the card sets the weight it draws.
