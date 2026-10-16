# PinDesk project instructions

- Windows implements native topmost windows; macOS implements live reference overlays. Preserve this distinction in behavior and documentation.
- Implement changes in focused branches. Commit each coherent feature or fix with a detailed message explaining the behavior, reason, validation and material limitations.
- Push the branch, open a pull request and inspect checks for its current head. Merge only after applicable checks pass and substantive review findings are resolved. Keep detailed commit history.
- The repository enables automatic deletion of merged PR source branches. After merge, fetch/prune and remove the corresponding merged local branch with `git branch -d`; retain main and release tags.
- Attach every PR created for the task to the chat when the host supports PR artifacts.
- Add meaningful behavioral or native integration coverage for ownership, recovery, focus, process identity and asynchronous lifecycle changes. Never replace failed native checks with silent skips.
- Use an appropriate versioned prerelease until real-device acceptance is recorded. Publish artifacts only from reviewed commits on main; retain checksums and clearly state signing and testing limits.
- Never commit credentials, signing certificates, captured screen content, private window-title logs, local SDKs, package caches or build outputs.
- Screen content stays in memory on the local machine. Request macOS permissions only for the selected capture or explicit source-window operation.
- Do not use private macOS window-server APIs, disable SIP, inject into other apps or forward user input to a mirror.

See CONTRIBUTING.md, specs/acceptance.md and docs/release.md for the implementation and release workflow.
