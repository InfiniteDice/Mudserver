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