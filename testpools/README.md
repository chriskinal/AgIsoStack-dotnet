# Test pools

Object pools for testing the terminal's drawing and input. `bench/pools.sh` draws every screen
of every pool here, says which changed since the last run, and prints the address of one page
that shows them all (`painter/sheet.html`).

| File | What |
|---|---|
| `iop.py` | A small pool writer (ISO 11783-6 Annex B layouts) the generators below use |
| `drawing.py` → `drawing.iop` | The drawing code's test pool: one data mask each for shapes, text, numbers, gauges, pictures and structure, an alarm mask, soft keys (masks of 12, as many as the terminal shows by default, and of 14). Every sample is labelled with what it should look like |
| `inputs.py` → `inputs.iop` | Every kind of input field; the self-test's input run checks its ids and positions |
| `expected/` | Each of our pools' screens as draw lists (one line per mask, then a hash per picture), committed: a renderer change shows in `git diff testpools/expected` |
| `local/` | Git-ignored: other people's pools, their screens (`local/expected/`) and every run's output (`local/out/`) |

Ours are plain on purpose: test fixtures, not designs. Add a sample to `drawing.py` for anything
the renderer gets wrong, and run `python3 testpools/drawing.py`, then `bench/pools.sh`.

## Other people's pools (local only)

Anything under `local/` is picked up. On the dev Mac it holds:

- `local/pooledit/`: the twelve examples of PoolEdit (github.com/moehman/PoolEditDist, GPLv3), so
  they are not committed. Each was made in two steps: PoolEdit's "Export to Embedded XML"
  (numeric ids for every object; run without the GUI by calling `pooledit.Tools.loadDocument`
  and `Tools.exportToEmbeddedXML` from the jar), then PoolEditParser
  (github.com/moehman/PoolEditParser, GPLv3) with `-d=480 -sw=80 -sh=80 -c=256`, run from the
  PoolEditDist folder because image paths are relative to it. PoolEditParser 1.6.0 crashes on a
  yes/no attribute that is missing (built here with that check added, not committed), and writes
  UTF-8 into Latin-1 strings, so the Finnish help texts show as `tÃ¤ytettÃ¤`: the pool's fault,
  not the renderer's.
- `local/seeder.iop`: AgIsoStack++'s seeder example (from isobus-implement-sim's `pools/base.iop`).
