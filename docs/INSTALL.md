# Installing SoloStone (Windows)

> [!WARNING]
> **Alpha 0.1.0.** SoloStone is in early playtesting, so bugs and blockers can happen. Feedback is very welcome:
> if anything goes wrong, tell us on [Discord](https://discord.gg/e45QgKHmC2) or open an
> [issue](https://github.com/KeyStarr/SoloStone/issues).

SoloStone is an unofficial offline single-player mod for Hearthstone®. It requires your own legally obtained Hearthstone client. Not affiliated with, endorsed by, or sponsored by Blizzard Entertainment. Hearthstone is a trademark or registered trademark of Blizzard Entertainment, Inc.

It has been tested on few machines. Read "Known issues" near the end before you start.

## What SoloStone does to your PC

SoloStone makes a separate copy of the Hearthstone you already have, adds its own files to the copy, and blocks the
copy from the internet with Windows Firewall. Your own Hearthstone folder is only read. SoloStone never talks to a
Blizzard service and never logs in anywhere.

## Fair play and safety

- SoloStone runs a separate offline copy of the game. It never connects to Blizzard's servers: Windows Firewall blocks the copy, and SoloStone refuses to start if it detects a connection.
- It doesn't change your own Hearthstone, your Battle.net app or your account, and it never logs in.
- It has no online features, so it can't affect anyone else's games.
- SoloStone is an unofficial fan project, not affiliated with or endorsed by Blizzard Entertainment. Use it at your own risk; it comes with no warranty (see [LICENSE](../LICENSE)).

## Before you start

1. Windows 10 or 11, 64-bit.
2. Hearthstone installed through Battle.net.
3. About 12.5 GB free for the copy.
4. **Quit Battle.net and its update agent first.** Right-click the Battle.net icon in the tray and choose Quit.
   SoloStone refuses to install, update or run while they are open. Do the same every time before you play. If it
   still says Battle.net is running, wait a minute, or end "Agent" in Task Manager.

## Download

From the [Releases page](https://github.com/KeyStarr/SoloStone/releases), download `SoloStone-Setup-<version>.exe`.

## Run the setup

1. Double-click `SoloStone-Setup-<version>.exe`.
2. **Windows will probably show a blue "Windows protected your PC" (SmartScreen) page.** The installer is not signed
   with a paid Windows code-signing certificate, so Windows does not know who made it. Choose **More info**, then
   **Run anyway**. This is expected. Do not turn off your antivirus or SmartScreen to get past it. To check the file
   first, see "Optional: check your download" at the end.
3. The setup needs no administrator rights. It installs only for your account, into `%LOCALAPPDATA%\SoloStone\app`.
   It also works if your account runs everything as administrator (for example with UAC turned off).
4. SoloStone's own setup window opens with five steps:

| Step | What you see | Does it write anything? |
|---|---|---|
| 1. How it works | A short explanation | No |
| 2. Your Hearthstone folder | Found for you, or choose it with Browse. It shows the client version SoloStone was last tested with and the version of yours | No |
| 3. Copy to | Default `%LOCALAPPDATA%\SoloStone\client`, with free space and path checks | No |
| 4. What will happen | The plain list of what the next step does, and the "Check for updates" box | No |
| 5. Do it | Progress for each step, and a Cancel button | Only here |

Closing the window or clicking Cancel in steps 1 to 4 leaves no copy of Hearthstone behind. SoloStone itself stays
installed, and its shortcut opens setup again until a copy exists.

5. In step 5, **Windows asks for permission** (a UAC prompt for Windows' own `cmd.exe` and `netsh.exe`). Say
   Yes. It adds one outbound block rule per program in the copy. If you say No, the copy is kept and SoloStone
   cannot start; the window offers "Ask again". Windows asks again if you later remove or re-apply the rules,
   and at uninstall.

If setup stops half-way, step 5 offers **Try again**, **Remove partial copy** and **Open log**
(`%LOCALAPPDATA%\SoloStone\reports\installer.log`).

## Your Hearthstone version

| Your client | What happens |
|---|---|
| The version SoloStone was tested with | Works |
| Same major and minor version, newer or older patch | Works, no warning |
| Same major version, different minor version | A **warning**. Tick "I understand" once to continue. Card text and rules can differ from the game |
| A different major version, or a version SoloStone cannot read, or a client on the known-problem list | **Blocked**. The window says why |
| The check finds that something SoloStone needs from your client is missing | **Blocked**, even for a patch-only difference |

If your Hearthstone gets a new major version, SoloStone is blocked until a SoloStone release is tested with it.
Copies that are already installed keep working. The first time you accept a warning, your saves are backed up
first.

## What connects to GitHub

Only the launcher, only when it starts, and only if "Check for updates" is on:

1. It fetches a small release list, a signing-key file and their signatures from `raw.githubusercontent.com`.
2. Only if a newer version exists, it downloads the update from `github.com` (GitHub hands the file over from
   `objects.githubusercontent.com` or `release-assets.githubusercontent.com`).

The launcher connects to nothing else. Every request is HTTPS to those four GitHub hosts, with no cookies, no login and no tracking,
and a redirect to any other host is refused. **GitHub sees what any web visitor shows: your internet address, and
that you fetched those files.** The signature is checked before the file is read, and an update that is not newer
than yours is ignored.

All requests finish before the game starts. The game itself makes no web requests of its own and stays behind its
firewall rules. At every start the mod does try two test connections (to 1.1.1.1 and to a typical home-router address on the private network, port 443) to
prove the firewall blocks them; if either one gets through, the game quits. If GitHub cannot be reached, you keep
your current version and can still play.

A few buttons in the game (help and bug reporting) open a web page in your normal browser. That happens only when
you click one.

**The off switch:** untick "Check for updates" in setup step 4 or in the launcher window, or run

```text
SoloStone.exe settings --updates off
```

With it off, SoloStone makes **no request at all**.

## Playing and updating

The desktop and Start menu shortcut opens the launcher. It shows the client version SoloStone was tested with,
yours, the SoloStone version, update status, **Play**, **Roll back**, the update switch and **Uninstall**.

- On start it checks for an update (you can **Skip update**) and installs it. After the swap it restarts itself.
- A new release is installed and then checked: the next start must see the mod load. If it does not, the previous
  version is restored and the window says so.
- A release that would give a warning for your client asks first. A release for a different major version never
  installs by itself; the update is held and your current version keeps working.
- **Roll back** returns to the previous version, only if that version can read your current saves. Otherwise the
  window says progress since the update would be lost and offers the backup taken before the update. Your
  current saves are moved aside first, never deleted.
- Backups are in `%LOCALAPPDATA%\SoloStone\backups`. The newest three stay.

## SoloStone settings in Options

The game's **Options** has two extra buttons in its Other group: **SoloStone Options** and **Tracker Options**. Each
opens a window like the one **Sound Options** opens. **Back** or Escape returns to Options, and **Done** or a click
outside the window closes it.

**SoloStone Options** holds these. Turn Timer and NPC Deck Guess take effect from the next match; the others change at
once.

- **Turn Timer** puts the burning rope on your turns.
- **Handheld UI** makes mulligan cards and menu text larger, for small screens.
- **FPS Counter** shows the frame rate in the top-right corner.
- **NPC Deck Guess (Experimental)** lets NPCs guess your deck from the cards you have played.
- **Pace: <tier>** opens the Pace page, and its **Back** returns to this window. The button is hidden during a match.
- **Language: <name>** is covered under "Game language" below.
- **Restore Starter Decks** appears only while you have a starter deck hidden.

**Tracker Options** turns the match overlays on and off: Countdown, Deck Tracker and Attack Counter in one column, the
deck tracker's Card Art, Deck Stats and Mulligan Guide in the other. A change applies at once.

The settings are saved in `BepInEx\config\solostone.plugin.cfg` in the copy.

## Game language

The game starts in English, or in a language your copy has if it has no English files. SoloStone's own menus are
always in English, and so is the text of the cards described under "Known issues".

To switch, open **Options**, then **SoloStone Options**, and press **Language: <name>**. It offers the next language
your copy has files for; with only one, pressing it explains how to add another. The change applies the next time you
start SoloStone, and until then the button shows the new name followed by "(restart)". The choice is saved as
`Language` under `[Display]` in `BepInEx\config\solostone.plugin.cfg` in the copy. `client.config` stays as shipped,
so the launcher's file check still passes.

### Adding a language

Your copy has the languages your Hearthstone had when you ran setup, usually just one. To add another:

1. In the Battle.net app, select Hearthstone, click the gear next to **Play** and choose **Game Settings**. Set the
   text language to the one you want and let Battle.net download it.
2. Uninstall SoloStone and run the installer again, so setup makes a new copy. Don't tick "Also delete my saves".
   SoloStone's settings in Options go back to their defaults, because they are stored in the copy.
3. Start SoloStone, open **SoloStone Options** and press **Language: <name>**.

The new copy has the languages Battle.net left installed. If Battle.net removed your old language, SoloStone starts
in the new one. You can switch Battle.net back afterwards: the copy keeps the files it was made with.

## Match overlays

Three overlays show during a match:

- **Deck Tracker** lists the cards still in your deck down the right edge of the screen, under the deck's name. It
  counts only cards you have seen leave or enter your deck, so a card shuffled in face down shows as "Unknown card".
  Each row shows the card's art, cropped like the game's own deck list, unless **Card Art** is off. On by default.
- **Attack Counter** shows beside each hero how much damage that side's characters can still attack for this turn,
  or could on its next turn. Off by default.
- **Countdown** shows the time left in your turn above the End Turn button. It runs only while the Turn Timer option
  is on. On by default.

The T key hides and shows all three during a match. To turn one off, use **Tracker Options** in Options.
`DeckTrackerKey` under `[Display]` in `BepInEx\config\solostone.plugin.cfg` changes the key.

### Deck stats

Each finished match saves, in your profile, which of your cards you were offered and kept in the mulligan, your hand
after it, and which cards you drew and played, along with the opponent's class. Matches from before this version
count toward a deck's wins and losses but not toward its card win rates.

With **Deck Stats** on, the deck tracker shows the deck's record under its name, such as "12–7 · 63%   vs Mage 3–1"
for the class you're facing. Press S during a match to add three win rate columns to each card:

- **Mulligan**: games where the card was in your hand after the mulligan.
- **Drawn**: games where you drew it at any point, your opening hand included.
- **Played**: games where you played it.

A game counts once per card, however many copies you drew or played. Rates are green above 50% and red below. A dash
means no games yet. `DeckStatsKey` under `[Display]` changes the S key.

With **Mulligan Guide** on, each card offered in the mulligan gets a box under it: how many times you kept it out of the
times it was offered, and your win rate with it in your opening hand.

### Stats screen

The **Stats** button sits after **Collection** in the main screen's bottom drawer. It lists every deck you've finished
a match with, most played first. Pick one to see:

- its record, split into Ranked and Casual;
- its record against each opponent class;
- each card's Mulligan, Drawn and Played win rates, which match the tracker's S columns, plus how often you kept it
  when it was offered and how many games you played it in;
- its last 10 games, with the opponent, the length in turns, the mode and the date.

The screen reads the same match history as the tracker, so it shows data even with **Deck Stats** turned off. Press
Close or Escape to leave.

## What SoloStone leaves alone

- Your Hearthstone folder. It is only read.
- Battle.net, its settings and your Blizzard account.
- Windows Firewall, apart from rules named `SoloStone-<install id>-...`, one per program in the copy.
- Your saves, in `%LOCALAPPDATA%\SoloStone\saves`. Update, rollback and uninstall keep them. The launcher's Uninstall button deletes
  them only if you tick "Also delete my saves" and confirm; uninstalling from Apps & features always keeps them.
- The registry, except the standard "Apps & features" entry and what the copy writes itself. The copy has its own
  company name (`SoloStone Offline Mods`), so the game writes `HKCU\Software\SoloStone Offline Mods\Hearthstone`
  and not the key your own Hearthstone uses. The copy keeps its options in
  `%LOCALAPPDATA%\Blizzard\Hearthstone\SoloStone`, beside your own Hearthstone's data but separate from it, and may
  write to `%USERPROFILE%\AppData\LocalLow\SoloStone Offline Mods\Hearthstone`.
- Your administrator rights. SoloStone doesn't need them and doesn't ask for them, though it also runs if your account
  is always elevated (UAC turned off). Only Windows' own `cmd.exe` and `netsh.exe` are started with your permission, for
  the firewall rules.

## Uninstall

Use the launcher's **Uninstall** button or Windows' Apps & features. Both check that the folders and firewall rules
are SoloStone's own, then remove the rules and the offline copy, and then the app folder. **The copy folder is
deleted whole, including anything you put there.** Your saves stay unless you use the launcher's Uninstall button
and tick "Also delete my saves", which also deletes your save backups.

Otherwise left behind in `%LOCALAPPDATA%\SoloStone`: `backups\` (your save backups) and `reports\` (logs and launch
markers, which include your Windows user name). Delete that folder by hand if you do not want them.

## Known issues

- **Card text follows SoloStone's own file.** For the cards in the fixed set, SoloStone shows the English text from
  its own card file on any client and in any client language. After Hearthstone balance patches, that text can
  differ from your client's or the live game's. Cards outside the set show your client's own text.
- **Hearthstone updates:** see "Your Hearthstone version". A new major version blocks SoloStone until a new release.
- **Quit Battle.net first.** Do not run your normal Hearthstone at the same time as SoloStone. Running both together
  has not been tested.
- **Not tested yet:** a brand-new Windows account, third-party firewalls (SoloStone can only read Windows
  Firewall), and clients newer than the one it was tested with.
- **Antivirus:** a loader file in the copy (`winhttp.dll`) can trigger false alarms in some antivirus programs.
  Do not switch your antivirus off. If it flags the file, check the installer as in "Optional: check your download" and
  report it to your antivirus vendor as a false positive. If you are not comfortable, do not install.
- Windows only. Steam Deck and Linux are not supported yet. SoloStone's own menus are in English only; see "Game
  language" for the game's text.

Problems or questions: see [CONTRIBUTING.md](../.github/CONTRIBUTING.md). Logs in `%LOCALAPPDATA%\SoloStone\reports` can include
your Windows user name and folder paths; remove those before you post a log.

## Optional: check your download

For people who want proof the file is genuine. Most players can skip this.

Also download `SHA256SUMS` and `SHA256SUMS.minisig` from the same release.

In PowerShell, in the folder you downloaded to:

```powershell
Get-FileHash .\SoloStone-Setup-<version>.exe -Algorithm SHA256
```

The hash printed must match the line for that file in `SHA256SUMS` (upper or lower case does not matter). A matching hash only proves anything if
`SHA256SUMS` itself is genuine, so also check its signature with [minisign](https://jedisct1.github.io/minisign/):

```text
minisign -Vm SHA256SUMS -P RWSsM35KycCUtK/VB0YlewJOA99zkss4+lMLTL7yVzm56kxILzP7W4PN
```

It must say the signature is valid. If the hash or the signature does not match, do not run the file, and tell us
(see [SECURITY.md](../.github/SECURITY.md)).
