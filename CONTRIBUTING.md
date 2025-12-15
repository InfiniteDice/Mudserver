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
- Applies to all files in this repository .
- For larger removals, add a reference line:
  //REF: PR #123 or Issue #456

Notes:
- Use __Format Document__ before committing.
- Keep changes small and include a short rationale alongside markers.


## Required Patch Output Format (MANDATORY)
To reduce friction when applying fixes to large files, every contributor and every AI assistant must follow this exact procedure when proposing code changes or patches that modify existing methods or add new methods. Failing to follow this format will cause the patch to be rejected.

1) Always output the whole changed method
- For any method that is changed, the patch must include the entire method declaration and body (from the method signature line to the closing brace that ends the method). Partial-method diffs are not acceptable.
- Rationale: large files and multi-line signatures make it error-prone to apply partial snippets.

2) Provide explicit, machine-findable replacement instructions
- For each changed method include a one-line search-start and search-end marker in plain text so the patch can be applied with two simple text searches.
- Format (required):
  - Search start keyword: "REPLACE FROM: <exact search string>"
  - Search end keyword:   "REPLACE TO:   <exact search string>"
- The search strings must be literal substrings that exist in the target file. Prefer the actual method signature for the start string and a closing brace line or a unique following comment for the end string.
- Example:
  REPLACE FROM: "public static Message TransferFromContainer(MudCore.World.World world, SqliteConnection conn, Player player, Item container, Item item, int quantity = 1)"
  REPLACE TO:   "} // end TransferFromContainer"

3) Show the full replacement block as a single code block and the target file path
- Provide the complete replacement code block and prefix the block with the exact file path.
- Example (required format):
```csharp MudServer/MudCore/Entities/Inventory.cs
<entire method here>
```
- This must be the only code block describing the replacement for that method.

4) Include short explicit apply instructions for humans
- After the code block include exact steps (3–4 lines) describing how to apply the patch in Visual Studio 2022, e.g.:
  1. Open `MudServer/MudCore/Entities/Inventory.cs` in Visual Studio.
  2. Find the line that contains the SEARCH START string you were given (use __Edit__ > __Find and Replace__ > __Quick Find__).
  3. Replace from that line through the line that matches the SEARCH END string with the provided block.
  4. Save, __Format Document__ and __Build Solution__.

5) Provide a brief test checklist
- At minimum: build the solution and run a focused manual test that exercises the changed method. Provide the specific commands to run in-game (e.g., `drop 30 copper`, `get 30 copper`).

6) When changing multiple methods, repeat steps 1–5 for each method in the same message.

7) Flags and JSON normalization recommendation
- If your change affects Flags/FlagsJson semantics ensure flags are serialized deterministically (e.g., sort the list before JsonSerializer.Serialize) and document that in the patch notes.

8) Commit message and PR guidelines
- Suggested commit message prefix: "fix(inv):" or "feat(inv):" followed by short description.
- In the PR description repeat the replacement instructions and test checklist so reviewers can quickly apply or test locally.


## Examples
- Minimal valid example (for changing `TransferFromContainer`):
  - Provide REPLACE FROM with exact signature line.
  - Provide REPLACE TO with unique trailing comment or the closing brace line.
  - Provide the full method code block (signature + body).
  - Provide apply steps and a test checklist.


## Enforcement
- Automated pre-commit checks and PR templates will verify the presence of REPLACE FROM / REPLACE TO markers and full-method blocks. Non-conforming patches will be blocked.


## Rationale
- Large files (thousands of lines) make small inline snippets ambiguous and error-prone. This policy ensures a deterministic apply path and speeds developer workflows.


## Final notes
- These rules are additive to the repo rules at the top of this document. Follow both sections when preparing patches.

- If you have suggestions for improving the exact marker strings or tooling to automate apply, open an Issue describing the proposal.