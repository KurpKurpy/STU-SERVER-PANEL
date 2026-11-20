# Other server-side performance measures for an Arma 3 dedicated server (beyond basic.cfg and launch parameters)

Scope: Windows dedicated server, heavily modded milsim (ACE/CBA), 40–80 players, Zeus-run missions, 1 Gbit upload, modern CPU with 6 or more cores, game version 2.20/2.22.

How to read the tags on each finding:
- **Tier:** T1 = Bohemia developer statement or official changelog. T2 = Bohemia wiki (Biki): hosted by Bohemia but edited by the community. T3 = mod documentation (ACE3, Werthles). T4 = community anecdote (BI forums, Steam, Epoch forums). T5 = hosting-company knowledge base or marketing.
- **Retrieval:** "(snippet)" means the content came from a search-engine summary because the page itself could not be fetched. Every Biki and BI-forum page returned HTTP 403 to fetches, and a scripted download of the Biki was refused, so **every Biki and BI-forum claim below is snippet-level**. "(read)" means the page itself was fetched and read.
- **Quotation marks:** on snippet-tagged findings, quoted text is as the search summariser relayed it and may be paraphrased. Pages tagged "(read)" also reached me through the fetch tool's model summary, not as raw page text.
- **"Page not pinned":** the claim came from a search summary that did not say which result it was from. The links given are the candidate results ("one of"). Treat these as weaker than pinned findings.
- **Era:** years marked "est." or "about" are my estimates from topic IDs and context. They are not dates shown in the retrieved content. Versions such as ACE 3.6.2 or Arma 2.22 are as stated by the source.
- **Set by:** `server.cfg`, `launch` (launch option), `host OS`, `mission`, or `mod settings` (CBA/ACE settings).
- **No T1 source was retrieved.** No Dwarden post or other BI developer statement could be read. The one T1 hit was a search result for the 2.20 Prof-Branch changelog, which is cited only for a logging key.

---

## 1. Headless clients: how much do they help, how many to run, and what setup advice recurs (offloading AI, ACE headless, Zeus/ZHC, Werthles)?

### Takeaway
The sources agree that a headless client (HC) is the main server-side fix for AI-driven FPS loss:
- three Biki pages (Headless Client, Dedicated Server, Mission Optimisation);
- ACE;
- the authors of the Werthles and ZISHC modules;
- a BI-forum Zeus anecdote.
Host guides say the same, but I could not pin those claims to a specific page. None of the sources gives a controlled benchmark. ACE's own distribution supports up to 3 HCs and says it is "most effective with 3". The Biki says HCs should run on the same machine or LAN, with their IP in both `headlessClients[]` and `localClient[]`. An HC does nothing unless something moves the AI to it. For Zeus-run missions that is a real problem: Zeus-placed AI starts out local to the Zeus player's machine, and the ACE docs never say whether ACE moves Zeus spawns.

### Cited Findings
**What an HC is and how it is wired (T2, snippet)**
- HCs offload AI calculation from the server instance. An arbitrary number of HCs can run on one machine. Each HC's IP goes in `headlessClients[]`, and multiple addresses are allowed. — [Biki: Arma 3 Headless Client](https://community.bistudio.com/wiki/Arma_3:_Headless_Client) (T2, snippet; set by: `server.cfg`)
- Clients listed in `localClient[]` get unlimited bandwidth and near-zero latency. Use 127.0.0.1 for an HC on the same box. — [Biki: Arma 3 Headless Client](https://community.bistudio.com/wiki/Arma_3:_Headless_Client) (T2, snippet; set by: `server.cfg`)
- The HC and the server need "extremely low latency and high bandwidth", so they should be on the same LAN at least and preferably on the same computer. Faster CPU core speed matters most. Offloading AI to multiple HCs on one machine "will probably give the best results for complex missions with many AI units", though it is expensive. — [Biki: Arma 3 Dedicated Server](https://community.bistudio.com/wiki/Arma_3:_Dedicated_Server) (T2, snippet; set by: `host OS` / `server.cfg`)
- The Mission Optimisation page is more cautious: a player who owns "more than one average computer" could consider an HC to offload AI. — [Biki: Mission Optimisation](https://community.bistudio.com/wiki/Mission_Optimisation) (T2, snippet)
- `setGroupOwner` moves a group and all its units to a client. It must be called on the server, and the group leader must not be a player. Reported pitfalls:
  - AI lose their loadouts when many groups are transferred in quick succession. The workaround is a delay between transfers.
  - Locality-dependent commands such as `forceSpeed` stop working after a transfer and must be reapplied on the new owner.
  - [Biki: setGroupOwner](https://community.bistudio.com/wiki/setGroupOwner) (T2, snippet; set by: `mission`)
- An HC is a game client that renders nothing. If the mission is not "headless-ready", the HC sits idle and still takes a player slot. — Steam Community discussion found via search; the exact thread was not pinned. Candidates: [thread 622954302095460190](https://steamcommunity.com/app/107410/discussions/0/622954302095460190) and [thread 2288338908681838166](https://steamcommunity.com/app/107410/discussions/17/2288338908681838166) (T4, snippet)

**ACE3 headless (T3, read)**
- ACE passes AI groups automatically to up to 3 HCs, round-robin. Transfers are event-based: on unit spawn and on HC connect or disconnect. Whole groups move together. The page says "the performance impact is minimal", which refers to the cost of the transfer module itself. — [ACE3 wiki: Headless](https://ace3.acemod.org/wiki/feature/headless) (T3, read; set by: `mod settings`)
- Distribution is "most effective with 3 Headless Clients". The settings or a module control a minimum delay between transfers, which reduces desync from many locality changes, and transfer logging. ACE skips these groups automatically:
  - groups whose waypoints are tied to synchronized triggers;
  - groups that contain a player or a UAV;
  - groups already local to an HC.
  **Vanilla Support Modules are not skipped automatically.** They "will stop functioning" after a transfer, so the mission-maker must blacklist those units. Blacklist a group with `this setVariable ["acex_headless_blacklist", true]` or `ace_headless_fnc_blacklist`. When an HC disconnects, Arma hands its units back to the server and ACE forces a full rebalance. — [ACE3 docs: headless framework](https://github.com/acemod/ACE3/blob/master/docs/wiki/framework/headless-framework.md) (T3, read; set by: `mod settings` / `mission`)
- The ACE headless docs do not mention Zeus/curator-placed units at all. — same source (read)

**Zeus-placed AI (T3/T4)**
- AI created by Zeus is local to the machine running Zeus (the Ares maintainer). Another commenter in the same thread said Zeus handles AI much like an HC, and that AI returns to the server when an HC disconnects. Both statements are as relayed by a search summary. — [GitHub astruyk/Ares #159](https://github.com/astruyk/Ares/issues/159) (T3/T4, snippet, 2015)
- Werthles' Headless Module:
  - What its page lists:
    - "Editor/script/Zeus AIs auto-transferred to HC control";
    - "Splits AIs evenly among multiple HCs";
    - a "Time between each HC transfer, to aid stability";
    - an Ignore module and a phrase blacklist;
    - "Compatible with ACE, UPSMON…", with the advice to raise the startup delay for mods that need setup time;
    - a warning that transfers "change the locality" and "other mods can also break the functionality".
  - Last updated 17 Oct 2020.
  - **The fetched page also showed a "removed … violates Steam Community & Content Guidelines" notice and an "incompatible with Arma 3" note.** This could be a fetch artifact. Check the item's current status before recommending it.
  - [Steam Workshop: Werthles' Headless Module](https://steamcommunity.com/sharedfiles/filedetails/?id=510031102) (T3, read)
- ZISHC transfers curator-placed units to the server or to HCs and makes them visible to other curators. — [Steam Workshop: ZISHC](https://steamcommunity.com/sharedfiles/filedetails/?id=639965833) (T3, snippet)
- A 2024 comment on the Zulu Headless Client (ZHC) reports that "most if not all of the AI spawned in by the zeus remains untransferred". Another says transferred units lose AI changes such as disabled pathing. — [Steam Workshop comments, item 2450921295](https://steamcommunity.com/sharedfiles/filedetails/comments/2450921295) (T4, snippet)
- One user reported a large improvement after a 23-player Zeus mission using a Zeus-to-server/HC integration mod. This is a single anecdote. — [BI forums: Zeus Integration with Servers and Headless Client](https://forums.bohemia.net/forums/topic/188769-zeus-integration-with-servers-and-headless-client/) (T4, snippet)
- A BI-forum script listens for curator placements and calls `setGroupOwner` to an HC. Its author warns that it handles only one HC and does not check whether that HC is connected. — [Steam/BI discussion: Zeus ai](https://steamcommunity.com/app/107410/discussions/17/3183345176714600243/) and [BI forums: Headless-like script to offload AI from Zeus's client](https://forums.bohemia.net/forums/topic/201526-headless-like-script-to-offload-ai-from-zeuss-client-to-server/) (T4, snippet)

**How much HCs help, and how many to run (T4/T5)**
- A forum rule of thumb: "a good server is a server with a headless client and has at least 30 server frames". — page not pinned; one of [NFOservers forum t=10786](https://forums.nfoservers.com/viewtopic.php?t=10786) or [Steam discussion 144513670978400562](https://steamcommunity.com/app/107410/discussions/21/144513670978400562) (T4, snippet)
- Host guides call moving AI to an HC "the single biggest FPS win". One host claims an HC is the difference between 10 and 50 server FPS in missions with 100+ AI. **This is host marketing, not a benchmark.** — page not pinned; one of [low.ms](https://low.ms/knowledgebase/arma-3-troubleshooting), [GTXGaming](https://www.gtxgaming.co.uk/arma-3-server-guide/), [vmheaven](https://vmheaven.io/blog/guides/arma-3-server), [XGamingServer](https://xgamingserver.com/arma-3-server-hosting) or [Fatality Servers](https://fatalityservers.com/arma-3-server-hosting/) (T5, snippet)
- On the conflict over HC count:
  - ACE says up to 3, most effective with 3 (above).
  - One user ran 3 HCs "without issue" with Werthles. Another found an HC added "considerable additional overhead load on the system even when no AI were loaded onto it". — page not pinned; one of [BI forums: Headless Client Script for all AI](https://forums.bohemia.net/forums/topic/204479-headless-client-script-for-all-ai/), [BI forums: Zeus Integration…](https://forums.bohemia.net/forums/topic/188769-zeus-integration-with-servers-and-headless-client/), [Werthles Workshop page](https://steamcommunity.com/sharedfiles/filedetails/?id=510031102) or [Steam discussion 1734342793783279324](https://steamcommunity.com/app/107410/discussions/0/1734342793783279324/) (T4, snippet)
  - One admin ran 6 HCs on a separate, more powerful machine. They still saw server CPU spikes in large AI battles, and the HCs "almost always" crashed. The thread was unresolved. — page not pinned; one of [Steam 622954302095460190](https://steamcommunity.com/app/107410/discussions/0/622954302095460190), [Steam 2288338908681838166](https://steamcommunity.com/app/107410/discussions/17/2288338908681838166), [Workshop discussion 378941393/1649918058741392781](https://steamcommunity.com/workshop/filedetails/discussion/378941393/1649918058741392781) or [Workshop discussion 378941393/458607699623230154](https://steamcommunity.com/workshop/filedetails/discussion/378941393/458607699623230154) (T4, snippet)
- A dedicated server or HC is capped at 50 FPS by default. `-limitFPS` changes this, with a range of 5–1000. — [Biki: Startup Parameters](https://community.bistudio.com/wiki/Arma_3:_Startup_Parameters) (T2, snippet; set by: `launch`)

### Inferences
- **Recommended starting point for the preset guidance:**
  - Start with 2 HCs on the same box as the server, each with `127.0.0.1` in both `headlessClients[]` and `localClient[]`.
  - Go to 3 only when the CPU has free physical cores. Each HC is a full game process that wants its own fast core and its own copy of the mod set in RAM.
  - On a 6-core CPU, 1 server plus 2 HCs leaves headroom for the OS. ACE's "most effective with 3" is the upper bound for ACE-managed distribution.
  - This is my inference from the ACE and Biki statements, not a benchmarked number.
- **What a panel can do automatically:**
  - Write `headlessClients[]` and `localClient[]` into `server.cfg`.
  - Launch N HC processes with the same mod list.
  - Optionally pin each process to its own cores (see section 3).
- **What a panel cannot guarantee:** that any AI actually moves to the HCs. That needs a transfer mechanism:
  - ACE headless enabled in CBA/ACE settings,
  - or a Werthles/ZHC/ZISHC-style module,
  - or mission scripts.
  The guidance text should say this plainly.
- **Zeus-run missions:** without a transfer mechanism, Zeus-spawned AI runs on the Zeus player's own machine. Their FPS and upload then become the bottleneck for that AI, not the server's. The panel guidance should tell admins to check whether transfer is actually happening:
  - in-game, for example by watching `groupOwner` or the HC's FPS;
  - or from ACE's transfer log.
  Seeing an HC connected is not proof that AI moved.
- **Watch AI-behaviour settings on transfer:** settings applied to AI by locality-dependent commands, such as disabled pathing or `forceSpeed`, may be lost when a group moves (Biki setGroupOwner; ZHC comment). Mission-makers using "garrison/hold" AI should set them after transfer or blacklist those groups.

### Gaps
- No controlled benchmark of server FPS with 0, 1, 2 or 3 HCs on a current (2.x) build was found. No BI developer figure was found either.
- The search could not confirm whether ACE headless transfers Zeus-placed groups on spawn. ACE's docs are silent, and confirming it needs a test or a read of ACE source (`addons/headless`).
- Whether ZEN (Zeus Enhanced) has its own HC/server transfer option was not found.
- Reddit (r/arma, r/armadev) was unreachable: the search tool blocks reddit.com and fetches of old.reddit.com fail. **The agreement counts above exclude Reddit.**

---

## 2. server.cfg keys with a performance effect: which actually matter?

### Takeaway
No source shows a measurable server-FPS effect for any of the listed `server.cfg` keys. Most are logging or diagnostic switches, launcher/query plumbing, or mission-list load time. `idleFPSLimit` (2.22) only acts when the server has no players. It saves host CPU for an empty instance but does nothing during an op. The `server.cfg` entries that matter for performance are the HC whitelists (`headlessClients[]`, `localClient[]`) from section 1. `disableVoN = 1` is commonly advised when TFAR or ACRE is used, though the only source is one host KB and its server load effect is unmeasured.

### Cited Findings
- **`idleFPSLimit`:** "Servers with no players will limit their FPS to the specified value." Range 5–60, default 30, added in 2.22. — [Biki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File) (T2, snippet; set by: `server.cfg`)
  - **Version conflict:** host guides say empty servers have capped themselves at 30 FPS since 2.20, "regardless of the -limitFPS parameter". — search summary over [GTXGaming](https://www.gtxgaming.co.uk/arma-3-server-guide/) and [bestarmahosting](https://bestarmahosting.com/guides/how-to-configure-your-arma-3-server-all-options-explained/) (T5, snippet; which page said 2.20 was not pinned). One possible reading: the behaviour arrived in 2.20 and the config key arrived in 2.22.
- **`steamProtocolMaxDataSize`:** default 1024. It limits the Steam Query packet length. Raising it risks UDP packets larger than the MTU, which can be fragmented or dropped by older routers. Its purpose is the mod-list length shown in the launcher. — [Biki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File) (T2, snippet). A host KB suggests starting at 2048 and stepping by 2048 for large mod lists. — [VibeGames KB](https://help.vibegames.com/en/article/arma-3-steam-query-data-overflow-1bglksb/) (T5, snippet). **It has no performance role. It matters to a modded server only so the launcher can show the full mod list.**
- **`enablePlayerDiag`:** default 0. When on, it logs each player's bandwidth and desync info every 60 s, plus the owner identity behind "network message is pending" messages. — [Biki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File) (T2, snippet). **It is a diagnostic tool and adds logging.**
- **`callExtReportLimit`:** default 1000 ms. If a server-side `callExtension` call takes longer than this, a warning goes to the RPT and into the extension's return value. — [Biki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File) (T2, snippet). **It is a diagnostic threshold and does not speed up extensions.** It is useful for catching slow extensions such as databases or loggers that stall the server.
- **`AdvancedOptions.logObjectNotFound`:** turns the "Server: Object not found" log messages on or off (since 2.02). — [Biki: Server Config File](https://community.bistudio.com/wiki/server.cfg) (T2, snippet)
- **`AdvancedOptions.skipDescriptionParsing`:** skips parsing `description.ext` and `mission.sqm` for the mission list. The PBO filename is shown instead of the mission name, and the mission list loads faster. — [Biki: Server Config File](https://community.bistudio.com/wiki/server.cfg) (T2, snippet). **This affects mission-list loading, not runtime.**
- **`AdvancedOptions.queueSizeLogG`:** only active while `#monitor` is running. If a player's guaranteed-message queue goes above the threshold, that player's message data is dumped to a log in the RPT directory. 0 disables it; the example value is 1000000 (1 MB). — [Biki: Server Config File](https://community.bistudio.com/wiki/server.cfg) (T2, snippet). **It is a diagnostic tool for desync investigations.**
- **`timeStampFormat`:** "none", "short" (default) or "full" timestamps on server RPT lines. `timeStampFormatConsole` was added in 2.20/2.21. — [Biki: Server Config File](https://community.bistudio.com/wiki/server.cfg), [Biki: Prof-Branch Change Log v2.20](https://community.bistudio.com/wiki/Arma_3:_Prof-Branch_Change_Log_v2.20) (T2 / T1, snippet)
- **`persistent = 1`:** keeps the mission running after all clients disconnect, but only for missions with base or instant respawn. It is required for `-autoInit`. — [Biki: Server Config File](https://community.bistudio.com/wiki/server.cfg) (T2, snippet). **It is functional, not a performance setting.** A persistent server with nobody on it is the case `idleFPSLimit` covers.
- **`disableVoN` / `vonCodecQuality`:**
  - `vonCodecQuality` spans 1–30 in three bands: 8 kHz (1–10), 16 kHz (11–20) and 32 kHz (21–30). The Biki lists a default of 3. — [Biki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File) (T2, snippet)
  - "higher = better sound quality, more bandwidth consumption". — [Citadel Servers wiki](https://citadelservers.com/wiki/index.php?title=Arma_3_Server_Configuration_Guide) (T5, snippet)
  - With TFAR or ACRE "you may want to disable built-in VoN". — [WabbaNode KB](https://wabbanode.com/help/arma-3/how-to-configure-voice-chat-on-your-arma-3-server) (T5, snippet)
  - The allowed maximum has changed over time: it was once 10, then 30. — [BI forums: Setting voncodecquality to 30 crash](https://forums.bohemia.net/forums/topic/159861-setting-voncodecquality-to-30-crash/) (T4, snippet, old)
  - No source measured VoN's server CPU or bandwidth load.
- Panels already expose these keys: CubeCoders AMP's Arma 3 template marks `persistent`, `timeStampFormat`, `enablePlayerDiag`, `callExtReportLimit`, `steamProtocolMaxDataSize`, `disableVoN`, `vonCodec`, `vonCodecQuality`, `loopback`, `upnp` and the `AdvancedOptions` keys as panel-managed placeholders. — [CubeCoders AMPTemplates arma3server.cfg](https://github.com/CubeCoders/AMPTemplates/blob/main/arma3server.cfg) (read). Cited only as proof that panels expose these keys, not for what they do.
- Writing the log file has a cost: "each time the game writes to the file it causes a tiny delay". The source is a client-side forum guide (est. 2013) recommending `-nologs`, and it gives no number. — [BI forums: ARMA3 Performance and Problem Guide](https://forums.bohemia.net/forums/topic/152384-arma3-performance-and-problem-guide/) (T4, snippet, est. 2013 / likely pre-64-bit)

### Inferences
- **"Maximum" preset values for these keys.** None of these values is backed by a measured FPS gain; they are judgement calls on reducing overhead.

| Key | Suggested "Maximum" value | Why |
|---|---|---|
| `headlessClients[]` / `localClient[]` | `127.0.0.1` when the panel runs HCs | Enables HCs. This is the only key here with a real performance role. |
| `idleFPSLimit` (2.22+) | Keep the default 30 until tested. Lower (for example 10) only after verifying both unknowns. | Frees host CPU while an instance is empty, which matters when several instances or HCs share the box. No effect with players on. **Unknown 1:** whether a connected HC counts as a "player". If it does not, an HC-only server would idle-cap while AI keep running. **Unknown 2:** what a `persistent = 1` mission with live AI does at a low cap. |
| `enablePlayerDiag` | 0 | Diagnostic logging. Turn it on only while investigating desync. |
| `AdvancedOptions.logObjectNotFound` | false | Cuts RPT spam on modded servers. Any gain is unmeasured. |
| `AdvancedOptions.skipDescriptionParsing` | true (optional) | Faster mission list only. Costs friendly mission names in the list. |
| `AdvancedOptions.queueSizeLogG` | 0 normally | Diagnostic. Set it only while using `#monitor` to hunt desync. |
| `timeStampFormat` | "short" | Negligible either way. Keep timestamps for troubleshooting. |
| `callExtReportLimit` | default 1000 | Keep it, because it flags slow extensions. |
| `steamProtocolMaxDataSize` | 2048–4096 for big mod lists | Launcher mod-list display only. Not a performance setting. |
| `disableVoN` | 1 if TFAR or ACRE is in the mod set | Commonly advised functional choice (one host KB). Any bandwidth or CPU saving is unmeasured. |
| `persistent` | per mission design | Not a performance setting. |

- **Consolidated split of what a panel can set automatically and what needs a person** (covers all sections):
  - **Panel can set automatically:**
    - `server.cfg`: HC whitelists, `idleFPSLimit`, logging keys, `disableVoN`, `steamProtocolMaxDataSize`.
    - `launch`: `-limitFPS`, `-cpuAffinity`/`-cpuCount` per instance; also `-loadMissionToMemory` (section 3).
    - Starting HC processes.
    - Setting a process priority class and affinity when the panel spawns the process.
  - **Admin action on the host (panel can only advise, or run with explicit admin opt-in):**
    - Windows power plan and core parking;
    - antivirus exclusions;
    - storage placement (SSD/NVMe);
    - NIC settings;
    - how many instances share the box.
  - **Mission-maker or mod-settings action:**
    - turning on the AI transfer mechanism (ACE headless, or a Werthles/ZHC-type module);
    - AI counts;
    - Dynamic Simulation;
    - garbage collection;
    - script efficiency;
    - ACE feature toggles such as advanced ballistics and fragmentation.

### Gaps
- No measurement found for any of these keys' effect on server FPS or desync.
- The default of `logObjectNotFound` on current builds was not confirmed (on or off by default).
- The exact version history of `idleFPSLimit` and the empty-server 30 FPS cap is unresolved: 2.20 behaviour versus the 2.22 key.
- Whether connected headless clients count as "players" for the idle cap is unknown. So is how the cap interacts with a `persistent` mission that still has AI running.
- No measurement of VoN's server-side cost was found, with or without TFAR/ACRE.

---

## 3. Windows host tuning: power plan, process priority, CPU affinity and pinning, CPU parking, NIC, antivirus, SSD/NVMe, several servers on one box

### Takeaway
Advice on Windows host tuning is mostly old and anecdotal. The consistent point, from the Biki plus Epoch and host sources, is that the server simulates largely on one main thread, so single-core clock speed matters most. Power plans and core-parking tweaks that keep cores at full clock are the logical consequence of that, but the sources are from the Windows 7 era. Pinning each instance (server and each HC) to its own cores is the recurring advice for running several processes on one box, from the Biki, BI forums and Epoch. The Biki warns that careless affinity can hurt. No source was found on antivirus exclusions, NIC tuning or NVMe.

### Cited Findings
- The operating system will generally schedule threads efficiently, but manual CPU allocation is possible. Affinity can be assigned automatically from a batch file with `CMD /AFFINITY HEXvalue`. Performance may suffer if this is done carelessly. — [Biki: Arma 3 Dedicated Server](https://community.bistudio.com/wiki/Arma_3_Dedicated_Server) (T2, snippet; set by: `host OS` / `launch`)
- `-enableHT` makes the game use logical cores as well. It is ignored when `-cpuCount` or `-cpuAffinity` is given. — [Biki: Startup Parameters](https://community.bistudio.com/wiki/Arma_3:_Startup_Parameters) (T2, snippet; set by: `launch`)
- Admins launch each server with `start "name" /HIGH /affinity <hex>` (some use `/realtime`) so that each instance gets its own pair of cores. — [BI forums: How to set CPU affinity on startup?](https://forums.bohemia.net/forums/topic/177452-how-to-set-cpu-affinity-on-startup/) (T4, snippet, about 2015)
- When splitting a box between servers, use `-cpuCount` to give each one its share of cores. A single instance on a machine should not need affinity changes. — [Epoch Mod forum: Specify CPU load for your server or servers](https://epochmod.com/forum/topic/31341-specify-cpu-load-for-your-server-or-servers/) (T4, snippet, about 2015)
- The game "is processor clock dependent first". — [Epoch Mod forum: How to optimize server-FPS](https://epochmod.com/forum/topic/30663-how-to-optimize-server-fps-and-server-performance/) (T4, snippet, about 2014)
- Arma 3 is CPU-bound on a small number of cores, not bandwidth-bound. Most work happens on one or two threads, so core speed beats core count. On hybrid (P-core/E-core) CPUs, use `-cpuCount` or `-cpuAffinity` to keep the server on the fast cores. — page not pinned; one of [GameTeam](https://gameteam.io/blog/arma-3-dedicated-server-guide/), [Primcast](https://primcast.com/workloads/arma-3-dedicated-server), [vmheaven](https://vmheaven.io/blog/guides/arma-3-server) or [WinServers](https://winservers.net/blog/230-optimize-your-arma-3-windows-server-fps-and-tickrate-tuning) (T5, snippet)
- **Power plan:** set the machine's plan to "Balanced" or "High Performance". — [BI forums: ARMA3 Performance and Problem Guide](https://forums.bohemia.net/forums/topic/152384-arma3-performance-and-problem-guide/) (T4, snippet, est. 2013, client-oriented, likely pre-64-bit)
- **Core parking:** one user found half their cores parked, said power settings alone did not fix it, and disabled parking through the registry. — [Steam guide: Arma 3 Performance guide (AMD FX)](https://steamcommunity.com/sharedfiles/filedetails/?id=875262519) (T4, snippet, about 2017, client-side). Microsoft's description of core parking is for Windows 7 / Server 2008 R2. — [Microsoft: Core Parking](https://learn.microsoft.com/en-us/previous-versions/windows/hardware/design/dn613899(v=vs.85)) (Windows 7 era)
- **Several servers on one box:**
  - Hosts say yes, use distinct ports "100 ports apart" (for example 2302 and 2402). — search summary over [Primcast](https://primcast.com/workloads/arma-3-dedicated-server) / [GameTeam](https://gameteam.io/blog/arma-3-dedicated-server-guide/) (T5, snippet; which page said it was not pinned)
  - An old thread reports that multiple instances were once restricted on the DEV branch. Its status on current builds is unknown. — [BI forums: No more multiple servers on the same machine](https://forums.bohemia.net/forums/topic/145002-no-more-multiple-servers-on-the-same-machine-wtf/) (T4, snippet, est. 2013)
- **`-loadMissionToMemory`** is suggested to save server CPU. — page not pinned; one of [GTXGaming](https://www.gtxgaming.co.uk/arma-3-server-guide/), [GameTeam](https://gameteam.io/blog/arma-3-dedicated-server-guide/), [low.ms](https://low.ms/knowledgebase/arma-3-troubleshooting) or [WinServers](https://winservers.net/blog/230-optimize-your-arma-3-windows-server-fps-and-tickrate-tuning) (T5, snippet; set by: `launch`)
- **Large pages / custom allocator:** on Windows, custom memory allocators "may give performance gains, for example with Large Pages support". — [Biki: Custom Memory Allocator](https://community.bistudio.com/wiki/Arma_3:_Custom_Memory_Allocator) (T2, snippet; set by: `launch` plus `host OS` privilege for large pages)
- **Antivirus exclusions, NIC tuning (interrupt moderation, RSS and similar), SSD/NVMe placement:** no source found. See Gaps.

### Inferences
- **Panel-settable:**
  - Process priority class: set to High when the panel spawns the server and HC processes.
  - Per-process affinity or `-cpuAffinity`: give the server its own physical cores, avoid E-cores, and give each HC its own core or cores.
  - Starting multiple instances on separate port blocks.
  - **Avoid "Realtime" priority in a preset.** No source measured a benefit, and the Biki warns that careless CPU allocation can hurt.
- **Admin-only, or an explicit opt-in because it changes system settings:**
  - Windows power plan (High or Ultimate Performance);
  - disabling core parking;
  - Defender/AV exclusions for the server folder, mods and RPT/profile paths;
  - placing the server and mods on SSD/NVMe;
  - NIC driver settings.
  Present these as guidance text, not as automatic actions.
- **Why the power plan probably helps:** clock speed is the bottleneck because of the single main thread, so keeping cores out of deep idle and low clocks is the logical lever. The direct evidence, though, is Windows 7-era and client-oriented. Present it as "commonly advised, low risk" and not as measured.
- **Avoiding contention:** on a 6+ core box running a server plus 2–3 HCs, the main risk is two heavy processes landing on the same physical core or its SMT sibling. Explicit pinning, with one physical core per process, is the defensible "Maximum" behaviour.

### Gaps
- No current (Windows 10/11, Arma 2.x) measurement of the effect of power plan, core parking or priority on server FPS was found.
- No source was found on antivirus exclusions, NIC settings or NVMe for Arma servers. I am not filling this with generic Windows advice.
- Whether 2.20/2.22 changed how the server picks cores on hybrid CPUs (the history of `-cpuCount`) was not verified. One snippet claimed `-cpuCount` "is available from 2.20", which conflicts with Epoch threads from about 2015 that already use it. It is treated as unreliable.

---

## 4. Mission and mod-side causes of low server FPS that no config fixes

### Takeaway
The Biki and community admins agree that AI and object count is the main lever on server FPS, and that it belongs to the mission-maker or Zeus. The mission-side tools are:
- Dynamic Simulation, which freezes distant AI and objects;
- garbage collection of bodies and wrecks;
- keeping AI on HCs.
ACE Advanced Ballistics is the most-reported ACE performance cost, but the reports date from 2015–2017 and none isolates server FPS. No current evidence was found on the server cost of `ace_medical` or `ace_frag`.

### Cited Findings
- Fewer objects means more FPS, and the object count includes AI, vehicles and simple objects. The page also advises lowering the Dynamic Simulation threshold, considering an HC, using garbage collection, running mission logic server-side, and setting triggers server-only to save client resources. — [Biki: Mission Optimisation](https://community.bistudio.com/wiki/Mission_Optimisation) (T2, snippet; set by: `mission`)
- Dynamic Simulation freezes distant AI and objects. Its aim is to let designers "increase the number of objects and AI units … without destroying game performance". Activation distances must be tuned per scenario so the freeze and unfreeze stay hidden from players. Profile it at runtime with `diag_dynamicSimulationStart` and `diag_dynamicSimulationEnd`. — [Biki: Arma 3 Dynamic Simulation](https://community.bistudio.com/wiki/Arma_3:_Dynamic_Simulation) (T2, snippet; set by: `mission`)
- In Eden, the "Is Moving" multiplier scales the activation distance of moving entities. Garbage collection has a Limit (maximum dead bodies) and a Min Delay (how long a body stays once the limit is exceeded). — [Biki: Eden Editor: Setting Attributes](https://community.bistudio.com/wiki/Eden_Editor:_Setting_Attributes) (T2, snippet; set by: `mission`)
- "AI count (how many) is the quickest and sure way to impact server performance." — page not pinned; one of [Steam 1696048879955628901](https://steamcommunity.com/app/107410/discussions/0/1696048879955628901) or [Steam 558751660819089976](https://steamcommunity.com/app/107410/discussions/0/558751660819089976/?l=german) (T4, snippet)
- A host guide suggests reducing server-side view distance in busy missions. — page not pinned; one of [GTXGaming](https://www.gtxgaming.co.uk/arma-3-server-guide/), [GameTeam](https://gameteam.io/blog/arma-3-dedicated-server-guide/), [low.ms](https://low.ms/knowledgebase/arma-3-troubleshooting) or [WinServers](https://winservers.net/blog/230-optimize-your-arma-3-windows-server-fps-and-tickrate-tuning) (T5, snippet; set by: `mission`)
- Fewer AI, an HC or a faster core help; more RAM does not. Another host guide contradicts this by suggesting more RAM when the memory graph fills up. — page not pinned; one of [low.ms](https://low.ms/knowledgebase/arma-3-troubleshooting), [vmheaven](https://vmheaven.io/blog/guides/arma-3-server), [GTXGaming](https://www.gtxgaming.co.uk/arma-3-server-guide/) or [XGamingServer](https://xgamingserver.com/arma-3-server-hosting) (T5, snippet)
- **ACE Advanced Ballistics:**
  - A 4-player dedicated-server test saw FPS fall from about 60 to 15 after firing a few magazines, with no recovery; disabling AB kept FPS stable. The issue was milestoned for 3.3.1, so it was presumably addressed. — [ACE3 #2456](https://github.com/acemod/ACE3/issues/2456) (T4, snippet, 2015)
  - "Having the Adv. Bal. module enabled will cut your FPS in half even when not shooting" on ACE 3.6.2. It does not say whose FPS was measured, client or server. — [ACE3 #4386](https://github.com/acemod/ACE3/issues/4386) (T4, snippet, about 2016–2017)
  - An ACE versus vanilla benchmark issue found ACE costs more FPS than vanilla, with AB as a likely contributor. — [ACE3 #823](https://github.com/acemod/ACE3/issues/823) (T4, snippet, 2015)
  - A forum reply says ACE's base cost "shouldn't be that severe … unless there's ACE Advanced Ballistics on". — search summary for the HC/ACE query (T4, snippet; [Steam Troubleshooting: Extremely low fps with mods enabled](https://steamcommunity.com/app/107410/discussions/1/350543841947974911/) is the likely page, not pinned)
- The current ACE Advanced Ballistics and Fragmentation doc pages say nothing about performance, which machine computes the effect, or tuning settings. — [ACE3 docs: advanced-ballistics](https://github.com/acemod/ACE3/blob/master/docs/wiki/feature/advanced-ballistics.md), [ACE3 docs: frag](https://github.com/acemod/ACE3/blob/master/docs/wiki/feature/frag.md) (T3, read)
- ACE changelogs mention medical performance work and "frag cleanup and performance improvements" (2017), with no figures. — [ACE Workshop changelog p.5](https://steamcommunity.com/sharedfiles/filedetails/changelog/463939057?p=5), [p.7](https://steamcommunity.com/sharedfiles/filedetails/changelog/463939057?p=7) (T3, snippet)
- ACE headless notes that frequent locality changes cause desync, so it has a minimum delay between transfers. — [ACE3 docs: headless framework](https://github.com/acemod/ACE3/blob/master/docs/wiki/framework/headless-framework.md) (T3, read; set by: `mod settings`)
- `callExtReportLimit` exists because slow server-side extensions stall the server. — [Biki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File) (T2, snippet). This points to extensions (databases, loggers, stats) as a mission/mod-side cause.

### Inferences
- The preset's guidance should say plainly that **no server setting fixes**:
  1. too many active AI, or AI left on the server or the Zeus machine instead of HCs;
  2. Dynamic Simulation not enabled or badly tuned on placed AI, vehicles and objects;
  3. no garbage collection, so bodies and wrecks pile up over a long op;
  4. heavy scripts or loops and slow extensions;
  5. mass Zeus spawning in one burst, where each transfer is a locality change;
  6. ACE features with a per-projectile cost (Advanced Ballistics, fragmentation).
  Point 6 is a "test it on your mod set" item, not an established fact on current ACE.
- **For Zeus-run ops:** Zeus operators should spawn in waves, enable Dynamic Simulation on spawned groups where the tools allow it, and clean up bodies and wrecks. The HC transfer mechanism must be on before the op starts.

### Gaps
- No current (ACE 3.17+/3.18+, Arma 2.20) measurement of the server-side cost of `ace_medical` (including medical AI), `ace_frag` or `ace_advanced_ballistics` was found.
- The search did not establish which machine (shooter, server or HC) carries the AB and frag computation on current ACE. A read of ACE source or a test would settle it.
- No current source quantified the cost of common milsim mods on server FPS. Candidates include LAMBS Danger, ALiVE, TFAR/ACRE server plugins and OCAP-style recorders.

---

## 5. How admins measure: #monitor, server FPS targets, and config problem versus mission problem

### Takeaway
`#monitor` (logged-in admin, for example `#monitor 5`) is the standard tool. The server is capped at 50 FPS, and the Biki calls under 20 FPS overloaded. Community and hosts converge on these bands:

| Server FPS | Meaning |
|---|---|
| About 15 | Bare minimum |
| 30+ | Comfortable target in heavy fights |
| Sustained 10–15 | Mission too heavy |

Reported desync while server FPS stays healthy points at network or players. Desync together with low server FPS points at load, which usually comes from the mission.

### Cited Findings
- The server never runs above 50 FPS. Below 20 FPS "you can consider the server overloaded". Use the admin command `#monitor` to watch resource use. — [Biki: Arma 3 Dedicated Server](https://community.bistudio.com/wiki/Arma_3:_Dedicated_Server) (T2, snippet)
- About 15 server FPS is "the bare minimum that you ever want to see". Another poster aims for 30+ for headroom. — [BI forums: Server FPS Recommendation](https://forums.bohemia.net/forums/topic/209091-server-fps-recommendation/) (T4, snippet, about 2017)
- "everything 15FPS or higher will leave little to no issues". — [Epoch Mod forum: How to optimize server-FPS](https://epochmod.com/forum/topic/30663-how-to-optimize-server-fps-and-server-performance/) (T4, snippet, about 2014)
- Simulation lag is generally felt below about 15 server FPS, though some tolerate down to 5. — page not pinned; a Steam guide among [Steam 622954302095460190](https://steamcommunity.com/app/107410/discussions/0/622954302095460190), [Workshop discussion 378941393/1649918058741392781](https://steamcommunity.com/workshop/filedetails/discussion/378941393/1649918058741392781) and [Workshop discussion 378941393/458607699623230154](https://steamcommunity.com/workshop/filedetails/discussion/378941393/458607699623230154) (T4, snippet)
- Aim for 30+ server FPS in busy fights: "brief drops are fine, constant 10–15 is not". — [RedSwitches guide](https://www.redswitches.com/blog/arma-3-dedicated-server-setup-guide/) (T5, snippet)
- 30–45 FPS is typical for a busy server, and 15–20 means struggling. Use `#monitor 5` or `#monitor 10`. — [FREAKHOSTING KB](https://help.freakhosting.com/games/arma-3/monitoring-server-fps) (T5, snippet)
- Log in with `#login <password>`, then `#monitor 5` shows server FPS and memory every 5 s. — page not pinned; one of [low.ms](https://low.ms/knowledgebase/arma-3-troubleshooting), [GTXGaming](https://www.gtxgaming.co.uk/arma-3-server-guide/) or [vmheaven](https://vmheaven.io/blog/guides/arma-3-server). The `#monitor 5` usage is also on the [FREAKHOSTING KB](https://help.freakhosting.com/games/arma-3/monitoring-server-fps) (T5, snippet)
- Desync or rollback can come from connectivity, but also from an overloaded server (under 10 FPS). Check which it is before changing settings. — [BI forums: Dedicated Server desync situation](https://forums.bohemia.net/forums/topic/230061-dedicated-server-desync-situation/) (T4, snippet, about 2020)
- `enablePlayerDiag = 1` logs each player's bandwidth and desync every 60 s. `queueSizeLogG`, with `#monitor` running, dumps the message data of any player whose guaranteed queue goes above the threshold. — [Biki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File) (T2, snippet)
- An empty server sits at 30 FPS by design: the 2.20 behaviour, or `idleFPSLimit` in 2.22. — sources in section 2. **A low FPS reading on an idle server is therefore not a fault.**
- `diag_dynamicSimulationStart` and `diag_dynamicSimulationEnd` profile Dynamic Simulation. — [Biki: Arma 3 Dynamic Simulation](https://community.bistudio.com/wiki/Arma_3:_Dynamic_Simulation) (T2, snippet)

### Inferences
- **Suggested guidance thresholds:** these are a synthesis of the Biki's "<20 overloaded" and the community and host bands, not an official BI spec.

| Server FPS during a fight | Rating |
|---|---|
| 40–50 | Healthy |
| 30–40 | Fine |
| 20–30 | Watch |
| Below 20 | Overloaded (Biki) |
| Below 10–15 sustained | Players will feel AI and vehicle lag and desync |

- **How to tell a config problem from a mission problem:**

| Symptom | Likely cause |
|---|---|
| Server FPS high (40–50) with players reporting desync or rubber-banding | Network or individual players. Turn on `enablePlayerDiag` or `queueSizeLogG` to find them. Then look at basic.cfg/bandwidth (another researcher's scope) or the players' connections. |
| Server FPS falls as AI or objects rise, and recovers when AI is deleted, frozen or moved to HCs | Mission load. Config will not fix it. |
| Server FPS low at mission start with few AI | Heavy init scripts, extensions or the mod set. Check the RPT for `callExtReportLimit` warnings and script errors. |
| HCs connected but server FPS unchanged in AI-heavy scenes | The transfer mechanism is not running (section 1). |
| Low FPS with nobody connected | Expected: the idle cap. |

- **What a panel could show automatically:** parsed `#monitor` output or RCon/RPT-derived server FPS, a colour band using the thresholds above, and HC connection status. **Interpretation stays with the admin.**

### Gaps
- The exact field list of `#monitor` output on 2.20/2.22 (FPS, memory, bandwidth in/out, guaranteed and non-guaranteed queues) could not be confirmed from a primary source.
- No BI-official FPS target beyond "<20 overloaded" and the 50 FPS cap was found.
- No source was found on measuring HC FPS from the server side, or on per-HC monitoring tools.

---

## 6. Which popular "performance tips" are myths or outdated on current versions?

### Takeaway
Apart from the Biki's warning on raising MaxMsgSend, no source directly debunks any tip. Several common tips, however, rest only on pre-64-bit, Windows 7-era or 2013–2017 sources, or on host marketing, and have no current measurement. They should be presented as "dated / unverified on 2.x" and not as proven.

### Cited Findings
- **`-maxMem=2047` "for performance":**
  - The forum guide (est. 2013) recommends `-maxmem=2047` as a fix for profile corruption, not for speed. — [BI forums: ARMA3 Performance and Problem Guide](https://forums.bohemia.net/forums/topic/152384-arma3-performance-and-problem-guide/) (T4, snippet, est. 2013, likely 32-bit era)
  - The Biki describes `-maxMem` as a soft limit: the game "will do everything in its power to not cross this limit". It mentions a Linux bug before 2.14 that read values such as 4096 as negative, hence values like 2047, 4095 or 8191. — [Biki: Startup Parameters](https://community.bistudio.com/wiki/Arma_3:_Startup_Parameters) (T2, snippet)
  - Status: **dated / not a performance tip.**
- **`-exThreads`, `-enableHT`, `-malloc`, `-hugePages`:**
  - The Biki documents what they do. `-enableHT` is overridden by `-cpuCount`/`-cpuAffinity`, and the Linux server always uses the OS allocator. — [Biki: Startup Parameters](https://community.bistudio.com/wiki/Arma_3:_Startup_Parameters) (T2, snippet)
  - Host guides describe them with no benchmarks, and community command lines use them without testing. — [bestarmahosting guide](https://bestarmahosting.com/guides/how-to-configure-your-arma-3-server-all-options-explained/), [BI forums: Server start: command line not working](https://forums.bohemia.net/forums/topic/204717-server-start-command-line-not-working/) (T5/T4, snippet)
  - `-cpuCount`, `-malloc`, `-exThreads` and `-maxMem` do not work from a `.par` file and must go on the command line. — [Biki: Startup Parameters](https://community.bistudio.com/wiki/Arma_3:_Startup_Parameters) (T2, snippet)
  - Status: **unverified gains.** This is another researcher's launch-option scope; noted here only because it is relevant to myths.
- **"Raise MaxMsgSend / bandwidth values to fix desync":** the Biki says higher MaxMsgSend values "can create Desync problems if the server's available processing and/or bandwidth can not support the higher value" (default 128). A host guide warns that pushing values past what the network can carry makes desync worse. — [Biki: Arma 3 Dedicated Server](https://community.bistudio.com/wiki/Arma_3:_Dedicated_Server) (T2, snippet); host guide not pinned, one of [GTXGaming](https://www.gtxgaming.co.uk/arma-3-server-guide/), [GameTeam](https://gameteam.io/blog/arma-3-dedicated-server-guide/), [low.ms](https://low.ms/knowledgebase/arma-3-troubleshooting) or [WinServers](https://winservers.net/blog/230-optimize-your-arma-3-windows-server-fps-and-tickrate-tuning) (T5, snippet). Status: **"more is better" is contradicted by the Biki.** Details belong to the basic.cfg researcher.
- **"Add RAM to fix server FPS":** one host guide says more RAM does not help, and fewer AI, an HC or a faster core does. Another host suggests adding RAM when memory fills. — pages not pinned; among [low.ms](https://low.ms/knowledgebase/arma-3-troubleshooting), [vmheaven](https://vmheaven.io/blog/guides/arma-3-server), [GTXGaming](https://www.gtxgaming.co.uk/arma-3-server-guide/) and [XGamingServer](https://xgamingserver.com/arma-3-server-hosting) (T5, snippet). Status: **contested.** Neither side gave evidence.
- **Realtime priority, registry core-parking hacks:** they appear in about 2015 forum batch files and about 2017 client-side Steam guides. No measurement was found. The Biki warns that careless CPU allocation can hurt. — [BI forums: How to set CPU affinity on startup?](https://forums.bohemia.net/forums/topic/177452-how-to-set-cpu-affinity-on-startup/), [Steam AMD FX guide](https://steamcommunity.com/sharedfiles/filedetails/?id=875262519), [Biki: Arma 3 Dedicated Server](https://community.bistudio.com/wiki/Arma_3_Dedicated_Server) (T4/T2, snippet). Status: **dated / unverified.**
- **"ACE Advanced Ballistics halves your FPS":** this rests on 2015–2017 issues (#2456 was milestoned for a fix in 3.3.1). The reports are not server-specific. — [ACE3 #2456](https://github.com/acemod/ACE3/issues/2456), [ACE3 #4386](https://github.com/acemod/ACE3/issues/4386) (T4, snippet). Status: **dated. Re-test on current ACE.**
- **"An HC takes you from 10 to 50 FPS":** a host marketing claim. — page not pinned; one of the host pages listed in section 1 (T5, snippet). Status: **plausible direction, unsupported magnitude.**
- **"My empty server only runs at 30 FPS, something's wrong":** this is intended behaviour on current builds (2.20 cap, `idleFPSLimit` in 2.22). — [Biki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File) (T2, snippet). Status: **not a problem.**
- **Werthles' Headless Module as the default HC answer:**
  - It was last updated October 2020, and the fetched Workshop page showed a removal/incompatible notice. — [Steam Workshop: Werthles' Headless Module](https://steamcommunity.com/sharedfiles/filedetails/?id=510031102) (T3, read)
  - ZHC users report Zeus spawns not transferring. — [Steam comments 2450921295](https://steamcommunity.com/sharedfiles/filedetails/comments/2450921295) (T4, snippet)
  - Status: **check current maintenance status before recommending either module.**
- **`-nologs` for performance:** "each time the game writes to the file it causes a tiny delay". — client guide (est. 2013), [BI forums: ARMA3 Performance and Problem Guide](https://forums.bohemia.net/forums/topic/152384-arma3-performance-and-problem-guide/) (T4, snippet). Status: **unquantified.** On a server it removes the RPT you need for troubleshooting.

### Inferences
- The "Maximum" preset should **not** do any of the following:
  - set `-maxMem=2047`, which caps a 64-bit server for no reason;
  - set Realtime priority;
  - raise network message limits blindly;
  - disable logs entirely.
- It should present `-exThreads`/`-enableHT`/`-malloc`/hugepages only where another researcher found current evidence. Otherwise leave them at engine defaults.
- The guidance text can name these as "common tips with no current evidence" so admins do not chase them.

### Gaps
- No BI developer statement (Dwarden or others) on any of these tips was retrieved. BI forums returned 403 and the Biki was snippet-only.
- No Reddit evidence: reddit.com is blocked for search and fetch here.
- No controlled before-and-after benchmark of any listed tip on 2.20/2.22 was found.
