# Building and publishing PinDesk

Every feature or fix is developed on a branch, described in a commit, and merged through a pull request. The `Build and test` workflow runs on pull requests, pushes to `main`, and manual dispatch. It has read-only repository permissions and does not persist Git credentials in the checkout. The package upload steps run only after their platform's build and tests succeed.

## Local packaging

Windows requires the .NET 10 SDK on Windows. From the repository root:

```powershell
dotnet run --project tests/PinDesk.Core.Tests/PinDesk.Core.Tests.csproj --configuration Release
dotnet run --project tests/PinDesk.Windows.Integration/PinDesk.Windows.Integration.csproj --configuration Release
./scripts/package-windows.ps1 -Version 0.1.0-alpha.1
```

The archive is `dist/PinDesk-windows-win-x64-0.1.0-alpha.1.zip`; extract every file and run `PinDesk.Windows.exe`. It includes its .NET runtime. The script refuses to overwrite a package with the same filename. Build staging folders under `artifacts/` remain available for inspection and are excluded from source control.

macOS requires Xcode command-line tools with the matching SDK, and macOS 15.2 or later for the app:

```bash
cd apps/macos
swift build --configuration release
swift test --parallel
cd ../..
bash scripts/package-macos.sh 0.1.0-alpha.1
```

The resulting `PinDesk-macos-*.zip` contains a `PinDesk.app` bundle. The build architecture is recorded in the workflow log. CI initially targets the `macos-15` runner; inspect the archive's architecture before using it on an Intel Mac.

## GitHub release process

1. Merge the feature/fix PR only after its required checks succeed. Review the real-device evidence in `specs/acceptance.md` and resolve material failures.
2. Create and push an annotated version tag on an approved commit already merged into `main`, for example `v0.1.0-alpha.1`. Tags are intentional publishing inputs; ordinary commits do not publish releases.
3. Open Actions → **Publish release** → **Run workflow**. Select the `main` branch and enter the existing tag. Leave **prerelease** enabled for preview builds.
4. The workflow checks that the tag resolves to a commit reachable from `origin/main`, then builds and tests both platforms from that exact commit. A failed platform prevents publication of both packages.
5. The final job verifies the tag again, downloads only this run's release artifacts, generates and verifies `SHA256SUMS`, and publishes both ZIPs and the checksum file. It refuses to overwrite an existing release.

Only the final publication job receives `contents: write`. It does not run application source or package scripts. Build jobs retain `contents: read`. No custom third-party action receives a release token. Releases use the GitHub CLI already installed on hosted runners.

On Windows, verify a downloaded file with `Get-FileHash -Algorithm SHA256 <zip>`. On macOS/Linux, place `SHA256SUMS` alongside both ZIPs and run `shasum -a 256 -c SHA256SUMS`.

## Signing and permission limits

Windows ZIPs are not Authenticode signed. The macOS `.app` is unsigned (or only locally/ad-hoc signed where required by the build machine) and is not notarized; no Developer ID identity or notarization credentials are configured. Gatekeeper or SmartScreen may block these preview packages. Distribution signing is a separate release milestone and must be verified before claiming a signed or notarized release.

The macOS application uses the system window picker to authorize the selected content for a capture session. It does not require a blanket full-screen capture grant at startup. Returning to a source window asks for Accessibility permission when needed; denial still permits viewing the reference. CI does not grant those permissions or establish that all third-party windows can be captured. See the explicit manual checklist before promoting a prerelease to stable.

The workflows use stable major releases from the official GitHub `actions` organization. They were checked against the official repositories during setup: [checkout](https://github.com/actions/checkout), [setup-dotnet](https://github.com/actions/setup-dotnet), [upload-artifact](https://github.com/actions/upload-artifact), and [download-artifact](https://github.com/actions/download-artifact).
