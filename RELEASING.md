# Preparing a GitHub release

The GitHub repository should contain the source and documentation. Do not add `Windows-x64/`, `Source/artifacts/`, the local NuGet feed, or build output to a commit. The current Windows executable is about 162 MB, above [GitHub's 100 MiB limit for ordinary repository files](https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github). GitHub [Releases can carry binary assets](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository).

1. On Windows with the .NET 8 SDK, run `Source/scripts/Build.ps1`. This builds the solution, runs the 34-check runner, and publishes a self-contained single-file executable to `Source/artifacts/win-x64/`. Use `-Runtime win-arm64` only for an ARM64 release built and checked for that target.
2. Make a fresh release ZIP containing `CodexUsage.exe`, the root `README.md` and `LICENSE`, the `Notices/` directory, and a `SHA256.txt` generated from the **same executable in the ZIP**. Do not include `CodexUsage.previous.exe`, caches, symbols, or user preferences.
3. Extract the ZIP on a separate Windows machine or folder and run the widget. Check the desktop and taskbar views, hover details, zoom, and tray recovery against [VERIFICATION.md](VERIFICATION.md).
4. Attach the ZIP to a GitHub Release. Put platform, architecture, version, prerequisite Codex CLI, and any known limitations in the release notes.

The `Windows-x64/` folder in this local workspace is an installed copy for testing. It is intentionally ignored by Git; future source checkouts should build locally or download a release asset.
