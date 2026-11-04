# On-demand local AI setup

Super Dev is distributed without Ollama and without model weights. Manual templates and hand-built checklists work with no AI components installed.

**Generate** checks the computer, asks before any runtime or model download, then installs only what is missing. The checklist request still goes to `http://127.0.0.1:11434`. Super Dev does not call a cloud model API.

The required model is `qwen3.5:0.8b`.

## What is detected

These checks are independent. A responding API is not treated as proof that Ollama is installed.

1. An Ollama app or executable is on disk.
2. That executable can be found.
3. An Ollama process is running.
4. The local API answers.
5. `qwen3.5:0.8b` is in the local model list.
6. The model can be described. During setup, Super Dev also asks the local API to load it with a one-token generation.

A later launch repeats the checks. A saved note under the app data folder only remembers a path that was found. If the file is gone, Super Dev offers setup again.

Nothing is downloaded before **Download and Setup AI**. The size line in the confirmation window comes from the GitHub release listing, not from the installer file.

## macOS runtime

Official source: the `Ollama-darwin.zip` asset on the latest [Ollama GitHub release](https://github.com/ollama/ollama/releases), which is the archive the Ollama macOS install script uses.

1. Read `sha256sum.txt` from that same release.
2. Download the zip only when the checksum line exists and the URL is HTTPS on `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`, `github-releases.githubusercontent.com`, or `ollama.com`.
3. Compare the file to the published SHA-256. A mismatch stops setup.
4. Unpack with `/usr/bin/ditto -x -k`, then copy the app with `ditto` so permissions and symlinks stay intact.
5. The destination is `/Applications` when that folder is writable, otherwise `~/Applications`. An existing app is left in place. If the system folder rejects the copy, Super Dev retries in the user Applications folder.
6. Start it with `open -a Ollama.app --args hidden`.

Super Dev does not remove the quarantine attribute, does not run `spctl`, and does not ask for a root password. If Gatekeeper asks the user to approve the app, the setup window explains that and **Try again** continues after the app is allowed.

## Windows runtime

Official source: the same GitHub release.

- 64-bit Windows: `OllamaSetup.exe`. Super Dev starts it with the normal Windows shell so User Account Control and the installer window stay visible. Setup continues when that window closes, then checks that `ollama.exe` is actually present. The installer is not launched with a silent elevation bypass.
- Windows on ARM: `ollama-windows-arm64.zip`, unpacked into `%LocalAppData%\Programs\Ollama` after the SHA-256 check. Archive entries that would write outside that folder are rejected.

The service is started with `ollama.exe serve` only when no Ollama process is already running and the local API is down.

## Model

The model is not in the Super Dev installer. After the local API answers, Super Dev calls `POST /api/pull` with `stream: true` for `qwen3.5:0.8b`. Progress uses the byte counts in the streamed JSON. Ollama verifies the blobs and can resume an interrupted pull. A model that is already listed is not pulled again.

## Licenses

Super Dev does not redistribute the Ollama application or the model weights. The runtime is the official build from the Ollama project. The weights are fetched by that local runtime from the Ollama model library. Those projects' licenses still apply to the files stored on the computer.

## If setup fails

The setup window stays usable. **Try again** repeats the missing steps. **Close** leaves manual checklists available. Typical recoveries:

| Problem | Recovery |
| --- | --- |
| No network, or the download stops | Check the connection and try again. A finished runtime download is not repeated, and Ollama can resume a model pull. |
| Not enough disk space | Free space and try again. The check includes the published runtime size plus room for the model. |
| Permission denied | Approve the system prompt and try again. |
| Installer did not leave Ollama on disk | Try the setup again. |
| The service does not answer | Approve Ollama if the system asks, then try again. A second Ollama process is not started while one is already running. |
| The model does not load | Try the setup again. |
