# Captain's Log / CLExporter

**Version:** v0.0.3 Beta  
**Author:** C Melvin  
**Last Updated:** 07/05/2026

---

## Most Important

This project is an unofficial community-made tool for **Captain of Industry** and is not affiliated with, endorsed by, or supported by MaFi Games.

This mod may include short excerpts from, references to, or reflected structures from Captain of Industry game code. Any such game code is © MaFi Games and is used only under the Captain of Industry Modding Policy.

MaFi Games retains all rights, title, and interest in and to Captain of Industry and its intellectual property, including without limitation all underlying code, art, assets, audio, names, elements, and materials.

---

## Overview

**Captain's Log** is a local factory diagnostics dashboard powered by the **CLExporter** mod for **Captain of Industry**.

`CLExporter` runs inside the game and exports factory data from your loaded save. The Captain's Log Dashboard then reads that data locally and helps identify production balance, bottlenecks, power issues, paused machines, and upstream input dependency problems.

The goal is simple:

> Plan smarter. Build cleaner. Waste less. Scale better.

Or, in factory terms:

> Less spreadsheet fog. More useful signals.

---

## Architecture

Captain's Log currently has two main downloadable components:

### 1. CLExporter

`CLExporter` is the Captain of Industry mod component.

It runs inside Captain of Industry and exports the required factory data from your loaded save.

The exporter writes JSON files to:

```text
%APPDATA%\Captain of Industry\Mods\CLExporter
```

Generated JSON filenames currently include:

```text
coi_products.json
coi_recipes.json
coi_world_machines.json
coi_power.json
coi_power_entities.json
```

> Note: The JSON filenames intentionally still use the `coi_*.json` pattern for compatibility.

### 2. Captain's Log Dashboard

The Dashboard is a separate local web app located in the `cl-dashboard` folder.

It reads the exported data from `CLExporter`, analyses it, and displays the results in your browser at:

http://localhost:3030

Dashboard releases include a bundled Node.js runtime and are launched using:

Start Dashboard.bat

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

### Factory Balance

- Whole-factory product balance view
- Product grouping to reduce massive flat product lists
- Product group filter based on exported game product types
- Search by product name, product ID, status, and product family
- Status filters for shortfalls, surplus, balanced products, flow issues, disposal issues, and external input requirements
- Product detail panel showing:
  - Producers
  - Productive consumers
  - Disposal / sinks / venting paths
- Custom machine names are supported in the detail view
- Paused machines are shown and flagged
- Paused machines contribute **0** to current production and current consumption
- Paused machines still show their installed capacity where relevant
- Air pollution is shown as a top-level current KPI instead of as a normal product-balance row

### Input Dependencies

New in **v0.0.3 Beta**.

- Separate **Input Dependencies** tab
- Trace the actual upstream inputs required for a selected product
- Uses actual assigned/active machine recipes from the exported factory data
- Groups producer machines by recipe and machine type
- Shows direct upstream inputs with:
  - Required here
  - Total factory demand
  - Can produce
  - Current production
  - Net capacity
  - Status
- Click upstream inputs to walk the dependency chain
- Breadcrumb trail shows the path taken through the chain
- Back up chain button allows easy navigation back through the trace
- Breadcrumb colours indicate issue severity

This is an **actual factory dependency diagnostic**, not a theoretical recipe planner.

For example, if **Lab Equipment IV** is not running properly, the dependency view can help trace whether the real bottleneck is Electronics III, Lab Equipment III, or something further upstream.

### Power

- Power overview tab
- Demand, capacity, margin, and status cards
- Generator and power asset table
- Diesel backup include/exclude toggle
- Power asset filtering and sorting
- Turbine / mechanical power information where exported
- Solar assets shown as capacity-only where current generation data is limited
- Custom power asset names are supported
- Header tooltips explain key power columns

### Dashboard Quality of Life

- Top banner with app title, version, last export, last refresh, refresh button, and scope note
- Local browser dashboard
- Bundled Node.js runtime in packaged Dashboard releases
- Refreshes data automatically while running
- Manual refresh button
- Clearer handling of paused machines, pollution, and dependency diagnostics

---

## Important Disclaimer

Captain's Log currently calculates from exported factory data and should be treated as a planning and diagnostic aid.

It does **not** modify game data.

The Factory Balance tab is a **whole-factory view**. It is useful for identifying global product shortfalls, surpluses, disposal capacity, and producer/consumer issues.

The Input Dependencies tab is an **actual factory dependency view**. It follows the recipes currently represented by your built/exported factory machines. It does not yet attempt to solve every possible theoretical route from raw resources to final products.

In short:

> Good for planning.  
> Good for diagnostics.  
> Still worth checking in-game before rebuilding half the island.

Do not go building a diesel empire off one unchecked number. Tiny calculator goblins are sneaky.

---

## Requirements

To use Captain's Log, you need:

- Captain of Industry
- The `CLExporter` mod ZIP from the Captain of Industry Modding Hub
- The Captain's Log Dashboard ZIP from GitHub
- A Windows PC

Dashboard releases include a bundled Node.js runtime. In that case, you do **not** need to install Node.js separately.

If you are running the Dashboard from source rather than a packaged release, you may need Node.js LTS installed and available on PATH.

Download Node.js LTS from:

https://nodejs.org/

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
   - Last export
   - Last refresh
   - Refresh status

7. Use **Factory Balance** to check:
   - Required inputs
   - Expected outputs
   - Current production
   - Net supply
   - Product shortfalls
   - Surplus products
   - Disposal capacity
   - Paused or blocked producers

8. Use **Input Dependencies** to trace upstream issues:
   - Select a product in Factory Balance
   - Open Input Dependencies
   - Review producer groups and direct upstream inputs
   - Click an input to walk upstream
   - Use breadcrumbs or Back up chain to navigate

9. Use **Power** to check:
   - Power demand
   - Effective capacity
   - Margin
   - Generator status
   - Diesel backup capacity
   - Mechanical power production and demand

---

## Data Refresh

The Dashboard includes refresh information in the top banner.

### Last Export

Shows the latest export trigger/count from `CLExporter` where available.

### Last Refresh

Shows when the Dashboard last refreshed data from the local exported JSON files.

If the refresh time does not update:

- Make sure Captain of Industry is running.
- Make sure the mod is enabled.
- Make sure a save is loaded.
- Wait at least 10 seconds after loading your save.
- Click **Refresh now**.

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

If you are using a packaged Dashboard release, make sure you are running `Start Dashboard.bat` from inside the extracted Dashboard folder.

Packaged Dashboard releases include a bundled Node.js runtime.

If you are running from source, install Node.js LTS and try again:

https://nodejs.org/

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

### Browser shows an old version

Your browser may have cached an older dashboard file.

Try:

```text
Ctrl + F5
```

Or close and reopen the browser tab.

---

### I only downloaded the Modding Hub file and cannot find the Dashboard

That is expected.

The Modding Hub package only includes `CLExporter`.

You must also download the Captain's Log Dashboard ZIP from the GitHub releases page.

---

## Known Limitations

- Factory Balance is a whole-factory view and may not reflect isolated chains by itself.
- Input Dependencies follows actual exported producer recipes, not every possible theoretical recipe route.
- Alternative production routes are not automatically solved yet.
- Source data must be accurate for outputs to be reliable.
- Some calculations may require manual validation.
- Browser caching may show old files if the Dashboard is updated.
- Dashboard must currently be downloaded separately from GitHub.
- Farms and Population tabs are placeholders.
- Some game concepts may require additional exporter data before deeper diagnostics are possible.

---

## Future Improvements

Potential future features:

- Deeper dependency diagnostics
- Optional recipe-route comparison for products with multiple production methods
- Better producer-state summaries in dependency view
- Product search directly inside Input Dependencies
- Chain breadcrumbs with richer issue explanations
- Export results to CSV or Excel
- Save and load factory profiles
- Better error messages
- Visual production flow diagrams
- Packaged desktop version
- User-friendly installer
- Single-package release if Modding Hub upload restrictions change
- Farms and population demand analysis

---

## Change Log

### [07/05/2026] - v0.0.3 Beta

- Added: New **Input Dependencies** tab for tracing upstream product requirements
- Added: Producer group summaries for selected products
- Added: Direct upstream input table with required here, total factory demand, production capacity, current production, net capacity, and status
- Added: Click-to-trace dependency navigation
- Added: Breadcrumb trail for dependency tracing
- Added: Back up chain button for dependency navigation
- Added: Product visual grouping in Factory Balance
- Added: Product group filter using exported product type data
- Changed: Air pollution moved out of the normal product table and into a top-level current KPI
- Changed: Paused machines are included and flagged, but contribute 0 to current production and consumption
- Changed: Factory Balance machine count label updated to included machines
- Improved: Factory Balance readability for large product lists
- Improved: Custom machine names remain visible in Factory Balance, Power, and dependency diagnostics

### [06/05/2026] - v0.2 Alpha

- Changed: Renamed exporter references to `CLExporter`
- Changed: Updated install instructions for separate Modding Hub and GitHub downloads
- Changed: Clarified that the Dashboard must be downloaded separately from GitHub
- Fixed: Removed duplicate troubleshooting section
- Fixed: Cleaned up README structure and heading format

### [05/05/2026] - v0.1 Alpha

- Added: Initial release

---

## Credits

Created by:

**C Melvin**

Project:

**Captain's Log / CLExporter**

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
