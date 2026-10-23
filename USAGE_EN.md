# WeChatBridge Usage Guide

WeChatBridge sits on top of WeChat's own “select messages → merge forward” flow. You select messages as usual, choose “Forward to other apps”, and the menu shows an extra row of destinations. Pick one, and WeChatBridge delivers the conversation archive plus your instructions to the target app, or turns it into an Obsidian note.

This document walks through each feature in the order you will actually use it.

- Installation, build, and requirements: [README_EN.md](README_EN.md)
- Full capability list and explicit non-goals: [PRODUCT_CAPABILITIES.zh-CN.md](PRODUCT_CAPABILITIES.zh-CN.md)

## One hand-off in three steps

1. Open “Settings → Entries” and switch on the destinations you need. Only enabled entries appear in the WeChat menu.
2. In WeChat, select messages and use “Merge forward” → “Forward to other apps”.
3. Pick a destination, for example “Send to Codex”. WeChatBridge activates the target app, writes the archive, and pastes the conversation with its scene prompt.

![WeChatBridge entries in the WeChat "Forward to other apps" menu](shared/Screenshots/usage-wechat-share-menu.png)

These entries come from WeChatBridge and sit in the same menu as AirDrop, Messages, and Mail.

## Destinations

Twelve entries cover mainstream agents, note-taking apps, and the clipboard. You can narrow or widen the list at any time.

![Entries settings](shared/Screenshots/usage-entries.png)

Each row shows its current state: missing apps are flagged, Obsidian reports whether a vault is selected, and the custom entry reports how many apps you added. Turning a switch off removes that entry from the WeChat menu immediately.

Supported agents:

![Supported agents](shared/Screenshots/usage-supported-agents.png)

- Send to Codex / Claude / Doubao / QwenWork / WorkBuddy / WeSight: activate the target app and paste the archive.
- Save to Obsidian: create a Markdown note and keep the original WeChat export.
- Copy to Clipboard: write the files without pasting, so you decide where they land.
- Send to Custom: add any macOS app; terminal-style apps can receive file paths only.

## Scenes: write the instructions once

A scene is a prompt plus the agents it applies to. Write it once, and each hand-off only needs a scene choice.

![Scene management](shared/Screenshots/usage-scenes.png)

A scene can specify the output format, for example “extract customer requests, commitments, and risks, then list next steps”. Each scene also lists the agents it applies to; destinations outside that list are skipped.

Groups can bind several scenes, giving every chat its own set of instructions.

![Group matching](shared/Screenshots/usage-group-scenes.png)

At forward time a picker appears so you can choose one scene, or skip the picker and forward directly.

![Choosing a scene while forwarding](shared/Screenshots/usage-scene-picker.png)

A common setup maps customer groups to “Customer review”, project groups to “Project standup”, and news groups to “Daily digest”, so you stop re-explaining the task.

## Skill Center

Skills are `SKILL.md` packages installed into supported agents. Scenes describe what you want; skills make sure the agent can actually do it.

![Skill Center](shared/Screenshots/usage-skills.png)

Take the article links that show up in group chats. A busy group shares several articles a day, and opening each one is slow.

![Article links in a group chat](shared/Screenshots/usage-skill-input.png)

Forward with the “Article extraction” skill installed, and the agent fetches the full text before summarizing, so you can read further only where it matters.

![Skill execution](shared/Screenshots/usage-skill-run.png)

![Skill result](shared/Screenshots/usage-skill-result.png)

## Saving to Obsidian

“Save to Obsidian” creates one note per chat name, records the source and the original archive, and lays the conversation out in time order.

![Obsidian note structure](shared/Screenshots/usage-obsidian-note.png)

Attachments are rendered the way they appear in WeChat, so images, files, and links keep their original shape and you can read the note without opening the original ZIP.

![Attachment rendering](shared/Screenshots/usage-obsidian-attachments.png)

## History

The History pane keeps every hand-off batch with its destination and delivery state, so you can confirm what actually arrived.

![History pane](shared/Screenshots/usage-history.png)

A batch can be sent again to another app, copied, revealed in Finder, or cleaned up. Batches are kept for 7 days by default.

## Permissions and fallback

WeChatBridge needs two system permissions, both used only for hand-offs you start:

1. Accessibility: activate the target app and perform the paste.
2. Screen Recording: read the chat name from WeChat's title bar to match group scenes. Images are processed in memory.

When a permission is missing, the target app is not installed, or the paste fails, the files stay on the clipboard. You can also use “Copy to Clipboard” and paste manually.

## Security boundary

| Dimension | Database-decrypting tools | WeChatBridge |
| --- | --- | --- |
| Approach | Reverse engineering and memory injection: hook WeChat, read the SQLCipher key, decrypt local `.db` files | Reuse WeChat's own select, package, and forward flow; deliver between apps with Accessibility and the clipboard |
| Account risk | Injecting into the process trips risk-control heuristics and can get an account limited or banned | Indistinguishable from a manual forward, so no risk-control exposure |
| Data scope | Once the key is extracted, every contact and group chat can be scanned silently | Only the messages you select in the UI, processed locally |
| Update resilience | Depends on memory offsets, so each WeChat release can break it | Independent of WeChat internals and keeps working after WeChat updates |
| Workflow fit | Offline bulk export and archiving | Built for AI workflows, delivering prompts directly to Codex, Claude, Obsidian, and more |

Conversation content only comes from files WeChat exports on your request. WeChatBridge does not read the WeChat database, does not decrypt data, and does not inject into WeChat. Share Extensions run in the macOS sandbox without network access. See [SECURITY.md](SECURITY.md) for details.

## FAQ

**The entries are missing from the WeChat menu.**
Confirm the switch is on, then check the extension under “System Settings → General → Login Items & Extensions → Sharing”. WeChat sometimes needs a restart before the menu refreshes.

**The target app is not installed.**
The Entries pane flags it. Install the app and switch the entry back on; no need to reinstall WeChatBridge.

**The archive arrived but nothing was pasted.**
Check the Accessibility permission. With the permission missing, the files stay on the clipboard for a manual paste.

## Collect more than 100 messages

Manually select each batch in WeChat and forward it to **Collect in WeChatBridge**. Share to the same entry to append batches. **View boundaries** lets you switch between batches to find the next range manually; each batch can also be expanded in History. Finish once to choose an agent and scene, copy all original ZIP files, or save them to a folder. Resume hidden collections from History or the menu bar; drafts and retryable collections are not automatically removed.

Each batch stores an editable group or contact name. New shares try to recognize the current WeChat conversation; otherwise fill in the name before delivery. An explicit checkbox applies the name to unnamed and future batches in the collection. Delete buttons move batches to Trash; Undo restores their original files and order.
