# Windhawk Share

> ⚠️ **Alpha software.** Expect bugs and rough edges. Back up your Windhawk settings before importing.

Export your Windhawk mod settings to a file and import them on another PC: share your setup with
others, or try someone else's in a few clicks.

## Core rule

Only **unmodified mods from the official repository** (`ramensoftware/windhawk-mods`) are shared.

- The package format has no fields for code, sources or URLs: it only contains mod IDs, versions
  and setting values.
- Local mods (`local@...`) and mods not in the official repository are excluded.
- When the installed version is the latest one, the installed source is compared with the official
  source (SHA-256): if they differ, the mod is excluded.
- On import, mods are installed **by ID only, from the official repository**, through Windhawk's own
  CLI. The CLI's `--file` option is never used.
- The repository address is hard-coded, never read from external files.
- If the repository can't be reached, the operation stops: no verification, no export or install.

## Requirements

- **Windhawk 2.0 or later** (currently in alpha). The tool uses `windhawk-cli.exe`, the official
  command-line interface introduced in 2.0. It does not work with Windhawk 1.7.x.
- **Administrator rights for importing**: Windhawk stores settings in the system registry
  (`HKEY_LOCAL_MACHINE`). Exporting works without them.
- To build: .NET 10 SDK. The published executables need nothing else.

## GUI version

`windhawk-share-gui.exe` has two tabs:

- **Export**: tick the mods to share (expand them to choose individual settings), fill in name,
  author and description, then click **Save package...**. Local mods are greyed out and can't be
  selected.
- **Import**: **Open package...** shows the plan. Settings containing paths or commands are shown
  in orange. Untick any mods you don't want, then click **Apply**. If the app isn't running as
  administrator, it offers to restart itself elevated and reopens the same package.

## Command-line version

```
windhawk-share list
windhawk-share settings windows-11-file-explorer-styler
windhawk-share export -o my-setup.json
windhawk-share export -o explorer.json --mod windows-11-file-explorer-styler --name "My Explorer" --author me
windhawk-share export -o explorer.json --mod windows-11-file-explorer-styler:theme,controlStyles
windhawk-share import my-setup.json
windhawk-share import my-setup.json --only windows-11-file-explorer-styler
windhawk-share import my-setup.json --exact-version
```

Without `--mod`, `export` starts a guided selection.

Settings are selected by top-level name: `TimeStyle` includes `TimeStyle.FontSize`,
`TimeStyle.TextColor`, etc. Lists (`controlStyles[0]...`, `controlStyles[1]...`) are always shared
whole, to avoid inconsistent lists.

## How import works

Before changing anything, the tool shows the plan: which mods will be installed, which will only have
their settings updated, what will be skipped and why, and which settings contain paths, commands or
web addresses. Nothing happens until you confirm.

- Keys and values are checked before being passed to the CLI (no `=` in keys, no arguments starting
  with `-`, length limits); then Windhawk validates them against the mod's settings schema.
- If Windhawk rejects some settings (e.g. they no longer exist in the installed version), the others
  are still applied, group by group, and the rejected ones are listed.
- For mods you already have, only the settings in the package change; version and enabled state are
  left as they are. New mods are installed enabled or disabled as in the package.
- By default the latest version of each mod is installed; `--exact-version` (or the checkbox in the
  GUI) installs the package's version instead.

## Building

Locally:

```
dotnet publish WindhawkShare.csproj -c Release -r win-x64 -o publish/cli
dotnet publish gui/WindhawkShare.Gui.csproj -c Release -r win-x64 -o publish/gui
```

Use `-r win-arm64` for ARM PCs.

GitHub Actions builds both versions for x64 and ARM64 on every push to `main`. Pushing a tag like
`v0.1.0` also publishes a release with the executables attached.

## Known limitations

- The executables are not digitally signed yet, so Windows SmartScreen may warn on first launch.
  Right-click → Properties → Unblock.
- The check for locally modified mods assumes mod sources are in
  `%ProgramData%\Windhawk\ModsSource`. If they aren't found, the check is skipped with a warning
  (use `--mods-source` for portable installs).
- If your list setting is longer than the package's, the extra items are left unchanged
  (the plan warns about it).

## Project structure

- `src/Models.cs`: package format
- `src/WindhawkCli.cs`: the only point of contact with Windhawk, through the official CLI
- `src/OfficialCheck.cs`: verification against the official repository
- `src/SettingsTools.cs`: flat settings and selection by name
- `src/Exporter.cs`: package creation
- `src/Importer.cs`: checks on incoming packages, plan and execution
- `src/Program.cs`: command-line interface
- `gui/`: GUI version (Windows Forms), sharing the files in `src/` except `Program.cs`
