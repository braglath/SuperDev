# Local AI checklist generation

Super Dev can draft a checklist with Ollama on the same computer. The request stays on localhost. Super Dev does not call a cloud model API and does not send the task description anywhere else.

Manual templates still work when local AI is not installed.

## First use

Choose **Generate**. If Ollama or `qwen3.5:0.8b` is missing, Super Dev explains the one-time local download and waits for **Download and Setup AI**. It then downloads the official runtime if needed, starts the local service, and pulls the model. No Terminal or Command Prompt commands are required.

An Ollama install that is already on the computer is reused. See [ai-setup.md](ai-setup.md) for the macOS and Windows install steps.

The app talks only to `http://127.0.0.1:11434`.

## Generate a checklist

1. On the home screen, choose **Generate**.
2. Select a platform, framework, and work type.
3. Optionally describe the task. This text is sent only to the local model.
4. Choose **Generate Checklist**.
5. Edit the preview: change titles and descriptions, remove items, or add items and sections.
6. Choose **Create Checklist** when the preview looks right.

Create opens a normal Super Dev checklist. It is not written to disk until you save it. Add the branch name and PR link from inside the checklist after it is created.

**Regenerate** replaces the whole preview. **Regenerate section** replaces one section. **Cancel** stops a request that is still running. **Back** closes the generator without creating a checklist.

Framework choices follow the platform. Web offers React, Angular, and Custom / Other. Backend / Server offers ASP.NET Core, Node.js, and Custom / Other.

## What the model returns

The generator asks Ollama for JSON with a title, a summary, and sections of checklist items. Each item has a title, description, a work type (Investigation, Implementation, Verification, or Documentation), a priority (Critical, Required, Conditional, or Review), and acceptance criteria.

Super Dev checks that JSON before showing it. A missing type or priority is filled in. A value the checklist cannot store is changed to the nearest supported value. Empty titles and duplicate items are removed. The preview states this in one sentence. A response that is not a checklist is rejected with a retry message. Generated text is never run as a command.

When you create the checklist, sections become item categories. Priority is saved as Critical, Required, Conditional, or Review. The description and acceptance criteria are saved in the item notes. The summary and item type stay in the preview. The task, platform, and framework are saved with the checklist so a later review or explanation can use them. A checklist created by hand does not gain those fields.

## Review a checklist

Open a checklist and choose **Review**. Super Dev sends the checklist that is open, including its title, work type, and the checks already on it. Platform, framework, and the original task are included only when the checklist has them.

The review lists missing work, overlap, unclear checks, test gaps, and priority problems. Security and performance findings are requested only when they fit the task. A checklist that already covers the task can come back with no findings.

Nothing on the checklist changes until you apply a suggestion. **Add item** inserts one check. **Replace item** and **Update priority** ask you to confirm, and they keep the check's id, completion, notes you did not change, evidence, and subtasks. Duplicate and observation findings are information only. **Dismiss** hides a finding for this session. **Rerun review** reads the checklist as it is now and replaces the previous findings.

## Explain a check

Each check has an AI icon. The same icon is on the check in the side panel and in the check popup. It explains that one check: what it is asking, why it is on the list, how to do it, and when it is done.

Super Dev sends that check, including its notes and subtasks, plus the checklist title and work type. Platform, framework, and the original task are included only when the checklist has them. The explanation does not change the check. Choosing the icon again for a check that is already explained brings that explanation forward. **Cancel** stops a request that is still running. The icon stays available after the checklist is completed.

## Troubleshooting

| What you see | What to do |
| --- | --- |
| Ollama does not appear to be installed | Choose **Generate** and confirm the one-time local setup. |
| Ollama is installed but not running | Choose **Generate**. Super Dev starts the local service. |
| Could not connect to localhost:11434 | Choose **Generate** again so Super Dev can start the local service. |
| The model is not installed | Choose **Generate**. Super Dev downloads `qwen3.5:0.8b` through the local API. |
| The model took too long | Use a shorter task description and generate again. |
| The response was not a valid checklist | Generate again. A small model sometimes returns incomplete JSON. |
| Generation failed because of memory | Close other apps and try again. |

Inside the generator, **Check again** repeats the connection and model check. If Ollama or the model is missing, close the generator and choose **Generate** so Super Dev can set it up.
