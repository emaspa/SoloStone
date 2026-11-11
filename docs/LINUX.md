# Installing SoloStone (Linux)

> [!WARNING]
> **Alpha 0.1.0.** SoloStone is in early playtesting, so bugs and blockers can happen. Feedback is very welcome:
> if anything goes wrong, tell us on [Discord](https://discord.gg/e45QgKHmC2) or open an
> [issue](https://github.com/KeyStarr/SoloStone/issues).

SoloStone is an unofficial offline single-player mod for Hearthstone®. It requires your own legally obtained Hearthstone client. Not affiliated with, endorsed by, or sponsored by Blizzard Entertainment. Hearthstone is a trademark or registered trademark of Blizzard Entertainment, Inc.

Hearthstone has no Linux version, so the game itself runs under Wine. SoloStone's launcher is a native Linux program
that sets up the copy, checks it and starts it. Linux support is new, and we tested it on one machine. Read "Known
issues" before you start.

## What SoloStone does to your computer

- It creates its own Wine prefix in `~/.local/share/SoloStone/prefix`, separate from any Wine, Lutris, Bottles or
  Steam prefix you already have.
- It copies your Hearthstone folder into that prefix and adds its own files to the copy. Your own Hearthstone folder
  is only read.
- It starts the game in its own network namespace, which has no network interface except loopback. The copy has
  no network at all, not even blocked connections. Wine's background process, `wineserver`, runs in the same
  namespace, so nothing the game does can reach the network.
- It installs DXVK in that prefix. DXVK runs the game's Direct3D 11 graphics on Vulkan in place of Wine's own
  translation, which is slower. The Linux download ships it, so setup needs no internet.
- It adds one font, `LegacyRuntime.ttf`, to that prefix's Fonts folder. SoloStone's own on-screen text, such as the
  SoloStone label, the FPS counter and the ladder screen, uses Unity's built-in font, and Unity looks for it in that
  folder. A new Wine prefix has none, so without it that text is blank. The font is Liberation Sans under another name.
- It adds SoloStone to your applications menu.

SoloStone never talks to a Blizzard service and never logs in anywhere.

## Fair play and safety

- SoloStone runs a separate offline copy of the game. It never connects to Blizzard's servers.
- Before the game loads, the mod checks that it can see no network interface except loopback, and that a public
  address has no route. If either check fails, the game stops. SoloStone also refuses to start while another Wine
  program runs in its prefix, because that program's `wineserver` could have network.
- It doesn't change your own Hearthstone, your Battle.net app or your account, and it never logs in.
- It has no online features, so it can't affect anyone else's games.
- SoloStone is an unofficial fan project, not affiliated with or endorsed by Blizzard Entertainment. Use it at your
  own risk; it comes with no warranty (see [LICENSE](../LICENSE)).

## Before you start

You need:

1. 64-bit Linux on an x86-64 processor.
2. Wine. Tested with Wine 11.21. SoloStone uses the `wine` on your `PATH` unless you choose another one.
3. A graphics driver with Vulkan support. Run `vulkaninfo --summary`, part of your distribution's `vulkan-tools`
   package, and check that it lists your graphics card.
4. A way to make a network namespace without root. That is `unshare` from util-linux 2.38 or newer, which most
   distributions ship, or `bwrap` from bubblewrap. Your system must allow unprivileged user namespaces, which most
   distributions do. On Ubuntu 24.04 and newer, install bubblewrap, because Ubuntu's AppArmor rules allow them for
   `bwrap` and block them for `unshare`.
5. A Hearthstone folder, the one that holds `Hearthstone.exe`. It can come from Battle.net running in Wine, Lutris,
   Bottles or Steam, or be copied from a Windows PC. It must be a version SoloStone supports, listed in the
   [README](../README.md#what-to-expect).
6. About 13 GB free in your home folder for the copy.

If Battle.net runs in Wine, quit it before setup. SoloStone refuses to copy while Battle.net runs, because Battle.net
could update the game halfway through.

## Install

Download `SoloStone-linux-<version>.tar.gz` from the
[latest release](https://github.com/KeyStarr/SoloStone/releases/latest) and unpack it where it can stay:

```sh
mkdir -p ~/.local/share/SoloStone/launcher
tar -xzf SoloStone-linux-<version>.tar.gz -C ~/.local/share/SoloStone/launcher --strip-components=1
```

Then run the setup with your Hearthstone folder:

```sh
~/.local/share/SoloStone/launcher/solostone setup --source "/path/to/Hearthstone"
```

To find your Hearthstone folder, run `solostone find-clients`. It looks in the usual Wine, Lutris, Bottles and
Steam places. With exactly one result, `setup` without `--source` uses it.

Setup creates the Wine prefix, checks your client, copies it, adds the mod files and adds SoloStone to your
applications menu. The copy takes a few minutes. To run the game with a Wine other than the one on your `PATH`, add
`--wine /path/to/wine` to the setup command.

## Play

Start **SoloStone** from your applications menu. No terminal opens. A notification says the game is starting, and
the game window follows after a few seconds. If something stops the game, a dialog says why. If your client needs a
warning confirmed, a dialog asks first. The dialogs use `kdialog` on KDE and `zenity` elsewhere. The launcher's
messages also go to `launcher.log` (see "Where things are").

You can also run `solostone play` in a terminal. The launcher then writes its checks there and waits until you quit
the game. Add `--accept-warning` to confirm a client warning.

| Command | What it does |
|---|---|
| `solostone play` | Start the game. This is the default when you give no command. |
| `solostone status` | Show the installed version, your client and the prefix. |
| `solostone check-client` | Check whether your installed copy can play. |
| `solostone restore-backup --id <id>` | Restore a save backup. |
| `solostone recover` | Finish or remove a copy that an interrupted setup left behind. |
| `solostone desktop-entry` | Add SoloStone to your applications menu again, for example after moving the launcher folder. |
| `solostone uninstall` | Remove the copy. Add `--delete-saves` to delete your saves too. |

## Where things are

| What | Where |
|---|---|
| The launcher | Wherever you unpacked it, for example `~/.local/share/SoloStone/launcher` |
| The Wine prefix | `~/.local/share/SoloStone/prefix` |
| The game copy | `<prefix>/drive_c/users/<you>/AppData/Local/SoloStone/client` |
| Your saves | `<prefix>/drive_c/users/<you>/AppData/Local/SoloStone/saves` |
| The game log | `<prefix>/drive_c/users/<you>/AppData/Local/SoloStone/reports/Unity.log` |
| DXVK's logs | `Hearthstone_d3d11.log` and `Hearthstone_dxgi.log` in the same `reports` folder |
| What Wine, the game and DXVK print to the console | `wine.log` in the same `reports` folder, emptied at each start |
| The mod log | `<prefix>/drive_c/users/<you>/AppData/Local/SoloStone/client/BepInEx/LogOutput.log` |
| Launcher settings | `~/.config/solostone/linux.json` |
| The launcher's messages, when started from the menu | `~/.local/state/solostone/launcher.log` |

The saves live inside the prefix. Deleting the prefix deletes them, so copy the `saves` folder out first.

## Updating

The Linux launcher does not update itself yet. For a new version:

1. Run `solostone uninstall`. Your saves stay.
2. Unpack the new release over the launcher folder.
3. Run `solostone setup --source "/path/to/Hearthstone"` again.

## Uninstall

`solostone uninstall` removes the game copy, keeps your saves and removes the applications menu entry.
`solostone uninstall --delete-saves` removes the saves and their backups too.

To remove everything, uninstall, then delete `~/.local/share/SoloStone` and `~/.config/solostone`.

## Known issues

- Linux support is new. We tested it on one machine, CachyOS with Wine 11.21 and an NVIDIA graphics card. We have
  not tested Steam Deck or Proton.
- The launcher has no window of its own. Setup and the other commands run in a terminal. Without `kdialog` or
  `zenity`, errors from the menu entry appear only as a notification, and a client warning can't be confirmed there:
  run `solostone play --accept-warning` in a terminal instead.
- The launcher does not update itself yet. "Updating" above has the steps.
- If the game is slow, check that `vulkaninfo --summary` lists your graphics card, and that DXVK's log in the
  `reports` folder names that card. After a graphics driver update, restart the computer before you play: until
  then, Vulkan may not find the card and the game falls back to slower graphics. To test without DXVK, start
  SoloStone with `SOLOSTONE_DXVK=0 solostone play`.
- If SoloStone says a program is still running in its prefix, a Wine program from an earlier session is still open.
  Close it, or stop it with the `wineserver -k` command that the message shows.
- Logs include your Linux user name and folder paths. Check them before you share them.
