# How to release an update (owner)

Every copy of Laptap Battery Logger from v15 on updates itself from this repo's GitHub Releases. When it's opened and
online, it quietly downloads a new version in the background and switches to it the next time it's opened. It never
updates during a test. An update is only accepted if it is signed with one of your two keys.

You need: this PC (it holds your signing key), the repo folder, and GitHub signed in as **laptaptechnical**.

## Once: two-step sign-in for GitHub (5 minutes)

1. Sign in at github.com as **laptaptechnical**.
2. Click your picture (top right) → **Settings** → **Password and authentication**.
3. Under "Two-factor authentication", click **Enable two-factor authentication**.
4. Scan the QR code with an authenticator app on your phone (e.g. Microsoft Authenticator) and type the 6-digit code.
5. **Download the recovery codes** and keep them with the backup-key passphrase in the office safe.

## Once: make your signing keys (5 minutes)

1. Open PowerShell in the repo folder and paste: `powershell -ExecutionPolicy Bypass -File tools\keys.ps1 new`
2. Type a passphrase for the **backup** key (at least 12 characters) twice. Write it on paper; keep it in the office safe.
3. When it asks where to save the backup file, choose the **office-safe USB stick**.
4. Send the Manager the lines it prints. They are public keys and safe to share; they go into `src/UpdateKeys.cs`.

Your **primary** key stays on this PC, locked to your Windows user. Nobody else can sign updates.

## Each update

1. In PowerShell, run `powershell -ExecutionPolicy Bypass -File .\tools\release.ps1`.
   It automatically picks the next patch version. If there are uncommitted changes, it lists them and asks before
   staging and committing all of them. Review the list carefully; answer **Y** only if all listed files belong in
   the release. Then it builds and tests everything, signs the release, checks the signature with the new exe, saves
   the version and tags the release.
2. It asks **"Push to GitHub now? (Y/N)"**. Type **Y** and press Enter.
3. A folder opens with three files: `update.json`, `update.json.sig`, `Laptap-Battery-Logger.exe`.
4. **First release only:** tell Microsoft the exe is safe (about 10 minutes):
   1. Open https://www.microsoft.com/en-us/wdsi/filesubmission and sign in with a Microsoft account.
   2. Choose **Software developer**, then **Microsoft Defender Antivirus**.
   3. Click **Browse** and pick `Laptap-Battery-Logger.exe` from that folder.
   4. Choose **Incorrectly detected as malware/malicious** (or "not detected" if nothing was flagged yet) and write:
      "In-house battery test tool for Laptap Enterprises. Updates itself from our public GitHub releases."
   5. Click **Continue**, then **Submit**. Microsoft answers by email, usually within a few days.
5. On https://github.com/laptaptechnical/laptap-battery-logger click **Releases** → **Draft a new release**.
6. In "Choose a tag" pick the `v` version tag created by the script. Use that same tag as the title.
7. Drag the three files from the opened folder into the box at the bottom.
8. Click **Publish release**. Done.

## If something goes wrong

- A logger that can't update keeps running its old version and tries again next time.
- Hover over the version number at the top right of the logger to see why it didn't update (e.g. "Folder is read-only"
  on a locked stick, "Clock is wrong" on a laptop with a flat clock battery).
- **A stick shows a file ending in `.exe.old` and no logger:** right-click it → **Rename** → remove `.old` so it is
  `Laptap Battery Logger.exe` again. (This only happens if a stick was pulled out in the split second of switching.)
- If an antivirus flags the exe: don't turn the antivirus off. Restore the file from quarantine and tell the Manager
  which antivirus it was and what it said.

## Keys: change, loss, theft

The logger trusts two keys at a time: your **primary** (this PC) and your **backup** (office-safe USB + passphrase).

| What happened | Run | Then |
|---|---|---|
| **Planned change** (primary is fine) | `tools\keys.ps1 rotate` | Send the Manager the printed lines; they go into the next release. Release as usual: it is signed with the current key, and after it the new key takes over by itself. |
| **Primary lost** (Windows profile reset, PC died, new PC) | `tools\keys.ps1 recover` (pick the backup file, type its passphrase) | Send the Manager the printed lines. The next release is signed with the **backup** (it asks for the passphrase) and carries {backup, new primary}. Later, you may run `tools\keys.ps1 roll-backup` to make a fresh backup for the release after. |
| **Primary stolen** | `tools\keys.ps1 stolen` | As "lost", and the next release number jumps by 500 so anything the thief signed is superseded. Also change the GitHub password: a thief needs the GitHub account too to publish anything. |
| See the current public keys again | `tools\keys.ps1 public` | — |

Keys never go into the repo (`*.key` is ignored). The backup file is useless without its passphrase.
