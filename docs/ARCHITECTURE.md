# FlowBoard — Architecture

## Stack (and why)

| Concern | Choice | Rationale |
|---|---|---|
| UI framework | **.NET 8 + WPF** (x64, `net8.0-windows`) | Best-in-class control over hit-testing, adorners and per-pixel drag visuals — which is what makes DnD feel good. WinUI 3 has weaker adorner/overlay support and a rougher packaging story; Avalonia is unnecessary since the target is Windows-only. WPF also gives us `Win32` window placement persistence for free. |
| Theming | **WPF-UI (lepoco)** | Real Fluent (Mica backdrop, system accent, system theme watcher) without WinUI's baggage. We override its resource dictionary to get a distinctive look rather than a template feel. |
| MVVM | **CommunityToolkit.Mvvm** | Source-generated `ObservableProperty`/`RelayCommand`; no reflection, fast startup. |
| Data | **Microsoft.Data.Sqlite** (raw SQL, no EF Core) | The object graph is small (thousands of cards), loaded once into memory at startup. EF Core's change tracker fights the undo stack and doubles startup cost. Hand-rolled DAL = explicit transactions, explicit WAL, explicit migrations. |
| Markdown | **Markdig** + a small `FlowDocument` renderer | Rendering in the card detail view only. |
| Drag & drop | **Custom** (no GongSolutions) | We need insertion placeholders, edge auto-scroll, ESC cancel, and undoable drops. Off-the-shelf DnD libs give none of those. |

## Layering

```
FlowBoard.exe
├─ Domain/      POCO + ObservableObject graph. No persistence knowledge.
├─ Data/        FlowBoardStore  — schema, migrations, load, upsert, delete
│               JsonSnapshot    — export/import (versioned)
├─ Services/    UndoRedoService — the ONLY legal path for mutations
│               Ops/*           — undoable operations
├─ ViewModels/  Workspace/Board/Card VMs, filter state, selection
└─ Views/       XAML + drag-drop behaviors + theme dictionaries
```

**Golden rule:** every user-visible mutation goes through `UndoRedoService.Execute(op)`.
An op does three things atomically: mutate the in-memory graph, write to SQLite inside a
transaction, and record enough state to revert. Nothing mutates the graph directly — that
is how we get "undo covers everything" for free instead of bolting it on later.

## Object model

**A board *is* a column.** There is no separate column entity.

```
Workspace (sidebar)
└─ Board (a lane in the horizontal row; name + accent colour)
   │      ← seeded: Now, Next, Later, Parked
   └─ Card
      ├─ Labels (app-global, many-to-many)
      ├─ ChecklistItem[]
      ├─ CardLink[]  (url | file)
      └─ Activity[]  (append-only log)
```

Consequences of the flattening, all of them simplifications:

- **The board-tab strip is gone.** "Promote Later → Next → Now" is just a drag from one
  lane to the next, i.e. an ordinary cross-lane drop. One drop mechanic instead of two.
- **Workspaces inherit the tab role.** Sidebar entries are the drop target for moving a
  card out of the current set of lanes; `MoveCardOp` handles it unchanged, because it
  only ever targeted a board id. "Move to board…" lists boards across all workspaces.
- **`Ctrl+1..9` focuses the Nth lane** rather than switching a view.
- **Column reorder = board reorder** (`MoveBoardOp`), scoped to the workspace.

## Persistence

- `%APPDATA%\FlowBoard\flowboard.db`, `journal_mode=WAL`, `synchronous=NORMAL`, `foreign_keys=ON`.
- WAL + one transaction per op ⇒ a crash mid-write rolls back cleanly; no corrupt DB.
- `meta.schema_version` drives forward-only migrations in `FlowBoardStore.Migrate()`.
- Autosave: implicit — the op *is* the save. No dirty flag, no save button.
- Export/import: whole workspace tree → single JSON with `schemaVersion`.

## Ordering

`position` is a plain `INTEGER` per parent (cards within a board, boards within a workspace), densely packed. On reorder we rewrite the
positions of the affected sibling list in one transaction (cheap at this scale) rather than
using fractional indices — no rebalancing bugs, no float drift.

## Staged delivery

| Stage | Content | Status |
|---|---|---|
| **1** | Solution, domain graph, SQLite store + WAL + migrations + seed, undo engine, JSON export/import, app shell that boots and renders seeded data | **delivered** |
| 2 | Board canvas: tabs, horizontal column scroller, card face rendering (priority edge strip, due-date states, checklist `3/7`, label chips, aging) | next |
| **3** | Drag & drop: card reorder within a lane, card across lanes, card onto a sidebar workspace, lane reorder, placeholder gaps, shift animations, edge auto-scroll, ESC cancel, undoable drops | **delivered** |
| **4** | Card detail flyout: Markdown description, labels, checklist, links, activity log | **delivered** |
| **5** | Search/filter bar (dim non-matching), keyboard model, selection movement, `Ctrl+1..9` lane focus, `?` cheat-sheet | **delivered** |
| **6** | Theming polish (light/dark/system + override), Archive view, window placement, File menu wiring | **delivered** |

## Build

Requires .NET 8 SDK and NuGet access:

```
dotnet restore && dotnet build -c Release
dotnet run --project src/FlowBoard/FlowBoard.csproj
```


## Visual design

Tokens live in `Theme/Tokens.Dark.xaml` and `Theme/Tokens.Light.xaml`, which carry an
**identical key set** by contract — a theme change is a dictionary swap and nothing else
(`Theme/ThemeTokens.cs`). Nothing in the app hard-codes a colour.

**The neutral ramp is cool ink**, not neutral grey. Board accents and label colours are
user data and can be any hue, so the chrome leans blue and sits back rather than competing.

**Signature: the priority strip encodes level by length, not just colour.**

```
 None      Low       Medium     High     Critical
  ·        ▌         ▌          ▌         ▌
           ▌         ▌          ▌         ▌
                     ▌          ▌         ▌
                                ▌         ▌
                                          ▌
  —       25%       50%        75%       100%   of the card edge
```

Colour alone is the template answer, and it fails for ~8% of men with colour-vision
deficiency. Length is readable in peripheral vision while scanning a lane of twenty cards,
which is the condition the strip actually gets used under. The ramp runs cool→hot
(dusty blue → indigo → violet → crimson) and **deliberately contains no amber**, so the
strip can never be misread as the due-soon pill.

**Type: three faces, three roles**, all shipped with Windows — nothing to license or embed.

| Role | Face | Used for |
|---|---|---|
| Display | Segoe UI Variable Display | card + lane titles (optical sizing holds at SemiBold) |
| Body | Segoe UI Variable Text | descriptions, menus, prose |
| Utility | Cascadia Mono | due dates, `3/7` counter — tabular figures don't jitter when `3/7` becomes `10/12` |

**Everything on the card face hides when empty.** A bare card is one line of title. That
restraint is what makes a loud card readable.

### Wall-clock state

Due colours and ageing depend on the clock, not on any card property — a card goes amber,
then red, with nothing raising `PropertyChanged`. `Services/Clock.cs` ticks one app-wide
`Now` every 30s and card faces multi-bind against it. The alternative is a `DispatcherTimer`
per card: hundreds of timers to answer one question.

### Why converters never return brushes

A converter that resolved a brush would capture whichever theme dictionary was loaded when
it ran, and go stale the moment the system flipped to dark at sunset. So converters emit
*state* (`DueState.Overdue`, `true` for stale) and XAML `DataTrigger`s map state to a
`DynamicResource` brush, which re-resolves on swap for free.


## Drag & drop

`Views/DragDrop/` — the heart of the app, and the most opinionated code in it.

### Why not `DragDrop.DoDragDrop`

WPF's built-in drag runs a **modal message loop**. You get ESC-cancel and cross-process
drops free, but storyboards inside that loop stutter, and driving edge auto-scroll from it
means fighting the loop for control. FlowBoard only ever drags within one window, so the
OLE machinery buys nothing and costs exactly the animation quality that is the point. We
capture the mouse ourselves.

### The one line that mutates

```
pickup → ghost → targeting → gap → drop → Undo.Execute(new MoveCardOp(...))
```

Everything before the last step is *presentation*: a bitmap, some offsets, some hit tests.
The bound collections are never touched until the drop commits. Two things fall out:

- **ESC cancel is trivial** — drop the visuals, mutate nothing. There is no half-applied
  state to unwind, because nothing was applied.
- **Undo is automatic** — a drop is just an op, same as any menu command.

### Four decisions worth keeping

**The slot cache.** Index maths asks "which gap is the pointer in?". Answering it by
querying live container positions feeds back on itself the moment we translate those same
containers to open the gap: container moves → hit test changes → gap moves → container
moves. So `DragSession.Slots` snapshots the *untranslated* layout once, in the panel's own
coordinate space (immune to scrolling), and all subsequent maths runs against that.

**The gap is transforms, not layout.** `GapAnimator` animates `TranslateTransform`, never
`Margin` or `Height`. A margin animation re-runs measure+arrange on the whole lane every
frame at 60fps; a render transform is composited on the GPU and touches no layout. This is
the only reason the gap can animate *while* the board auto-scrolls.

**Auto-scroll rides `CompositionTarget.Rendering`.** Scroll offset lands on the same beat
as the frame that draws it, so the board doesn't shear against the ghost. A `DispatcherTimer`
at any interval beats against the refresh rate. Velocity ramps quadratically with depth
into the hot zone — constant speed forces a hover-and-wait; a ramp lets the same gesture
ask for a nudge or a sprint.

**The ghost is a bitmap, not a `VisualBrush`.** We collapse the source container the instant
the drag starts, and a `VisualBrush` of a collapsed element renders nothing. Snapshotting
via `RenderTargetBitmap` (at real monitor DPI, or it's visibly soft on a scaled display)
also cuts the ghost loose from the source, so nothing happening in the lane below can
flicker the thing in the user's hand.

### Gestures

| Gesture | Result |
|---|---|
| Drag card within a lane | reorder → `MoveCardOp` |
| Drag card to another lane | move → `MoveCardOp` (this is "promote Later → Now") |
| Drag card onto a sidebar workspace | move to that workspace's first lane → `MoveCardOp` |
| Drag a lane header sideways | reorder → `MoveBoardOp` |
| ESC mid-drag | abandon, mutate nothing |
| Lost capture (alt-tab, UAC) | treated exactly like ESC |

A drop that changes nothing (`DragSession.IsNoOp`) emits no op at all — pushing a no-op
onto the undo stack means the user's next Ctrl+Z appears to do nothing, which reads as a bug.


## Stages 4–6

**Card editor (`CardEditorViewModel`)** works on a *draft*. The brief said Ctrl+Enter saves
and Esc closes — which only means anything if Esc genuinely throws work away. So nothing
touches the model until Save, which then diffs draft against card and emits a single
`CompositeOp`. A session that changed a title, a due date and three checklist items is one
Ctrl+Z. The usual alternative — edit live, undo per keystroke — makes Esc a lie.

**Markdown** is rendered by a hand-written Markdig-AST → FlowDocument pass rather than
`Markdig.Wpf`. The Markdown a kanban card actually uses is small enough that owning the
renderer is cheaper than owning the dependency. Unrecognised nodes degrade to plain text,
never vanish. Card links are user-authored strings and `ShellExecute` on an arbitrary
string launches arbitrary things, so `Launcher` allows http/https/mailto/file and existing
local paths only.

**Filtering dims, never hides.** `FilterViewModel.Matches` is bound through a converter on
each card face, not applied as an `ICollectionView`. A CollectionView filter removes
containers, which destroys the board's shape (a lane of 30 showing 2 looks like a lane of
2), invalidates the drag slot cache mid-drag, and makes "where does this card live?"
unanswerable — the exact question a search is usually asked to answer. The MultiBinding
lists every input the answer depends on, clock included, because a "due soon" filter
changes answer with time alone.

**Keyboard.** `MainWindow.OnPreviewKeyDown` guards on text-input focus first: without it,
typing "n" in the search box spawns a card. `ShortcutsWindow.All` is the single source of
truth for the cheat sheet and sits beside the handler, because a cheat sheet in its own
file drifts within two releases and then actively lies.

**Archive is the only place data dies.** Archiving is a soft delete everywhere else
precisely so that one screen is the careful one. `PurgeCardOp` goes through
`UndoRedoService.ExecuteIrreversible`, which applies and then wipes the stack — every entry
in it may reference the rows just destroyed. The prompt names the card, because "Are you
sure?" is not a question anyone can answer and "Delete 'Ship v2' forever?" is.

## Build verification status

This was developed in a Linux sandbox with **no access to nuget.org** (`403
host_not_allowed`), so `dotnet build` could not be run: the WPF reference pack and all four
packages are only distributed via NuGet, and WPF needs Windows regardless.

What *was* verified mechanically:

- **Roslyn parse of every `.cs`** against the .NET 8 BCL — zero syntax or semantic
  diagnostics beyond unresolvable WPF/package types. This found and fixed one real bug
  (`GapAnimator` declared both a `Duration Slide` field and a `Slide(...)` method).
- **XAML**: all 9 files parse as XML; all 73 resource keys referenced are defined; every
  `clr-namespace` type referenced exists in C#; every `Command="{Binding …}"` resolves to a
  real `RelayCommand`.

What is **not** verified: compilation against the real WPF and WPF-UI assemblies, BAML
generation, and all runtime behaviour. Expect the first build on Windows to surface
WPF-UI 3.0 API drift (`FluentWindow`, `ui:SymbolIcon` glyph names) before anything else.

```
dotnet restore
dotnet build -c Release
```
