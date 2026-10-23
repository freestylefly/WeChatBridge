# App logos

Official 1024 x 1024 App Store artwork, ordered for the forwarding flow:

1. `01-doubao.png` - [豆包](https://apps.apple.com/cn/app/id6683305962)
2. `02-qwen.png` - [千问](https://apps.apple.com/cn/app/id6752689384)
3. `03-claude.png` - [Claude](https://apps.apple.com/us/app/id6473753684)
4. `04-chatgpt.png` - [ChatGPT](https://apps.apple.com/za/app/chatgpt/id6448311069)
5. `05-obsidian.png` - [Obsidian](https://apps.apple.com/cn/app/id1557175442)
6. `06-workbuddy.png` - [WorkBuddy](https://apps.apple.com/cn/app/id6761374913)
7. `07-wesight.png` - WeSight

Doubao, Qwen, Obsidian, and WorkBuddy use the China App Store listing.
Claude and ChatGPT have no China App Store listing, so their official
international artwork is used.

`macos/Scripts/make-app.sh` turns these into one `.icns` per matching Share
Extension. The settings pane uses the same files when the destination app is
not installed. On Windows they are published to `Resources/AppLogos` and used
by the Entries pane and target pickers.
