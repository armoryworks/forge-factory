# Forge Factory — Plan (Phase 0 skeleton)

Target audience: a **hardcore factory game user** — someone who optimizes ratios, builds
for throughput, and expects the sim to be deterministic and inspectable. Every
prioritization call below is made in that user's favor.

Status: Phase 0 skeleton. Discovery reports not yet landed; sections below are
placeholders with acceptance criteria stated up front so the reports can be
graded on arrival.

## Prioritization criteria

Foundation before features, in this strict order. A module may not start until
everything above it is standing.

1. **Engine scaffold** — a window opens, a loop ticks at a fixed rate, we can draw
   one sprite and read one input. Nothing else matters until this is true.
2. **Forge integration** — the factory build sits inside the existing forge repo
   family (forge-api, forge-db, forge-ui, forge-deploy). It must build and run the
   way the rest of forge does, not as an island. Discovery report decides how deep
   the integration goes.
3. **Sim core** — fixed-timestep tick, entity/recipe/belt model, deterministic
   given the same seed and input log. This is the thing a hardcore player is
   actually playing; the renderer is a view onto it.
4. *(only then)* feature modules — isometric render polish, UI, content, progression.

Rules of thumb applied throughout:
- **Sim is authoritative, render is a view.** No game state lives in the renderer.
- **Determinism is a foundation property, not a feature.** It cannot be retrofitted.
- **A blocker never pauses the run.** Log it in `inventory.md`, take the documented
  fallback, keep moving.
- **Vertical slice over breadth.** One belt feeding one machine, end to end, beats
  five half-built subsystems.

## Discovery reports (landing shortly)

Each report is a placeholder until its file exists. The "must answer" lines are
the acceptance bar.

### forge-backend-survey.md
_Placeholder — not yet landed._
Must answer: what exists in forge-api / forge-db / forge-deploy that the factory
can reuse; what the persistence story is; whether anything forces a language or
runtime choice on us.

### engine-choice.md
_Placeholder — not yet landed._
Must answer: which engine/runtime, and why, judged against fixed-timestep
determinism, isometric 2D rendering, and fit with whatever the backend survey
found. Must name a fallback.

### factory-math-v0.md
_Placeholder — not yet landed._
Must answer: tick rate; item/belt/machine throughput units; the v0 recipe set;
how ratios are expressed. This is the contract the sim core is built against.

### isometric-design.md
_Placeholder — not yet landed._
Must answer: tile/grid geometry, coordinate transform, draw order, and the sprite
budget. Explicitly a *view* spec — it must not introduce game state.

## Phases (total run ~300 min)

| Phase | Budget | Content | Exit condition |
|---|---|---|---|
| 0 — Skeleton | ~20 min | These docs. | plan.md + inventory.md exist. **Done.** |
| 1 — Discovery | ~40 min | The four reports above, in parallel. | All four files exist and clear their "must answer" bar. Any that don't get a blocker entry and a documented fallback rather than a re-run. |
| 2 — Engine scaffold | ~50 min | Window, fixed-timestep loop, one sprite, one input. | Loop ticks at a stable rate and is observable. |
| 3 — Forge integration | ~40 min | Builds and runs the forge way; persistence seam stubbed. | Factory builds from the forge repo root without special-case steps. |
| 4 — Sim core | ~80 min | Tick, entities, recipes, belts. Deterministic. | Same seed + same input log ⇒ same state. Verified, not assumed. |
| 5 — Isometric render | ~40 min | The view onto the sim, per isometric-design.md. | The vertical slice is visible and correct on screen. |
| 6 — Integrate & verify | ~30 min | End-to-end pass; inventory triage. | Vertical slice runs start to finish; open blockers are recorded with owners. |

Budget totals ~300 min. Phases 2–4 are the foundation and hold the majority of it
by design. If the run overruns, the cut order is 5, then 6's polish — never 4's
determinism check.

## Vertical slice (the Phase 6 definition of done)

One ore source → one belt → one machine → one output, ticking deterministically,
drawn isometrically, running inside the forge build. That is the whole target. Any
work not on this path is out of scope for this run.
