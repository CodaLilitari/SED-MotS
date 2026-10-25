# Applied lessons from SM Sith Lord's COG Factory

These original acceptance procedures credit SM Sith Lord's published experiments.
The cited outcomes are his project reports. We reviewed the source pages and our
SED command documentation; we did not rerun his levels or execute these procedures
in this study. This addition does not resume a held PLAY release.

## Check the player's route to the event

[P54, Sim Deck Seven](https://smsithlord.com/openjkdf2libretro/cogfactory/gen1/p54-holodeck.html)
reports switches that passed a scripted-message test but were outside the player's
Use reach. Testing normal input exposed the installation mistake. A handler
receiving a message and a player reaching it require separate evidence.

For a new switch, door or lift, keep a handler test for state transitions and
repeated events. Then start at a reachable player position and use the target
engine's normal action binding. Check input consumption, delivery to the intended
target, and the resulting state change. Do not inject the COG message as a
substitute. Include an obstructed or out-of-reach negative control. If a headless
harness cannot exercise targeting, leave it unverified; do not open a visible
game merely to complete this procedure. P54's exact reach and message behavior
are not asserted as MotS contracts.

The current authoring CLI supports these preparatory checks:

```text
sed-agent --version
sed-agent schema query
sed-agent schema bindings
sed-agent inspect candidate.jkl
sed-agent query candidate.jkl query-things.json
sed-agent bindings candidate.jkl bindings.json
sed-agent validate candidate.jkl resources.json
```

Discover installed schemas before creating the request files. `query` provides
indices and source identity; `bindings` resolves a selected placed COG using
explicit, hash-pinned archives. Neither proves Use reach.

## Give movement, lighting and sky separate verdicts

[P77, AIrena](https://smsithlord.com/openjkdf2libretro/cogfactory/gen2/p77-airena.html)
reports successful OpenJKDF2 movement/pickup checks alongside an owner rejection
of the all-3DO architecture's lighting. Its proposed revision uses structural
level geometry with modular detail and also requires a proper textured sky.

Make a small matched comparison between a structural JKL room and the candidate
model-based assembly. Use the same materials, viewpoints and target engine;
record ambient, source-light and material settings. Inspect floors, joins,
silhouettes and sky from lower and upper traversal points. A Blender render is an
authoring reference; a SED snapshot is an editor preview. Neither establishes the
game's appearance.

Use `sed-agent schema snapshot` and `sed-agent schema lighting` to prepare
offscreen diagnostics with explicit cameras and resource hashes. After geometry
changes, rerun movement and collision checks. P77 motivates a comparison, not a
universal prohibition on 3DO architecture.

## Extend dependency review beyond placed objects

[P50, Walker Yard](https://smsithlord.com/openjkdf2libretro/cogfactory/gen1/p50-walker-yard.html)
describes dependencies reached through class COGs and template inheritance.
[P52, The Dark Jedi Gauntlet](https://smsithlord.com/openjkdf2libretro/cogfactory/gen1/p52-dark-jedi-gauntlet.html)
adds effect templates reached through COG symbols and an episode test that caught
duplicate completion by asserting the next loaded world.

Our `assets` command is a bounded **direct** scan, not a recursive-closure proof.
Start with its exact entry receipts, then review the graph from placed things
and placed COGs through template parents, class/capture COGs, resource-valued
symbols, and model, puppet, KEY, SND, sound, material and effect dependencies.
Keep constructed names and conditional paths unresolved until reviewed; a
literal scan is not complete coverage.

Exercise an event-only dependency, such as a death spawn or special attack, in an
isolated profile. A synthetic missing-dependency control must fail its named
assertion. For episode completion, assert one transition and the exact successor
world. No new resolver, game run or retail qualification is claimed.
