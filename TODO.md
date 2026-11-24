# Unit Fighting: status and what's left

Last updated 2026-09-25. See README.md for how to build, run and play.

## Session 3 (2026-09-26): wave 2 finished and merged

All four session-2 requests are in this folder now. The code was finished on separate branches and merged in the
shadow repo `~/projects/unit-fighting-wip/uf-shadow`. That repo is the only git history so far: branch `base` is
this folder before wave 2, `setup`/`fight`/`view`/`lines` are the strands, and `integration` is what was copied
here.

- **Setup screen:** pick battalions per unit type for each side, men per battalion (10–60), seed, and
  "AI plays both sides". F5 and the end screen return to it. More than six battalions form a second line, and
  each second-line battalion becomes the reserve of the one in front.
- **Real-looking fighting:**
  - Between real blows, men probe, feint, shield-bash and shift their feet. A front-rank man in contact makes
    about 34 moves a minute, and the median gap between them is 0.55 s. Killing blows stay rare.
  - The view has a distinct motion for each blow kind, feints and flinches, shoves, planted-foot footwork, a
    guard that never freezes, and tiredness you can see.
  - Men in contact are "nearly still" in 2.5% of frames (was 39%).
- **Energy:**
  - After 2–3 minutes at the front an average man is at about 30% stamina. He recovers to about 90% within
    60 s at rest. Armour adds 30% to the cost.
  - Spent or badly hurt front-rank men swap with the man behind (`Battalion.ReliefStamina`/`ReliefHp`).
  - An equal line fight now lasts about 2.4 minutes (was 77 s). **Not yet confirmed with the user**; the knobs
    are listed below.
- **Reserves:**
  - A reserve keeps station 12 m behind the unit it backs. It relieves that unit automatically when the unit
    is worn (`Battle.ReliefWorn`, which uses `Battalion.Worn`) or its morale is sagging, or on the Relieve
    button.
  - The reserve closes up, steps into the line between the front's men, and the front backs out through the
    reserve's files and becomes the new reserve.
  - UI: right-click a friendly battalion, card badges, a Relieve button and a dashed ground line.
  - BattleAI uses its own second line as reserves.
- **Hold vs attack:** the holder now wins 48% over 300 seeds (42% in `all`'s 60 seeds; was 18%). Engaged attackers square up to the face they're fighting
  (`Battalion.SquareTo`) instead of wheeling onto the holder's flank.
- **Other fixes:**
  - Rear ranks now run in with a charge, so charges no longer land thin.
  - Line relief kept working after the first death.
  - Rear-rank men now recover their wind.
  - Exhausted chases now end (`LetHimGo`).
  - Duelling shield-men keep at least 0.88 m apart (`SoldierAI.ShieldGap`).
  - The card row wraps.
- **Checks:** selftest 104/104, `tools/simbench.sh all` has 0 sanity failures, and view perf is unchanged.

**Tuning knobs:**

| What it controls | Knobs |
|---|---|
| Fight length | `Combat.Lethality`, `Battalion.ReliefStamina`/`ReliefHp`, `Combat.HurtInLine`, `SoldierAI.LineStep` |
| Fatigue | `Combat.Exertion` (0.5), `Combat.ArmourLoad` (1 + 0.3 × weight), `SoldierAI.ContactRegen`/`RestRegen`, `BreatherBelow`, `RestedAt` |
| Skill spread | `Combat.SkillEdge` |
| Unit reserve trigger | `Battle.ReliefWorn` (0.3) |

**New tooling:**
- `tools/simbench.sh diag-fight`: front-rank rhythm, fatigue curve, rotations and who dies.
- `relief` bench rows: both sides with reserves, and a custom 8v8.
- `tools/view_shot.sh` options: `--scenario=gallery` (every blow kind, scripted), `--track`, `--find=shove|feint`
  and a "quiet spells" line in `--motionstats`.
- To reproduce a relief shot: `--seed=2 --rome=Legionaries:2 --gauls=Warband:1 --reserve=1:0 --time=105.6`.

## Where it stands

It's a playable slice on an open field. The setup screen picks the armies; the default is 4 Roman battalions against 5 Gallic ones, 20 men each.

- **Sim (`src/Sim`, plain C# with no nodes, deterministic per seed).** Every soldier has his own Skill, Strength, Agility, Endurance and Nerve.
  - Men pick opponents and fight real duels: windup, strike, then shield block, weapon parry or hit.
    - Blows come in five kinds: cut, overhead, backhand, thrust and low.
    - Between blows men probe, feint, shield-bash and shift their feet.
  - Defence depends on:
    - the angle of the blow: the shield covers the front and left, nothing covers the back;
    - what the defender is doing at that instant;
    - relative skill;
    - fatigue;
    - how many men are pressing him.
  - Hits roll location and armour. Heavy hits stagger, charges can knock men down, and spears reach past the front rank.
  - Men in a tight line get a shield-wall bonus.
  - Formation slots refill from the rear ranks.
  - Fatigue:
    - stamina drains with every action, and armour makes each action cost more;
    - tired men fight worse;
    - spent front-rank men swap with the man behind and recover in the rear ranks.
  - Reserves: a battalion can be the reserve of another. It relieves the front unit when that unit is worn out, by passing through its files.
  - Morale is per battalion, but men break one at a time by their own Nerve.
  - A simple AI general runs the Gauls.
- **View (`src/View`).** Low-poly 3D, drawn through shared MultiMeshes (about 170 draw calls for 180 men).
  - Procedural animation comes straight from sim state: a guard that never freezes, a distinct motion for each blow kind, feints and flinches, shield bashes and shoves, footwork, blocks, parries, staggers, knockdowns, deaths and visible tiredness.
  - Sparks, blood, ground decals, battalion banners, selection rings and order previews.
  - An RTS camera.
- **Game (`src/Game`).** A pre-battle setup screen, then Total War–style controls:
  - select, box-select, move, drag a line to set width and facing, attack, run;
  - formations 1–7, halt, pause, slow motion down to 0.1×, autopilot;
  - reserves: right-click a friendly battalion, and a Relieve button.
  - HUD: army strength, unit cards, orders panel, a live inspector for any single soldier, end screen with the battle's three heroes.
- **Tooling.**
  - `tools/shot.sh out.png [args]`: windowed screenshot of the real game. Args: `--seed --auto --time --shot-view=player|overview|melee --select --inspect`.
  - `tools/view_shot.sh`: the visuals harness.
    - Small test fights: `--scenario=duel|line|spears|chosen|big|charge|gallery`.
    - `--find=death|down|impact|press|shove|feint` replays to just before the next event of that kind.
    - Diagnostics: `--motionstats` and `--perf`.
  - `tools/simbench.sh [duel|lines|flank|matchups|default|perf|charges|relief|tune|diag-fight|all]`: headless balance bench. It exits 1 on any sanity failure.
  - `--selftest`: drives the controls with synthetic input and runs 104 checks. On macOS launch it through `open -W` the way `tools/shot.sh` does.
  - This folder has no git commits yet. The history is in the shadow repo (see the session 3 section above).

## Next up (most important first)

1. **Confirm the fight length with the user.**
   - Equal line fights now last about 2.4 minutes (77 s before wave 2), so that fatigue, rotation and reserves
     matter.
   - To shorten them, start with `Combat.Lethality` and `Combat.Exertion`.
2. **Holding against attacking spears is broken.**
   - Any line on Hold loses every time to spearmen who attack it:
     - Legionaries holding against attacking Spear Levy: 0%;
     - Legionaries holding against attacking Aux Spearmen: 0%;
     - a Warband holding against attacking Aux Spearmen: 0%.
   - When both sides attack, the result flips: Legionaries beat Spear Levy 100%.
   - The round-1 diagnosis was that swordsmen step inside spear reach and break their own shield wall.
   - This matters more now that holding works and reserves stand still.
   - Check with `tools/simbench.sh matchups`.
3. **Attacking with a reserve does worse than with a plain second line** (31% against 49%).
   - The fresh reserve breaks the enemy front, chases it in loose order, and is caught by the enemy's fresh
     second line.
   - Fix it together with pursuit discipline in item 4.
4. **Rout and pursuit** (`~/projects/unit-fighting-wip/uf-rout-plan.md`).
   - Its headline target is already met: at the end of a line fight, 72% of the best skill quarter are
     alive against 50% of the worst (target: at least 12 points apart), because fatigue made the fighting
     itself favour good men.
   - What's left is worth doing:
     - flight speed from Agility, stamina and wounds;
     - a veteran who glances back and parries while fleeing;
     - pursuers who give up or re-form instead of running into fresh enemies.
   - The plan predates the fatigue model, so re-check it before building.
5. **UI still to build:**
   - wire in the per-soldier combat log (`src/Game/CombatLog.cs` exists but isn't connected);
   - a minimap;
   - order arrows.
6. **Small sim fixes:**
   - a victory is never taken back after a rally;
   - R does nothing while holding;
   - a `Skip` hook on `BattleAI`: `PlayerAutopilot` currently hides the player's battalions from it instead.
7. **Kit imbalance.**
   - In a duel between identical men, Legionary kit beats Warband kit 76% and Axe kit 74%. The Gauls'
     extra battalion is what balances it.
   - Decide whether that's intended.
8. **Duel skill curve** is at the top of its band: a 0.9 man beats a 0.5 man 96% of the time. Consider
   flattening it slightly so upsets happen.
9. **Target churn and clumping.**
   - Supporting-rank men re-pick targets often.
   - Gallic warbands bunch into dense knots that are hard to read close up.

## Balance snapshot (session 3, from `tools/simbench.sh all` in this folder unless noted)

| Metric | Now | Target |
|---|---|---|
| Equal 1v1 duel: median length | 23.0 s | 6–25 s |
| Duel, skill 0.7 vs 0.5 | 82% | 70–85% |
| 20v20 line fight: median melee length | 144 s | 2–5 min (pending the user's OK) |
| Average front-rank man's stamina after 2–3 min of contact | about 0.29–0.32 | 0.30–0.40 |
| Front-rank gaps between actions longer than 1.5 s | 8% | rare |
| Loser's losses taken while routing | 44% | a large share |
| Rear / flank / frontal reinforcement: time until the attacked battalion routs | 7.5 / 18 / 52 s | rear 5–20 s, frontal slowest |
| Equal lines: holder wins | 42% (60 seeds); 48% (300 seeds) | 45–60% |
| Legionaries holding vs an attacking Warband | 75% | clearly favoured |
| Any line holding vs attacking spearmen | 0% | Next up #2 |
| Best vs worst skill quarter alive at the end | 72% vs 50% | at least 12 points apart |
| Default battle, AI on both sides: Rome wins | 58% (60 seeds); 61% (240 seeds) | 35–65% |
| Reserve reliefs per battle (A holds, with reserve) | 2.3 | when the front is worn |
| Sim speed, default battle | 13,400 steps/s (56× what 4× speed needs) | |
| Sanity failures | 0 | 0 |

## Known small issues

- `Battle.CheckVictory` declares the winner as soon as every battalion on a side is routing or dead. It never takes that back, even if a routing battalion later rallies.
- R (run) does nothing while a battalion is holding (`Battalion.SetRunning` only affects moving units), so you can't set running ahead of time.
- A knocked-down body lies 1.3–1.5 m back but ranks are 1.2 m apart, so the man behind can stand in it.
- Downed men can still block with their shield.
- Blades can pass through shields at contact, and a spear thrust can reach past its target.
- Close-ups of a line fight are crowded. It reads best from the mid-distance camera.
- With 12 battalions at 1280×720 the start view is fairly zoomed out.
- The setup screen doesn't say that more than six battalions form a second line of reserves.
- View cost is about 7% higher than before wave 2 (most of it from planted feet).
- Formation keys 1–7 clash with the usual RTS control groups (Ctrl+1–9) if those are ever added.
- `Battle.Events` is trimmed to 4000 on every step if nothing drains it. The view drains it every frame and fast-forward clears it, but any new headless caller should set `RecordEvents = false`.

## Not built yet (the bigger roadmap)

- **Missiles.** Pila volleys before contact (the Roman signature), archers, slingers, javelins. Shields facing the volley should matter just as they do in melee.
- **Cavalry.** Charges, horses, the rear-charge threat, and spears bracing against horses.
- **Terrain.** Hills (uphill/downhill advantage), forest and rough ground (breaks formation), and line of sight.
- **Pre-battle deployment**: placing your battalions before the fight. Choosing armies is done: the setup screen.
- **Unit abilities and stances.** Testudo, a tighter shield-wall stance, "hold formation" vs "engage at will", and guard mode.
- **Generals and leadership.** A general's unit and a morale aura; rallying routers by riding near them.
- **Smarter AI.** Holding lines, reserves, deliberate flanking manoeuvres, reacting to its own flank being turned, and pulling back wavering units.
- **Persistence.** Soldiers gain experience and carry it between battles; named veterans and a campaign layer. The individual-soldier model is built for this.
- **Replays.** The sim is deterministic per seed, so recording the orders is enough to replay a battle.
- **Sound.** Clash of steel on shields, hits, shouts, horns for routs.
- **UI extras.** Minimap, control groups, a per-soldier combat log in the inspector, order arrows.
- **Scale.** The view was measured at 720 men (about 5.5 ms per frame to update, 58–75 fps in the Debug build) and the sim at about 20k steps/s for the default battle. Profile and optimise before going well past 1,000 men.
