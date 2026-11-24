# Unit Fighting

A Total War: Rome–style battle prototype in Godot 4.7 (C#). A setup screen lets you choose how many battalions
of each type Rome (you) and the Gauls (AI) field, and how many men each has; the default is four Roman battalions
against five Gallic ones on an open field. You pick formations, place lines and order attacks the way you would in Total War.
The difference is underneath: every soldier is an individual with his own Skill, Strength, Agility, Endurance
and Nerve, and melee is simulated as real one-on-one duels. Click any man to watch him fight, and at the end
see who the heroes of the battle were.

## Build and run

```bash
dotnet build
/Applications/Godot_mono.app/Contents/MacOS/Godot --path .          # or open the project in Godot 4.7 .NET and press F5
```

Options go after `--`, e.g. `Godot --path . -- --seed=3 --auto --speed=0.25`:

| Flag | |
|---|---|
| `--seed=N` | battle seed |
| `--auto` | the AI commands both sides (autopilot) |
| `--time=T` | fast-forward T seconds of battle before the first frame |
| `--speed=X` | starting time scale: 0.1, 0.25, 0.5, 1, 2 or 4 |
| `--screenshot=PATH` | save a PNG after `--shot-frames=N` frames (default 12), then quit |
| `--shot-view=V` | `player` (default), `overview`, or `melee` (close on the thickest fighting) |
| `--select=I` / `--inspect` | select your battalion I / inspect a man who is fighting |
| `--help-overlay` | show the controls overlay |
| `--quit-after=N` | quit after N frames (headless smoke runs) |
| `--selftest` | drive the controls with synthetic mouse and key input and check the orders (run it windowed) |
| `--setup` | show the setup screen even when other flags are given |
| `--rome=SPEC` / `--gauls=SPEC` | start a custom battle straight away, e.g. `--rome=Legionaries:3,AuxSpearmen:2 --gauls=Warband:4,Axemen:2` |
| `--men=N` | men per battalion in a custom battle (10–60, default 20); `--rome-men=N` / `--gauls-men=N` set one side |
| `--reserve=R:F` | make your battalion R the reserve of your battalion F |

A plain launch opens the setup screen. `--seed`, `--auto`, `--time` or a custom battle skip it and go straight
into the battle. In a custom battle, a side with more than six battalions forms a second line 20 m behind the
first, and each second-line battalion becomes the reserve of the one in front of it.

`tools/shot.sh out.png [flags]` builds, runs and saves a screenshot, e.g.
`tools/shot.sh /tmp/a.png --seed=3 --auto --time=40 --shot-view=melee`.

## Controls

| | |
|---|---|
| Camera | W A S D / arrows pan, Q E rotate, wheel zoom, middle-drag orbit |
| Select | left-click a man or banner (Shift adds), drag a box, Ctrl+A all; double-click to centre the camera |
| Move | right-click the ground; double right-click to run. Several battalions move as a block |
| Line up | right-drag: the drag is the new front rank and its width sets the files. Drag left to right to face away from you, right to left to face you. Several battalions share the line |
| Attack | right-click an enemy man or banner; double right-click to charge at a run |
| Reserve | with a battalion selected, right-click a friendly battalion to make yours its reserve. Relieve (panel button) swaps them now. Any other order releases a reserve |
| Formations | 1 Line, 2 Deep, 3 Thin, 4 Column, 5 Wedge, 6 Square, 7 Loose |
| Orders | H / Backspace halt, R run or walk |
| Soldiers | click any man, friend or foe, to inspect him; F follows him with the camera |
| Time | Space pause, + / - (or . and ,) speed from 0.1× to 4× |
| Battle | P autopilot, F5 back to the setup screen, Enter the same once the battle is over, F1 help, Esc deselect |

Battalion cards (bottom left) select on click and centre the camera on double-click. They wrap into a second
row when there are many battalions and show "Reserve for …" / "Relieved by …" badges. The command panel (bottom
right) has the same orders as the keys, plus Relieve. A faint dashed line on the ground joins a reserve to the
battalion it backs.

## How combat works

The simulation in `src/Sim` is plain C# with no Godot nodes, stepped at 60 Hz. A battalion only holds an
anchor, a facing and a list of slots. Its men do all the fighting. In formation a man keeps to his slot and
fights whoever comes within a step of it. When an attack order closes, the men are released and pick their own
opponents, and the better ones go looking for men who are busy with someone else. Every blow is a duel event
(`Combat.cs`). A man winds up (committed, so his guard is weaker), strikes and recovers. Whether the blow lands
depends on the angle it comes from relative to the defender's shield arc and his field of view, on both men's
skill, stamina and current phase, and on how many blades are pressing the defender. A blow from behind can't be
blocked at all, which is why flanking works. Between real blows (cuts, overheads, backhands, thrusts, low cuts)
men probe, feint to draw the other's guard, bash with the shield to shove him back, and shift their feet, so a
man in contact is rarely idle for more than about a second.

Every action costs stamina, and armour makes it cost more. A tired man is slower, hits weaker and defends
worse. After two or three minutes at the front an average man is down to about a third. When a front-rank man
is spent or badly hurt he changes places with the fresher man behind him, and he gets his wind back in the rear
ranks within a minute or so. When a whole battalion is worn out, its reserve (if it has one) closes up behind it,
steps into the line between its men, and the tired battalion backs out through the reserve's files, still
facing the enemy, to re-form 12 m behind as the new reserve. Hits take a location and armour roll. Heavy hits and broken guards
stagger a man, and running charges can knock him flat unless ranks behind hold him up. Spearmen brace against
a charge they can see. Morale (`Battalion.cs`) falls with losses, flank and rear attacks and fatigue. Men break
one by one, faint-hearted first, and when half have gone the battalion routs.

Layout: `src/Sim` simulation, `src/View` rendering and camera, `src/Game` bootstrap, player input and HUD.
