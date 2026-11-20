# 13th Console — design system

## Product context
13th Console is a private, self-hosted web panel for one Arma 3 gaming unit (the 13th Special Tactics Unit, a Halo-themed milsim group). It is NOT a commercial hosting product: no pricing, no marketing, no customer onboarding.
- **Users:** server admins, mission makers, Zeus/mission commanders, mod managers, regular members (roughly 40–60 people).
- **Jobs to be done:**
  - Mission makers upload a mission; it lands on the server immediately (no approval step).
  - Zeus or mission makers pick a mission that matches the loaded modlist and start it.
  - Admins start/stop/restart the dedicated server, watch the live console, and manage Workshop mods, modlists and server profiles (like the desktop tool FASTER, but on the web).
  - Everyone sees upcoming operations, server status and announcements.
- **Hard rule:** never show a state that isn't real. Server "Online" only when verified; missing data is shown as "n/a", never invented.

## Key pages (sidebar navigation, in this order)
Dashboard · Operations · Missions (Library, On server, Upload) · Server Control (console, players, headless clients) · Server Profiles (tabs: Overview, General, Mods, Missions, Difficulty, Headless clients, Server config, Performance, Startup params, Logs) · Mod Manager (Modlists, Installed mods, Workshop, Keys) · File Manager · Logs · Backups · Members · Settings.

## Visual direction: UNSC terminal in unit red
A Halo/UNSC ship-terminal interface (holographic console panels on a dark bridge), rendered in the unit's own colours. Serious, dense, functional admin tool — not a game HUD toy and not a generic SaaS dashboard.

### Colour (use ONLY these)
- Page background `#0B0C0D`, sidebar `#0E1011`, panel surface `#121416`, raised `#1A1D1F`, console/editor background `#08090A`.
- Text `#E6E6E6`; secondary `#C9CCCE`; muted `#A3A7AA`; smallest allowed text colour `#868C90`. `#6E7478` is decoration only, never text.
- Hologram colour (lines, active tabs, primary buttons): unit red `#E8453C`; red text on dark `#F2716A`; hologram line `rgba(232,69,60,.38)`; background grid `rgba(232,69,60,.06)`.
- Status blocks (solid fill, near-black `#0B0C0D` text, always with a word): ok `#93C47D`, warn `#F6B26B`, bad `#E8453C`, off `#8C9195`.
- Text on solid red or status fills is near-black `#0B0C0D`, never white.
- No other hues. No purple, pink, blue gradients, neon, or gradient washes.

### Type (use ONLY these Google Fonts)
- UI text: **Saira** 400/500/600/700.
- Headings, panel titles, buttons: **Saira Condensed** 600/700, uppercase, letter-spacing .05em.
- Labels, IDs, timestamps, logs, code: **Share Tech Mono**, uppercase for labels with letter-spacing .1em.
- Scale: 11 (mono labels) · 12 · 14 (desktop body/tables) · 16 (phone body) · 20 · 26 (page title bar) · 34 (entity heading) · 44 (gauge numerals).

### Shape and texture
- Square corners everywhere. No border-radius.
- Panels: cut (chamfered) top-left and bottom-right corners (18px cut) with a 1px hologram-red outline.
- Panel header: a solid red angled tab (right edge slanted) holding the panel title with a section code in the unit's style, e.g. `13:SRV // MAIN OPERATIONS`, `13:OPS // UPCOMING`; the rest of the header row is a thin red line with right-aligned mono meta text such as `A2S >>> OK 21:04:10`.
- Buttons: cut corners (10px), Saira Condensed uppercase; primary = solid red with near-black text; default = dark with red outline; danger = red outline + red text.
- Faint 40px hologram grid on the page background; very subtle horizontal scanlines inside panels.
- Round gauges (player count, readiness) with a dashed outer tick ring; skewed segment bars for readiness (n of 11).
- Telemetry copy style: `LINK >>> MAIN-OPS`, `SYSTEM CHECK >>> SERVER ONLINE // NEXT OP 1D 21H`.
- Title bar: "13TH SPECIAL TACTICS UNIT || DASHBOARD" in Saira Condensed, with a thick red stripe under it that ends in a slant, then a thin red line.
- Sidebar: unit crest placeholder (red ring with "XIII"), groups `// OPERATIONS`, `// SERVER`, `// UNIT`; active item = solid red fill with near-black text and an arrow-cut right edge; footer motto "ALL WE NEED IS LUCK".
- Icons: simple 24px stroke SVGs (lucide style). Never emoji.

### Motion
150 ms colour/border/background transitions on hover. No decorative animation. Respect prefers-reduced-motion.

### Accessibility
Visible focus on everything (inset 2px ring on cut-corner elements). 4.5:1 text contrast. 44px touch targets on touch devices. Status never colour-only. Real labels on inputs.

## Sample content rules
Use realistic Arma 3 content: missions like `co40_Op_Glasswing.Altis`, maps Altis/Malden/Tanoa/Stratis/Livonia, mods CBA_A3, ACE3, OPTRE, Task Force Arrowhead Radio, Zeus Enhanced, 3DEN Enhanced; modlist "Main Unit Modset"; profile "Main Operations"; player count 38 / 64. Use generic callsigns (e.g. Hawk, Viper, Atlas, Nomad). Mark the page with a small amber "MOCK-UP · SAMPLE DATA" chip.
