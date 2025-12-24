# Contributing

This repository enforces two simple, strict rules to preserve context:

1) Never remove comments
- Do not delete existing comments.
- If a comment is no longer applicable, mark it in place:
  //DEPRECATED// <original comments>

2) Never remove non-associated code silently
- Do not delete code blocks outright.
- Replace removed code with a clear marker including timestamp and a short excerpt:
  //CODE REMOVED - YYYY-MM-DD HH:mm:ss // '<the code removed here>'

Examples:
- Deprecating a comment:
  //DEPRECATED// Former spawn tick log; replaced by consolidated tracer

- Removing an obsolete line/block (example timestamp):
  //CODE REMOVED - 2025-12-02 14:37:00 // 'RoomSpawner.ProcessSpawns(_world, _db);'

Scope:
- Applies to all files in this repository.

Use the following when applying fixes to large files.
Every contributor and every AI assistant must follow this exact procedure when proposing code changes or patches that modify large classes. Failing to follow this format will cause the patch to be rejected.

1) Always provide the smallest syntactic unit that fully contains all modifications: a Try/Catch block, an IF/ELSE block (including its else-if/else
branches), a single CASE block, or — when changes touch multiple such blocks — the entire METHOD (signature and body). If you change a declaration
(field/property/method signature), include the whole declaration. This ensures deterministic, machine-applicable patches.

2) Provide explicit, machine-findable replacement instructions
- For each changed 'unit' include a one-line search-start and search-end marker in plain text so the patch can be applied with two simple text searches.
- Format (required):
  - Search start keyword: "REPLACE FROM: <exact search string>"
  - Search end keyword:   "REPLACE TO:   <exact search string>"
- The search strings must be literal substrings that exist in the target file. Prefer the actual method signature for the start string and a closing brace line or a unique following comment for the end string.
- Example:
  REPLACE FROM: "public static Message TransferFromContainer(MudCore.World.World world, SqliteConnection conn, Player player, Item container, Item item, int quantity = 1)"
  REPLACE TO:   "} // end TransferFromContainer"

3) Include short explicit apply instructions for humans
- After the code block include exact steps (3–4 lines) describing how to apply the patch in Visual Studio 2022, e.g.:
 1. Open `MudServer/MudCore/Entities/Inventory.cs` in Visual Studio.
 2. Find the line that contains the SEARCH START string you were given (use __Edit__ > __Find and Replace__ > __Quick Find__).
 3. Replace from that line through the line that matches the SEARCH END string with the provided block.
 4. Save, __Format Document__ and __Build Solution__.

4) Show the full replacement block as a single code block and the target file path
- Provide the complete replacement code block
- Example (required format):
- - This must be the only code block describing the replacement for that method.

5) Provide a brief test checklist
- At minimum: build the solution and run a focused manual test that exercises the changed method. Provide the specific in-game commands to run (e.g., `drop 30 copper`, `get 30 copper`).

6) When changing multiple units of code, repeat steps 1–5 for each method in the same message.

7) Flags and JSON normalization recommendation
- If your change affects Flags/FlagsJson semantics ensure flags are serialized deterministically (for example: sort the flag list before calling `JsonSerializer.Serialize`) and document that in the patch notes.

8) Commit message and PR guidelines
- Suggested commit message prefix: `fix(inv):` or `feat(inv):` followed by a short description.
- In the PR description repeat the replacement instructions and test checklist so reviewers can quickly apply or test locally.

## Inventory changes policy (NEW)
When proposing changes that touch inventory behavior (any file or method that reads/writes or manipulates item ownership, stacking, flags, categories or container logic) you MUST also:

1) Analyze and update all relevant call sites
- InventoryManager helpers are NOT an isolated surface. Callers such as `CommandHandler.HandlePutCommand`, `HandleGetCommand`, `DropItem`, `TransferToPlayer`, `TransferToContainer`, `TransferFromContainer`, `TransferToRoom` and any admin or pending-action flows that rely on inventory must be reviewed and updated to preserve user-visible message propagation and atomicity.

2) Propagate Message results and failures
- When a helper returns a `Message` (success, rejection, or error), callers MUST propagate non-success messages immediately (do not swallow or replace them with summary success text). Log and return the original `Message` when appropriate so the player sees accurate feedback.

3) Preserve transactional boundaries and writer-queue usage
- If your change affects persistence semantics, ensure the writer queue (`DbWriteQueue`) and local transaction boundaries remain correct. Document if a change requires a broader transaction or different ordering to avoid mid-operation visibility inconsistencies.

4) Tests and manual checklist
- Add at least one focused manual test case in the PR description covering both acceptance and rejection paths. Example checks:
  - `put 10 coin_copper in cup` => expect rejection: "Container 'Cup' does not accept items of category 'Currency'."
  - `put 10 coin_copper in bag_small` => expect success and `inspect bag_small` shows coins.
  - `put all copper in bag_small` when mixed stacks exist => verify partial merges and accurate messages.

5) Deterministic Flags serialization
- If you modify code that merges or serializes `Flags`/`FlagsJson`, sort the flag list before serializing and update any callers that compare `FlagsJson` string equality to instead use normalized lists or deterministic strings.

6) Logging
- Add short debug logs at call-sites where message decisions are made (for example, log the returned Message.Text from `TransferToContainer` before summarizing). Keep logs guarded with try/catch and avoid leaking sensitive data.

7) Patch apply instructions
- When inventory-affecting changes are provided in a PR, include the exact REPLACE FROM/REPLACE TO markers and the method-level full replacement blocks as required by this file.

8) PR reviewers checklist
- Verify all related call-sites were considered and tests/manual checks pass.
- Confirm no user-facing message regressions.

9) Rationale
- Inventory flows are cross-cutting and user-visible. Small internal fixes can produce confusing UX if callers summarize or swallow returned messages. This policy makes reviewers verify cross-layer behavior before merging.

## Refactoring large loops and phase extraction (preferred pattern)

Large single-method loops (for example the main `GameLoop.Start()` while loop) should be extracted into focused helper methods when it is safe to do so. The goals are improved readability, testability, and lower risk for future changes. Follow these rules when extracting phases (Effects, Combat, Spawner, WorldTick, Persistence):

1) Extract only one phase at a time
- Make a single, focused extraction (for example `ProcessEffectsTick`) and ensure no semantic changes are introduced. Keep the public external behavior identical.

2) Preserve ordering and side-effects
- The top-level loop must retain the same phase ordering as before. Extraction must not change when or how often phases run. If ordering or atomicity changes, document and justify it in the PR.

3) Keep DB access centralized in the loop
- Helpers may compute results and mutate in-memory state (HP, flags, lists), but `Start()` should remain responsible for persistence unless you explicitly redesign DB concurrency. This avoids subtle connection/transaction changes.

4) Small return values, not implicit side-effects
- Prefer helper signatures that return lists or small result objects (e.g., `(List<Player> playersToPersist, List<Npc> npcsToPersist)`) rather than hiding large side-effects. This makes testing and reasoning easier.

5) Add focused unit/integration tests
- For each extracted helper add tests that validate the behavior (effects application, messaging decisions, returned persist lists). If exact unit test coverage is not achievable due to complex dependencies, add a small integration test or a manual test checklist in the PR.

6) Messaging rules and UX parity
- Keep user-facing messages unchanged during refactor. If you decide to change phrasing, include it in the PR notes and add the new tests/manual checks.

7) DB & concurrency safety checklist (must be followed before merging)
- Confirm the helper does not create new concurrent DB access paths or require multiple `SqliteConnection` instances.
- If you must parallelize any phase later, convert to separate connections and transactions intentionally and add a migration note.

8) Minimal public surface change
- New helpers should be private instance methods inside the same class where possible (e.g., `private void ProcessEffectsTick(out ...)`). Avoid spreading logic into new public API unless required.

9) Incremental commits
- Make the refactor in small commits: extract helper, adjust `Start()` to call helper, run tests, then change next phase.

10) Rollback / DEPRECATED markers
- If you remove lines from `Start()` replace them with a DEPRECATED or CODE REMOVED marker following repo rules so reviewers can see what was removed.

## When it is safe to extract
- The extraction is safe when:
  - There is no change to external behavior (messages, persisted fields) in the happy path.
  - The helper is fully covered by tests or a manual test checklist is included.
  - The change does not introduce new concurrent DB use or change transaction boundaries.
  - The change is reviewed and approved by a maintainer.

## Suggested PR review focus for refactors
- Verify REPLACE FROM/TO markers and full-method blocks are correct.
- Run the provided manual test checklist.
- Confirm no change in user-facing messages unless intentionally changed.
- Confirm DB queries and updates are unchanged or documented.

## Enforcement
- Automated pre-commit checks and PR templates should verify the presence of REPLACE FROM / REPLACE TO markers and full-method blocks. Non-conforming patches will be blocked.

## Rationale
- Large files (thousands of lines) make small inline snippets ambiguous and error-prone. This policy ensures a deterministic apply path and speeds developer workflows.

## Final notes
- These rules are additive to the repo rules at the top of this document. Follow both sections when preparing patches.
- If you have suggestions for improving the exact marker strings or tooling to automate apply, open an Issue describing the proposal.