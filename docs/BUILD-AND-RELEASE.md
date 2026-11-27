# Build or publish a release

Choose what you need:

- **Just make an executable** to test locally: follow [Build the app](#1-build-the-app).
- **Publish an update** for GitHub and staff laptops: follow [Publish a release](#2-publish-a-release). This also
  builds and tests the app; you do not need to build it separately first.

Run commands in PowerShell from the repository folder. In VS Code, use **Terminal > New Terminal** to open one there.

## 1. Build the app

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Wait for `BUILD OK`. The finished executable is:

```text
dist\Laptap Battery Logger.exe
```

The script runs the tests and checks the executable, logo, and bundled video. The executable is a local build; it
does **not** publish a GitHub release or update staff laptops. A normal build needs the two public update keys in
`src\UpdateKeys.cs`.

## 2. Publish a release

### Before you start

1. The release command checks for uncommitted changes and lists them. It asks before staging and committing all listed
   files; review the list and answer **Y** only if you want all of them included in the release.
2. Make sure the owner's signing key is available on this Windows account. If this is the first release on this PC,
   follow the key setup in [HOW-TO-RELEASE.md](./HOW-TO-RELEASE.md).
3. The release command automatically fetches tags and increments the patch number from the latest version. Use it for
   normal patch releases.

### Create the release files

Run this one command. It builds and tests the app, creates the next patch release, and asks whether to push it:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\release.ps1
```

Leave PowerShell open and follow its prompts. After any confirmed changes are committed, the script runs the tests
and build, checks that the new app starts, signs the update files, updates `VERSION` to the selected version,
automatically increments the internal `release=` number, then creates a Git commit and matching `v` tag.

When asked `Push to GitHub now? (Y/N)`, type `Y` and press Enter. The script pushes the commit and tag, then opens
the release folder for the version it selected:

```text
release\v<your-version>
```

It contains these three files:

```text
Laptap-Battery-Logger.exe
update.json
update.json.sig
```

### Upload to GitHub

1. Open the repository on GitHub and select **Releases** > **Draft a new release**.
2. Choose the tag shown by the release script (it starts with `v`) and use the same tag as the release title.
3. Add all three files from the opened release folder. Do not rename them.
4. Publish the release.

The release is now available to staff laptops for self-update. If you answered `N` at the push prompt, the commit
and tag are still only on this PC; push them before creating the GitHub release.

For a major or minor release, choose that version deliberately instead of using the automatic patch increment:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\release.ps1 X.Y.Z
```

Replace `X.Y.Z` with the new version (without a `v` prefix).

## Bundled video (only when replacing it)

The existing `assets\video\loop.webm` is ready to embed. If you replace the video, install FFmpeg so both `ffmpeg`
and `ffprobe` work in PowerShell, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\prepare-video.ps1 -Source "C:\path\to\your-video.mp4"
```

The script writes `assets\video\loop.webm`, keeps the full video runtime (within one second of the source), preserves
stereo audio as Opus, and checks that the video is at most 18,000,000 bytes. Run the normal build or release command
afterward; it checks that the executable is at most 19,000,000 bytes. The updater accepts executables up to 20 MiB.
Install this larger release manually over versions whose updater still has the old 5 MiB download limit; subsequent
updates can use the new 20 MiB limit. Do not commit the original large video unless it is needed separately.
