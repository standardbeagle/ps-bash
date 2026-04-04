Publish a new version of PsBash to PSGallery.

Steps:
1. Run the full test suite with `pwsh -NoProfile -c "Invoke-Pester ./tests/ -Output Minimal"` — abort if any tests fail.
2. Determine the next version number:
   - Check the latest release with `gh release list --repo standardbeagle/ps-bash --limit 1`
   - If the user provided a version argument, use that. Otherwise bump the patch version (e.g., 0.3.0 → 0.3.1).
3. Generate release notes by examining commits since the last release tag:
   - `git log <last-tag>..HEAD --oneline`
   - Group by conventional commit type (fix, feat, test, docs, etc.)
   - Write concise, user-facing descriptions
4. Create the GitHub release:
   - `gh release create v<version> --repo standardbeagle/ps-bash --title "v<version>" --notes "<notes>"`
5. Verify the publish workflow started:
   - `gh run list --limit 1 --repo standardbeagle/ps-bash --workflow publish.yml`
6. Report the release URL and workflow status to the user.

Do NOT modify the ModuleVersion in PsBash.psd1 — the publish workflow patches it automatically from the release tag.
