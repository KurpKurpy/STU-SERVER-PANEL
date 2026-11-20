# Arma 3 servers choke on CPU, not bandwidth

An Arma 3 dedicated server on 2.20/2.22 runs best when you leave most settings at Bohemia's defaults, add `-loadMissionToMemory`, and move AI off the server. Scaling basic.cfg to a 1 Gbit pipe does not help. Bohemia's 2.20 guidance tells admins to stop setting `-exThreads` and `-cpuCount`, and says its own tests found `-enableHT` slower. Its basic.cfg page warns that an over-optimistic MinBandwidth adds lag and CPU load. The panel's "75% of upload" rule is right only for a link the server can actually fill. On 1 Gbit it sets MinBandwidth to 750 Mbit/s, about 19 times what 80 players need at the community estimate of 0.5 Mbit/s each. MaxMsgSend is the one value where the sources disagree. Bohemia's text says to raise it on high-upload servers. The only data from units like yours, seven communities in a single gist, settled on 48–64, and two of them (16AA and Karmakut) reported that 128 and above caused yellow chains (a network-lag warning) that did not clear. The recommended Maximum preset therefore sets MaxMsgSend 64 and MaxSizeNonguaranteed 1024 as a pair that can be reverted together. It also sets MinBandwidth 40,000,000, MaxBandwidth 1,000,000,000, `-limitFPS=100` and `-loadMissionToMemory`, and leaves the other eleven settings unset. The evidence is thin: nobody, Bohemia included, has published before-and-after `#monitor` numbers for any of these values on a current build, and Reddit gave only thread titles up to 12 years old. Headless clients that actually receive the AI, including Zeus spawns, will do more for server FPS than any value in this report.

## Bohemia's full basic.cfg page backs a cautious approach and corrects several snippet-based claims

The research notes were built while every Bohemia wiki and forum page returned HTTP 403. One researcher read wiki pages through Wayback Machine snapshots. Another worked only from search-engine snippets. The saved copy of **Basic Server Config File – Arma 3 (last edited 22 June 2025)** is now the authoritative Bohemia source on basic.cfg ([BI wiki, oldid 373717](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)). It confirms the engine defaults the notes relied on:

| Setting | Default |
|---|---|
| MaxMsgSend | 128 |
| MaxSizeGuaranteed | 512 |
| MaxSizeNonguaranteed | 256 |
| MinBandwidth | 131072 |
| MinErrorToSend | 0.001 |
| MinErrorToSendNear | 0.01 |
| sockets maxPacketSize | 1400 |
| MaxBandwidth | listed as unknown |

The page also confirms Bohemia's only worked recommendation: **MaxMsgSend 256 and MinBandwidth 768000 for a server with 1024 kbps of upload**, given outright. Only its further advice is conditional: raise both values more if the server is not using bandwidth it could use. It also confirms the warning that a too-optimistic MinBandwidth increases lag and CPU load, because messages are sent and then discarded. Where the full page differs from the snippet-based notes, the table below records the outcome.

| Claim in the notes | What the full page says | Verdict |
|---|---|---|
| The wiki's example file uses MinBandwidth 800000, MaxBandwidth 25000000 and MaxMsgSend 384 (search summary) | The example file uses MinBandwidth 131072, MaxBandwidth 10000000000, MaxMsgSend 128, MaxSize 512/256, MinErrorToSend 0.001/0.01 and MaxCustomFileSize 0 | **Corrected.** Those figures are not on this page. |
| The basic.cfg page warns that a higher MaxMsgSend causes desync | This page says only that raising it can reduce lag on high-upload servers. The desync warning is on Bohemia's Dedicated Server page, which says values above 128 can cause desync when CPU or bandwidth cannot keep up ([BI wiki: Dedicated Server, Wayback](http://web.archive.org/web/20260617130649/https://community.bohemia.net/wiki/Arma_3:_Dedicated_Server)) | **Wrong page cited.** The warning itself still stands. |
| A MaxBandwidth above 2,147,483,647 is a harmful out-of-range template value | Bohemia's own example uses 10,000,000,000. The page also carries a notice that some entries accept numbers written as strings, which matches the 2.20 changelog line about basic.cfg network parameters ([SPOTREP #00118](https://dev.arma3.com/post/spotrep-00118)) | **Corrected.** The ≤ 2,147,483,647 limit is the researchers' own precaution, not a Bohemia rule. |
| Raising maxPacketSize is a fix for a specific problem, not a tuning knob (researcher inference) | Change it only if a router or ISP enforces smaller packets and you have connection problems. It can be set separately for each direction | **Confirmed and strengthened.** |
| A lower MinErrorToSend adds CPU work and could worsen yellow chains (inference) | Bohemia programmer Suma: 10× lower can mean up to 10× the traffic, but the extra CPU cost should be minor | **Partly corrected.** The traffic cost is confirmed; the CPU worry is downgraded. |
| MinErrorToSendNear (notes: larger values save traffic) | Adds that a value set too high makes units appear to warp. Suma explains it is an absolute floor (his illustration is 1 cm) that applies alongside the distance-scaled MinErrorToSend | **Added.** |
| Whether MaxMsgSend must scale with unit count (open question) | Suma: one aggregate message carries logical updates for many objects | **Added.** MaxMsgSend does not need to grow with AI count. Whether it is counted per client or for the whole server is still unknown. |
| A server below 15 FPS is overloaded (2013 forum snippet) | Below 20 FPS the server can be considered overloaded | **Corrected to 20.** The page's line that the server never exceeds 50 FPS is out of date, because `-limitFPS` has existed since 2.00. |

Two other details matter for anyone tuning from this page. Its worked example for MinErrorToSend (a unit 1 km away is updated once it has drifted 50 m) does not match Suma's formula on the same page, which works out to about 2.5 m. Either way, the update threshold scales linearly with the setting and with the square of distance. The page also describes a simulation cycle as AI, movement, visibility, scripts and FSMs, then receiving and **sending network updates, all in one loop**. This is the most useful fact in the whole source base. Every basic.cfg limit applies per cycle, so a server whose cycle is slowed by ACE and AI sends less often, whatever the config says.

The saved **Headless Client page (last edited 12 February 2024)** confirms the wiring ([BI wiki, oldid 352522](https://community.bistudio.com/wiki?title=Arma_3:_Headless_Client&oldid=352522)). Headless client (HC) IPs go in `headlessClients[]`, and clients listed in `localClient[]` get unlimited bandwidth and near-zero latency. Any number of HCs can run on one machine, launched with `arma3server.exe -client -connect=…`. HCs skip signature verification, `-serverMod` does not work with `-client`, and HCs appear in `allPlayers` with `isPlayer` returning true for them. The page states plainly that missions must be changed to put AI on an HC. It says nothing about how much HCs help, how many to run, or keeping them on the same LAN. The snippet claims on those points came from the Dedicated Server page, not this one.

### Reddit offered thread titles, not answers

Four saved Reddit search pages (r/arma, r/armadev and all of Reddit) hold only post titles, ages, vote counts and Reddit's own AI summary. The summary for "arma 3 server MaxMsgSend" says the matching discussions were about hosting and connectivity, and that they **"don't support recommending a specific value"**. The on-topic threads are all old:

| Thread | Subreddit | Age shown | Comments |
|---|---|---|---|
| ["Understanding basic.cfg"](https://www.reddit.com/r/armadev/comments/2zkw7p/understanding_basiccfg/) | r/armadev | 12 years | 7 |
| ["What 'Server FPS' actually means?"](https://www.reddit.com/r/armadev/comments/4hpar5/what_server_fps_actually_means/) | r/armadev | 11 years | 4 |
| ["Dedicated Server basic.cfg help"](https://www.reddit.com/r/arma/comments/3nl0b8/dedicated_server_basiccfg_help/) | r/arma | 11 years | 1 |
| ["Arma 3 Headless client questions"](https://www.reddit.com/r/armadev/comments/d7cpab/arma_3_headless_client_questions/) | r/armadev | 7 years | 4 |

A more popular, unread thread on the difference between server and client FPS is three years old ([r/arma](https://www.reddit.com/r/arma/comments/18650em/what_the_difference_between_server_fps_and_normal/)). **No thread bodies were read**, so Reddit contributes no evidence to any value below.

## Message limits should track CPU load and player count, not the 1 Gbit pipe

Every popular template treats basic.cfg as a dial to turn up to the size of the pipe. The sources point the other way. On a heavily modded server, the bottleneck is the main simulation cycle. Values that make the engine try to send more per cycle add work to the thread that is already the constraint.

### MaxMsgSend: the one value where the sources point in opposite directions

**Recommended Maximum value: 64, paired with MaxSizeNonguaranteed 1024. Confidence: low.**

Bohemia's basic.cfg page sets the default at 128, says raising it can reduce lag on high-upload servers, and recommends 256 for a 1 Mbit server ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)). Its Dedicated Server page adds that values above 128 cause desync when CPU or bandwidth cannot support them ([BI wiki, Wayback](http://web.archive.org/web/20260617130649/https://community.bohemia.net/wiki/Arma_3:_Dedicated_Server)).

The closest evidence to this unit's profile is the "Arma useful things" gist. Seven named communities with 10–100 players, most of them on 1 Gbit lines, settled on **48–64**: 16AA (PvE/Zeus, 10–80 players), Karmakut (100 players), Digby's War Room, UNITAF, IBC, B.R.O and armasrbija. The gist reports that **128 or higher made the 16AA and Karmakut servers yellow-chain without recovering**, and that 48 caused extra lag while players were connecting ([MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)). Hosts and templates sit higher:

| Source | MaxMsgSend | Note |
|---|---|---|
| LOW.MS host guide | 384 on busy servers | Warns that higher values can starve the simulation ([LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration)) |
| LinuxGSM template | 2048 | Its own comment admits this may increase desync ([GameServerManagers](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)) |
| A 2014 Altis Life server | 2048 | Rubber-banded constantly above about 60 players ([NFOservers](https://forums.nfoservers.com/viewtopic.php?t=11674)) |
| A community gist | 640 | Claimed to match official BI servers; this is the gist author's claim, not a Bohemia statement ([veteran29 gist](https://gist.github.com/veteran29/ef08068262df808780fbcd4aeda0c03a)) |

**There is no agreement.** One gist points down. Its operator table is undated apart from one October 2023 edit, so it is older than the 2.20 and 2.22 netcode work. Bohemia's text and the host guides point up, and no source published measurements. The case for 64 rests on lopsided risks. Set too low, the cost seen in this profile was lag while players connect, which is mild, visible and easy to reverse. Set too high, the cost was yellow chains that never cleared. The trade-off is that if 2.20's multithreaded JIP processing and 2.22's improved netcode ([SPOTREP #00118](https://dev.arma3.com/post/spotrep-00118); [SPOTREP #00120](https://dev.arma3.com/post/spotrep-00120)) removed the cause, 64 gives up throughput for nothing. **If players see lag during connection waves or desync rises, revert both values of the pair to unset (128/256) together.** One hypothesis, which is not evidence: if the cap is counted for the whole server (no source confirms this), 64 packets × 1024 bytes allows twice the non-guaranteed payload per cycle of the default 128 × 256, in half as many packets.

### MaxSizeGuaranteed and MaxSizeNonguaranteed: keep the default, except for the paired change

**MaxSizeGuaranteed: leave unset (512). Confidence: high.** It is Bohemia's default, and every template, guide and the gist keeps it. Bohemia says guaranteed packets carry one-off events such as shooting, and that desync can follow if either MaxSize value exceeds maxPacketSize ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)).

**MaxSizeNonguaranteed: 1024, only as the pair with MaxMsgSend 64. Confidence: low.** Bohemia says this value governs repeated position updates, and that raising it may reduce bandwidth needs but may add lag ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)). Only the milsim gist raises it, as part of its combination ([gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)). LOW.MS and LinuxGSM keep 256 ([LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration); [GameServerManagers](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)), so agreement is one source against two. At 1024 it stays under maxPacketSize 1400, which is Bohemia's only hard rule. The panel should treat it as half of a pair: if MaxMsgSend goes back to unset, this goes back to unset too.

### Bandwidth floors should follow the player count once the link outgrows it

**MinBandwidth: 40,000,000 (40 Mbit/s). Confidence: medium-low.** The 75% rule is Bohemia's own example (768000 bps on a 1024 kbps link), and the same page warns that optimistic values cause lag and CPU load ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)). Community sources size the floor by players, not by the link. The milsim gist runs 25.6 Mbit/s, calls the setting not very relevant and says never to exceed real bandwidth. Its rule of thumb is 0.5 Mbit/s of upload per player, which it flagged in July 2024 as possibly outdated ([gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)). LinuxGSM suggests about 256 kbit/s per player ([GameServerManagers](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)), and LOW.MS calls 15 Mbit/s a sensible floor ([LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration)). In 2014, cutting an over-high 107 Mbit/s floor helped very little; a reply noted that the server estimates bandwidth by itself, and the floor only narrows that search sooner ([NFOservers](https://forums.nfoservers.com/viewtopic.php?t=11674)).

The figure of 40 Mbit/s is the per-player rule applied to the top of this unit's range: 80 players × 0.5 Mbit/s. It sits above the 25.6 Mbit/s the milsim operators ran. Agreement is broad that 75% of a gigabit is too optimistic, since Bohemia's own warning, the gist and the 2014 thread all point that way. The exact figure inside the 15–40 Mbit/s band is a judgement call. There are two caveats. Bohemia raised the default bandwidth settings in 2.06 without saying which ones ([SPOTREP #00100](https://dev.arma3.com/post/spotrep-00100)). And a snippet of the profiling branch changelog says that branch stopped using MinBandwidth in April 2025 ([Prof-Branch Change Log v2.18](https://community.bistudio.com/wiki/Arma_3:_Prof-Branch_Change_Log_v2.18)). Whether stable 2.20/2.22 followed is unconfirmed, so on current builds this field may do nothing.

Do not confuse it with the `class sockets` keys. That class has its own MinBandwidth and MaxBandwidth, in bytes per second per client. Bohemia engine programmer dedmen warned that the sockets MinBandwidth must stay below the slowest client that might ever connect ([veteran29 gist comments](https://gist.github.com/veteran29/ef08068262df808780fbcd4aeda0c03a)). The panel should never raise it automatically.

**MaxBandwidth: 1,000,000,000. Confidence: low (harmless).** Bohemia defines it as the bandwidth the server is guaranteed never to have, and its own example uses 10,000,000,000 ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)). The milsim gist says it does nothing ([gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)). LOW.MS uses it as a 100 Mbit/s cap so that one server cannot starve its neighbours on a shared host ([LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration)). The sources disagree on whether it does anything, but none reports harm. Setting it to the true line rate matches Bohemia's definition and fits in a 32-bit integer, which avoids the open question of how values written as strings are parsed.

### Error thresholds and packet size: Bohemia's defaults hold

**MinErrorToSend: leave unset (0.001). Confidence: high.** Bohemia says a smaller value makes distant units seen through scopes move more smoothly, at the cost of traffic. Suma estimates up to 10× the traffic for a 10× smaller value, with only minor extra CPU ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)). The milsim gist and LOW.MS both keep 0.001. Only LinuxGSM departs, shipping a coarser 0.01 that is worse for long-range smoothness, and it also mis-states the default as 0.003 ([GameServerManagers](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)). For sniper- or air-heavy ops, 0.0005 is a possible opt-in. It roughly doubles the traffic for distant units at most, but nobody has measured it, and it would compete for the same per-cycle packet budget as MaxMsgSend 64.

**MinErrorToSendNear: leave unset (0.01). Confidence: high.** Larger values cut traffic for nearby units, but Bohemia warns they make units warp. The setting also governs traffic from clients to the server ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)). Bohemia, the gist and LOW.MS all keep 0.01. Only LinuxGSM departs, and its 0.02 is a bad trade for close-quarters milsim.

**class sockets maxPacketSize: leave unset (1400). Confidence: high.** Bohemia says to change it only when an ISP or router enforces smaller packets ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)). The default was cut from 1490 to 1400 in Arma 2: OA 1.60 to suit unusual MTU sizes ([A2OA 1.60 notes](https://community.bistudio.com/wiki/A2OA:_Patch_v1.60)); MTU is the largest packet a network path carries without splitting it. Before the 2.18 hotfix, a changed value together with a large mod list hid the server from the server browser ([SPOTREP #00116](https://dev.arma3.com/post/spotrep-00116)). Bohemia, the LinuxGSM comment and LOW.MS agree it is not a tuning value, and the gist leaves it unset. The one documented reason to raise it is the RPT error "trying to send a too large non-guaranteed message", which dedmen cleared with 1430 ([veteran29 gist comments](https://gist.github.com/veteran29/ef08068262df808780fbcd4aeda0c03a)). Raise it to 1430 only if that error appears.

## Bohemia's 2.20 guidance retires most CPU and memory flags

### Leave core and thread detection to the engine

**`-cpuCount`: leave unset. Confidence: high.** Bohemia's 2.20 performance post says the engine detects the core count itself. It says `-cpuCount` is only for deliberately limiting cores, or for telling the game how many cores an external affinity tool allows ([OPREP 2.20](https://dev.arma3.com/post/oprep-performance-optimizations-in-220)). Since 2.20 the minimum value is 2, and setting it switches off `-enableHT` ([Startup Parameters, Wayback](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). A hand-set `-cpuCount=2` also froze an experimental build of the new job system in November 2024 before it was fixed ([profiling thread p.107, Wayback](http://web.archive.org/web/20250202170530/https://forums.bohemia.net/forums/topic/160288-arma-3-stable-server-218-profiling-performance-binary-feedback/?page=107)). Bohemia and the independent XenyxNZ guide agree, and no source argues for setting it ([XenyxNZ guide](https://github.com/XenyxNZ/Arma-3-Performance-Guide/blob/main/README.md)).

**`-exThreads`: leave unset. Confidence: high.** Bohemia now says it should not be used at all, because CPUs with four or more cores already have all extra threads enabled ([OPREP 2.20](https://dev.arma3.com/post/oprep-performance-optimizations-in-220)). Those threads mainly offload file, geometry and texture loading ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)), and a server renders nothing, so the case for setting it is weaker still. Bohemia and XenyxNZ agree, and no source argues for setting it.

**`-enableHT`: leave unset (off). Confidence: medium.** Bohemia says its tests found `-enableHT` performs worse, though this may depend on hardware ([OPREP 2.20](https://dev.arma3.com/post/oprep-performance-optimizations-in-220)). The wiki says it may slightly help or hurt and is overridden by `-cpuCount` or `-cpuAffinity` ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). The community is split. XenyxNZ leans towards on, but notes it is sometimes slower on CPUs with 8 or more cores ([XenyxNZ](https://github.com/XenyxNZ/Arma-3-Performance-Guide/blob/main/README.md)), and the milsim gist uses it without data ([gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)). There is no server-side data, since Bohemia's tests were client benchmarks. The panel should offer it as an opt-in A/B toggle, and block it alongside `-cpuCount` or `-cpuAffinity`.

### Large pages and custom allocators are unproven on servers

**`-hugePages`: leave unset. Confidence: medium.** Large pages require the Windows "Lock pages in memory" right (SeLockMemoryPrivilege) for the account that runs the server, followed by a re-logon or restart ([XenyxNZ](https://github.com/XenyxNZ/Arma-3-Performance-Guide/blob/main/README.md)). That memory is non-pageable and must be physically contiguous, so it is best grabbed at startup ([Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/memory/large-page-support)). Bohemia's wiki says the flag works with the default allocator. It also quotes Dedmen saying the scripting pool allocator ignores large pages, and it carries a community observation that a dedicated server may barely allocate through the custom allocator at all ([Custom Memory Allocator, Wayback](http://web.archive.org/web/20250428011956/https://community.bohemia.net/wiki/Arma_3:_Custom_Memory_Allocator)). Three community sources use large pages (XenyxNZ, the milsim gist and the mimalloc author), none with server measurements, and Bohemia documents the flag without endorsing it. The panel cannot verify the privilege, and no source documents what Arma does without it, so offer setup instructions and an opt-in instead.

**`-malloc`: leave unset (Bohemia's `tbb4malloc_bi`). Confidence: medium.** The October 2024 wiki revision lists `tbb4malloc_bi` (the default) and `jemalloc_bi` as the shipped allocators ([Custom Memory Allocator](http://web.archive.org/web/20250428011956/https://community.bohemia.net/wiki/Arma_3:_Custom_Memory_Allocator)). The mimalloc builds in circulation are third-party DLLs from GoldJohnKing. The author claims up to 30% higher FPS, but nobody has measured that independently. He deprecated one release because it gave no gain over TBB, and in July 2025 he flagged the v3 line as beta ([GoldJohnKing releases](https://github.com/GoldJohnKing/mimalloc/releases)). The LockPages variants in the gist's command line were aimed at large-RAM client PCs, and their own author advised against them ([release v2.1.2](https://github.com/GoldJohnKing/mimalloc/releases/tag/Arma-3-v2.1.2-20230427)). They were retired in January 2025, because current builds lock large pages automatically ([release v2.1.7-20250103](https://github.com/GoldJohnKing/mimalloc/releases/tag/Arma-3-v2.1.7-20250103)). Agreement mirrors `-hugePages`: three community sources recommend mimalloc without server data, and Bohemia endorses none. If the panel offers mimalloc, it should list the DLLs actually present in the `Dll` folder rather than hard-code a name.

### Frame cap, mission caching, memory and the experimental algorithm

**`-limitFPS`: 100. Confidence: low.** Bohemia documents a default of 50 and a range of 5–1000. The cap does nothing when it is not reached. A higher cap means network messages, simulation and AI run more often, but Bohemia says the practical effect may or may not be noticeable ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). The only measurement used custom binaries in 2017. An idle server rose from 46 to 168 FPS, while a test with 100 versus 100 AI went from 21 to 19 FPS ([Server FPS limit testing, Wayback](http://web.archive.org/web/20240421115300/https://forums.bohemia.net/forums/topic/210492-server-fps-limit-testing/)). At 100, the cap does nothing during heavy fights and allows more frequent cycles, and so more sends, when the server has headroom. That should partly offset a low MaxMsgSend, but this is an inference and has not been measured. The gist runs 500 with no supporting data, so agreement amounts to Bohemia's neutral description plus one unmeasured config.

**`-loadMissionToMemory`: set. Confidence: high.** Bohemia documents that the server keeps the mission pre-processed in memory after the first client downloads it, which saves CPU for later joins ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). The milsim gist uses it, and no source reports a downside or argues against it.

**`-maxMem`: leave unset. Confidence: high.** The engine sets it automatically, up to four-fifths of physical RAM on 64-bit Windows, and Bohemia advises using it only to fix memory problems ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). No current source argues for setting it for performance. Values like 2047 are leftovers from 32-bit and old Linux parsing bugs.

**`-bandwidthAlg=2`: leave unset. Confidence: medium.** The wiki still calls it an experimental algorithm that might be better than the default ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). Bohemia developer Dwarden described it in 2015 as just different: he said it changes how queued updates are prioritised, affects only the server and does not fix router problems ([Steam discussion](https://steamcommunity.com/app/107410/discussions/0/617330406665084468/)). The 2.22 netcode changes do not mention it ([SPOTREP #00120](https://dev.arma3.com/post/spotrep-00120)). The gist is the only source that uses it, without data, so it has no supporting measurement from anyone.

### Flags and branches to keep out of any preset

| Flag or branch | Why to keep it out |
|---|---|
| `-setThreadCharacteristics` | Bohemia warns it can freeze the whole OS on Windows Server ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). |
| `-noLogs` | Stops errors being written to the RPT, for a gain nobody has measured ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). |
| `-disableServerThread` | Meant only as a workaround for random crashes. Bohemia says it can reduce performance on multicore CPUs ([Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). |
| `-cpuAffinity`, `-cpuMainThreadAffinity` (new in 2.22) | Expert tools for keeping a server and its HCs on separate cores, or off E-cores on hybrid CPUs. They switch off `-enableHT`, and the RPT records whether they were applied ([SPOTREP #00120](https://dev.arma3.com/post/spotrep-00120); [Startup Parameters](http://web.archive.org/web/20260617124651/https://community.bohemia.net/wiki/Arma_3:_Startup_Parameters)). |
| Profiling branch | Bohemia calls it experimental and possibly unstable. It has had server-specific regressions, including a PhysX crash and crashes in the JIP queue (the join-in-progress sync for late joiners), and its big performance work reached stable in 2.20 anyway ([profiling thread p.106](http://web.archive.org/web/20241106123403/https://forums.bohemia.net/forums/topic/160288-arma-3-stable-server-218-profiling-performance-binary-feedback/?page=106&tab=comments); [OPREP 2.20](https://dev.arma3.com/post/oprep-performance-optimizations-in-220)). |

## Headless clients and mission discipline decide whether ops hold 30 FPS

Because sending happens at the end of each simulation cycle, server FPS is the real network setting. **AI and object count are the main levers on it.** Bohemia's mission-optimisation guidance points to the same tools: fewer objects, Dynamic Simulation for distant AI and objects, garbage collection, and HCs ([BI wiki: Mission Optimisation](https://community.bistudio.com/wiki/Mission_Optimisation); [Dynamic Simulation](https://community.bistudio.com/wiki/Arma_3:_Dynamic_Simulation)).

**HCs only help if something moves AI onto them.** ACE's headless module spreads AI groups round-robin across up to three HCs, and its docs call three the most effective number. It moves groups when units spawn and when an HC connects or disconnects. It skips groups that contain players or UAVs, or whose waypoints are synced to triggers. Units from vanilla support modules stop working once moved, so they must be blacklisted, and an HC disconnect triggers a full rebalance ([ACE3 headless framework](https://github.com/acemod/ACE3/blob/master/docs/wiki/framework/headless-framework.md)). ACE's docs never mention Zeus-placed units, which matters because AI placed by Zeus is reportedly local to the Zeus player's machine ([Ares #159](https://github.com/astruyk/Ares/issues/159)). Without a transfer mechanism, the Zeus player's PC and upload become the bottleneck for that AI. The Zeus-specific modules are shaky. Werthles' module was last updated in 2020, and its Workshop page showed a removal notice when fetched, although that notice may be an artefact of the fetch ([Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=510031102)). ZHC users report Zeus spawns not transferring ([Workshop comments](https://steamcommunity.com/sharedfiles/filedetails/comments/2450921295)).

**How many HCs.** A sensible start for a 6+ core box is two HCs on the same machine, each with `127.0.0.1` in both `headlessClients[]` and `localClient[]`. Add a third only if a physical core is free. This is an inference from the ACE and Bohemia wiring docs, not a benchmark. Confirm that AI actually moved, for example with `groupOwner` or ACE's transfer log, because seeing an HC connected is not proof.

**ACE's own costs.** Reports that ACE Advanced Ballistics halves FPS date from 2015–2017 and are not specific to servers ([ACE3 #2456](https://github.com/acemod/ACE3/issues/2456)). Re-test it on your own mod set rather than assume.

**Host-level measures.** Advice on power plans, process priority and core parking dates from the Windows 7 era and was written for clients. The one consistent point is that single-core clock speed matters most. These are host settings the panel cannot set, so present them as guidance only.

**Measuring.** Log in as admin and run `#monitor`. Bohemia calls a server below 20 FPS overloaded ([BI wiki](https://community.bistudio.com/wiki?title=Arma_3:_Basic_Server_Config_File&oldid=373717)), and forum veterans treat about 15 as the floor and 30 or more as comfortable ([BI forums](https://forums.bohemia.net/forums/topic/209091-server-fps-recommendation/)).

| What you see | Likely cause |
|---|---|
| Server FPS of 40 or more, but players report desync | Network or individual players. Set `enablePlayerDiag = 1` to log each player's bandwidth and desync ([BI wiki: Server Config File](https://community.bistudio.com/wiki/Arma_3:_Server_Config_File)). |
| Server FPS falls as AI rises | Mission load. No config value will fix it. |
| An empty server sits at 30 FPS | Intended. Empty servers have capped at 30 FPS since 2.20, and 2.22 adds `idleFPSLimit` to tune that ([SPOTREP #00118](https://dev.arma3.com/post/spotrep-00118); [SPOTREP #00120](https://dev.arma3.com/post/spotrep-00120)). Because HCs count as players in `allPlayers`, they may also keep a server out of the idle cap. That is an inference, not tested. |

## Conclusion

The panel's presets rest on a model in which better hardware means bigger numbers. The evidence supports a different model. Above roughly 40 Mbit/s, the link stops being the constraint. The main simulation cycle is the constraint, and every basic.cfg limit is measured against it. That is why "Maximum" ends up close to Bohemia's defaults, with one contested pair of values, and why the larger gains sit outside the panel: in HCs that actually receive AI, and in mission discipline that Zeus operators control.

The more lasting finding is how little anyone has measured. Bohemia's tuning page is built around a 1 Mbit example and decade-old developer comments. The best operator data is one gist from before the 2.20 netcode work, and Reddit offers only titles. A unit that records `#monitor` FPS and bandwidth during comparable ops, before and after changing MaxMsgSend from 64 to 128, would hold better evidence than any public source. The panel should make that kind of A/B test easy, with one value changed at a time and a one-click revert.

## Recommended Maximum preset

This preset is for a 1 Gbit, 6+ core Windows host running a heavily modded ACE/CBA, Zeus-run milsim with 40–80 players on stable 2.20/2.22. "Leave unset" means the panel writes nothing, so Arma uses its own default or auto-detection.

| Setting | Value | Reason | Confidence |
|---|---|---|---|
| MaxMsgSend | **64** | Seven comparable communities, mostly on 1 Gbit, settled on 48–64, and two (16AA, Karmakut) had yellow chains that never cleared at 128+. This is a pair with MaxSizeNonguaranteed; revert both if players lag while connecting. | Low |
| MaxSizeGuaranteed | **Leave unset (512)** | Bohemia's default, kept by every source, and safely below maxPacketSize. | High |
| MaxSizeNonguaranteed | **1024** | The other half of the gist's tested pair with MaxMsgSend 64, and still under maxPacketSize 1400. Revert together with MaxMsgSend. | Low |
| MinBandwidth | **40000000** | Sized to 80 players × 0.5 Mbit/s. 75% of 1 Gbit is the over-optimistic case Bohemia warns adds lag and CPU load. | Medium-low |
| MaxBandwidth | **1000000000** | The true line rate, matching Bohemia's definition of bandwidth the server can never exceed. No measured effect, and harmless. | Low |
| MinErrorToSend | **Leave unset (0.001)** | Bohemia's default, kept by the milsim and host sources. Lowering it multiplies traffic with no measured benefit. | High |
| MinErrorToSendNear | **Leave unset (0.01)** | Bohemia's default. Larger values make nearby units warp, which is bad for close-quarters play. | High |
| class sockets maxPacketSize | **Leave unset (1400)** | Bohemia says change it only for ISP or router packet limits. Raise it to 1430 only if the RPT reports a too-large non-guaranteed message. | High |
| -cpuCount | **Leave unset** | Bohemia's 2.20 guidance: the engine detects cores, and setting it also disables -enableHT. | High |
| -exThreads | **Leave unset** | Bohemia's 2.20 guidance: it should not be used; it is already enabled on 4+ cores. | High |
| -enableHT | **Leave unset** | Bohemia's tests found it slower, and there is no server data. Offer it only as an A/B toggle. | Medium |
| -hugePages | **Leave unset** | Needs a Windows privilege the panel cannot verify, and gives an unproven gain on servers. | Medium |
| -malloc | **Leave unset** | Bohemia's TBB allocator. mimalloc gains are third-party claims, and the LockPages builds were retired. | Medium |
| -limitFPS | **100** | Officially supported. It does nothing under heavy load and allows more frequent sends when there is headroom. 500 or 1000 has no evidence behind it. | Low |
| -loadMissionToMemory | **Set** | Bohemia documents a CPU saving for clients joining after the first download, and no downside is reported. | High |
| -maxMem | **Leave unset** | Auto-set to about 80% of RAM. Bohemia advises using it only to fix memory problems. | High |
| -bandwidthAlg | **Leave unset** | Still labelled experimental, with no measurements behind it. | Medium |

**Guidance text to show beside the preset:**

> This preset removes network and engine bottlenecks, but it cannot add CPU. If `#monitor` shows server FPS below 20 during an op, the cause is the mission: too many active AI, AI left on the server or the Zeus player's PC instead of headless clients, or heavy scripts. Run two or three headless clients with ACE headless or another transfer module, confirm that Zeus-spawned AI is actually being transferred, and use Dynamic Simulation and garbage collection. Change one value at a time and compare `#monitor` under similar load.

## Review of the panel's current built-in presets

All three presets scale Bohemia's 1 Mbit example by link size. That rule holds only where the server can fill the link, which is the home tier. Above that, MinBandwidth should follow the player count. MaxMsgSend should stay at 256, the only value Bohemia's basic.cfg page explicitly recommends and the low end of host practice, because no source supports more on a bigger pipe.

**Home connection (~10 Mbit/s upload)**

| Setting | Current | Verdict | Reason |
|---|---|---|---|
| MaxMsgSend | 256 | **Keep** | Bohemia's own recommendation for an even slower link. Low confidence, but nothing argues against it at home-server player counts. |
| MinBandwidth | 7,500,000 | **Keep** | The one tier where Bohemia's 75% rule applies as written, because the server can fill this link. |
| MaxBandwidth | 10,000,000 | **Keep** | The real line rate, matching Bohemia's definition. |
| -loadMissionToMemory | Set | **Keep** | Documented CPU saving with no downside. |

**Performance (~100 Mbit/s upload)**

| Setting | Current | Verdict | Reason |
|---|---|---|---|
| MaxMsgSend | 512 | **Change to 256** | No Bohemia text, host guide or operator report supports 512. Above home-link speeds the value should track CPU load, not upload. |
| MinBandwidth | 75,000,000 | **Change to 40,000,000** | 80 players × 0.5 Mbit/s already fits well under the link. A 75 Mbit/s floor heads towards the over-optimistic case Bohemia warns about. |
| MaxBandwidth | 100,000,000 | **Keep** | The real line rate. |
| -loadMissionToMemory | Set | **Keep** | As above. |

**Performance Enhanced (1 Gbit/s upload, 6+ cores)**

| Setting | Current | Verdict | Reason |
|---|---|---|---|
| MaxMsgSend | 1024 | **Change to 256** | Nothing supports 1024: it sits in the range only problem configs use. 256 is the only figure Bohemia's page states. Heavily modded ops should use the Maximum preset instead. |
| MinBandwidth | 750,000,000 | **Change to 40,000,000** | About 19 times what 80 players need. This is the over-optimistic case Bohemia's page warns about. |
| MaxBandwidth | 1,000,000,000 | **Keep** | The real line rate, and inside the 32-bit range. |
| -loadMissionToMemory | Set | **Keep** | As above. |
| -enableHT | Set | **Remove** | Bohemia's 2.20 tests found it slower. Offer it only as an opt-in A/B toggle. |

**Packet sizes and MinErrorToSend/MinErrorToSendNear left at Arma's defaults in all three presets: keep.** This agrees with the Maximum preset. The only departure there is MaxSizeNonguaranteed 1024, and only as a pair with MaxMsgSend 64.

**Performance Enhanced and Maximum target the same hardware.** The corrected Performance Enhanced (256 / 40 Mbit / 1 Gbit, no -enableHT) is the generic large-host preset for lighter mod sets or unknown missions. Maximum differs only by the MaxMsgSend 64 / MaxSizeNonguaranteed 1024 pair and -limitFPS=100, which are aimed at heavily modded 40–80 player ACE/Zeus ops. If the panel serves only this unit, retire Performance Enhanced and keep Maximum.
