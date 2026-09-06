---
name: review-safety
description: Read-only safety review of a diff for source writes, path traversal, secret leakage, and destructive operations
allowed-tools:
  - read
  - grep
  - glob
  - exec
permissions:
  allow:
    - Exec(git diff)
    - Exec(git status)
    - Exec(git log)
    - Exec(git show)
triggers:
  - user
  - model
---

Run a read-only safety review of the current diff.

Execute: `pwsh ./scripts/Review-Safety.ps1`

If the script is unavailable, perform a manual review using `git diff` and check for:

1. **Source writes** — Any API or code path that modifies, moves, renames, deletes, or extracts into source library directories. Release one is strictly read-only.
2. **Path traversal** — Any user-supplied path that is not validated by `ReadOnlyLibraryFileSystem` or `LibraryPathResolver`. Check for `..` components, absolute/UNC paths, symlinks, and reparse points.
3. **ACL bypass** — Any code that alters filesystem ACLs or opens source files for writing.
4. **Secret/log leakage** — Any log statement that includes absolute paths, passwords, tokens, cookies, archive entry names, or page bytes.
5. **Authorization-before-cache** — Any cached cover/page/stream that is served without checking the user's library grant.
6. **Destructive operations** — `File.Delete`, `Directory.Delete`, `File.Move` on source media, or any media mutation endpoint. Explicitly flag any media deletion/move API.
7. **Test gaps** — Any destructive operation without a corresponding test using isolated generated fixtures.

Report findings by category and severity. Do NOT modify any files. This is a read-only review.
