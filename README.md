# Captain's Log - COI Factory Calculator

**Version:** v0.2 Alpha  
**Author:** C Melvin  
**Last Updated:** 06/05/2026

---

## Most Important

This mod includes short excerpts or references to Captain of Industry game code. Any such game code is © MaFi Games and is used only under the Captain of Industry Modding Policy.

MaFi Games retains all rights, title, and interest in and to the game and its IP, including without limitation all underlying code, art, assets, audio, elements, and materials.

This project is an unofficial community-made tool and is not affiliated with, endorsed by, or supported by MaFi Games.

---

## Overview

**Captain's Log - COI Factory Calculator** is a lightweight factory planning and calculation tool for **Captain of Industry**.

It is designed to help players calculate resource chains, factory inputs, outputs, production rates, and bottlenecks without needing to manually juggle spreadsheets, ratios, and tiny calculator goblins.

The goal is simple:

> Plan smarter. Build cleaner. Waste less. Scale better.

---

## Architecture

Captain's Log currently has two main downloadable components:

### 1. CLExporter

`CLExporter` is the Captain of Industry mod component.

It runs inside Captain of Industry and exports the required factory data from your loaded save.

### 2. Captain's Log Dashboard

The Dashboard is a separate local web app.

It reads the exported data from `CLExporter`, analyses it, and displays the calculator outputs in your browser.

---

## Important Download Note

The Captain of Industry Modding Hub does **not** allow HTML files to be uploaded.

Because the Dashboard contains local web app files, Captain's Log is distributed as two separate downloads:

1. **CLExporter ZIP**  
   Available from the Captain of Industry Modding Hub.

2. **Captain's Log Dashboard ZIP**  
   Available from the GitHub releases page.

You need **both** downloads for Captain's Log to work.

The Modding Hub package only contains the exporter mod.  
The Dashboard must be downloaded separately from GitHub.

---

## Key Features

- Export factory data from Captain of Industry using `CLExporter`
- Calculate factory input and output requirements
- Compare production chains and resource demands
- Identify factory-level bottlenecks
- Support custom factory names
- Show refresh status and last updated data
- Provide whole-factory calculations
- Simple local web app interface
- Designed for easy sharing and future packaging

---

## Important Disclaimer

This tool currently calculates at a **whole-factory level**.

It does not yet isolate every individual production chain unless that feature has been specifically added. Results should therefore be treated as a factory-wide planning aid rather than a perfect chain-by-chain simulation.

In short:

> Good for planning.  
> Dangerous if trusted blindly.  
> Do not go building a diesel empire off one unchecked number.

No game data is modified, including calculations. Every effort has been made to confirm accuracy within the current limitations and features outlined below.

---

## Requirements

To use Captain's Log, you need:

- Captain of Industry
- The `CLExporter` mod ZIP from the Captain of Industry Modding Hub
- The Captain's Log Dashboard ZIP from GitHub
- A Windows PC
- Node.js LTS installed and available on PATH, unless using a packaged Dashboard release that includes Node.js

Download Node.js LTS from:

https://nodejs.org/

> Note: GITHIB Dashboard releases include a bundled Node.js runtime. In this case, you do not need to install it separately.

---

## Installation

### Step 1 - Install CLExporter

1. Download the `CLExporter` ZIP from the Captain of Industry Modding Hub.
2. Extract or copy the `CLExporter` mod folder into:

   ```text
   %APPDATA%\Captain of Industry\Mods
   ```

3. Start Captain of Industry.
4. Enable the `CLExporter` mod.
5. Load your save.
6. Wait at least 10 seconds for the export files to generate.

---

### Step 2 - Install the Dashboard

1. Download the Captain's Log Dashboard ZIP from the GitHub releases page.
2. Extract the Dashboard ZIP to a folder of your choice.
3. Open the extracted Dashboard folder.
4. Double-click:

   ```text
   Start Dashboard.bat
   ```

5. The dashboard should open at:

   ```text
   http://localhost:3030
   ```

If the browser does not open automatically, copy and paste the address above into your browser.

---

## How to Use

1. Start Captain of Industry.
2. Enable the `CLExporter` mod.
3. Load your save.
4. Wait for the export files to generate.
5. Start the Captain's Log Dashboard using `Start Dashboard.bat`.
6. Review the top banner for:
   - App title
   - Factory name
   - Logo
   - Last refresh date/time
   - Data refresh status

7. Use the calculator outputs to check:
   - Required inputs
   - Expected outputs
   - Production balance
   - Potential shortages
   - Overproduction areas

8. Adjust your factory plan in-game as required.

---

## Data Refresh

The Dashboard includes refresh information in the top banner.

### Last Refresh

Shows when the factory data was last updated by `CLExporter`.

If the refresh time does not update:

- Make sure Captain of Industry is running.
- Make sure the mod is enabled.
- Make sure a save is loaded.
- Wait at least 10 seconds after loading your save.

---

## Troubleshooting

### Dashboard opens but shows no data

Check that:

- Captain of Industry is running
- The `CLExporter` mod is enabled
- A save is loaded
- You have waited at least 10 seconds
- The Dashboard ZIP has been extracted properly
- You downloaded both the `CLExporter` package and the Dashboard package

---

### Start Dashboard says Node.js is missing

Install Node.js LTS and try again:

https://nodejs.org/

If you are using a Dashboard release that includes Node.js, make sure you are running `Start Dashboard.bat` from inside the extracted Dashboard folder.

---

### Port 3030 is already in use

This usually means the Dashboard is already running.

Try this:

1. Close any existing Dashboard terminal windows.
2. Close any browser tabs using:

   ```text
   http://localhost:3030
   ```

3. Run `Start Dashboard.bat` again.

If it still fails, restart your PC and try again.

---

### I only downloaded the Modding Hub file and cannot find the Dashboard

That is expected.

The Modding Hub package only includes `CLExporter`.

You must also download the Captain's Log Dashboard ZIP from the GitHub releases page.

---

## Known Limitations

- Whole-factory calculations may not reflect individual chain behaviour.
- Source data must be accurate for outputs to be reliable.
- Some calculations may require manual validation.
- Local setup may require Node.js.
- Browser caching may show old files if the Dashboard is updated.
- Dashboard must currently be downloaded separately from GitHub.
- Chain-specific calculation support is limited in this alpha release.

---

## Future Improvements

Potential future features:

- Chain-specific calculations
- Packaged desktop version
- Export results to CSV or Excel
- Save and load factory profiles
- Better error messages
- Visual production flow diagrams
- Dark mode
- Import directly from game data
- Multi-factory comparison
- User-friendly installer
- Single-package release if Modding Hub upload restrictions change

---

## Change Log

### [06/05/2026] - v0.2 Alpha

- Changed: Renamed exporter references to `CLExporter`
- Changed: Updated install instructions for separate Modding Hub and GitHub downloads
- Changed: Clarified that the Dashboard must be downloaded separately from GitHub
- Fixed: Removed duplicate troubleshooting section
- Fixed: Cleaned up README structure and heading format

### [05/05/2026] - v0.2 Alpha

- Added: First release

---

## Credits

Created by:

**C Melvin**

Project:

**Captain's Log - COI Factory Calculator**

Inspired by:

**Captain of Industry**, overbuilt conveyor spaghetti, and the eternal dream of a perfectly balanced production chain.

---

## License

This project is licensed under the MIT License. See the `LICENSE` file for details.

This license applies only to original Captain's Log project code and content. It does not apply to Captain of Industry game code, assets, trademarks, or other MaFi Games intellectual property.

---

## Final Note

This tool is intended to support planning and decision-making, not replace common sense, in-game testing, or the sacred art of staring at a production line until the problem reveals itself.

Use it, check it, improve it.

**Captain's Log out.**
