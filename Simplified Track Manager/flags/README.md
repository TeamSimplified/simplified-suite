# Round country flags

`round/` holds a round flag for every country the Track Manager can take
in-game flags from but the game has no round flag for: F1 25 has round flags
only for its own tracks' countries. The app uses the game's own round flag
where there is one and these otherwise.

They come from [flag-icons](https://github.com/lipis/flag-icons) 7.6.0 by
Panayiotis Lipiridis, released under the MIT licence (`LICENSE-flag-icons.txt`).
Each is the set's square `1x1` SVG, rendered at 448×448 and cut to a 112×112
circle with transparent corners. The build scales them to the game's 28×28.

Files are named by the game's nationality key (`new_zealand.png`), not by
ISO code. To remake them, render `flags/1x1/<iso>.svg` for each country the
game lacks a round flag for, the same way.
