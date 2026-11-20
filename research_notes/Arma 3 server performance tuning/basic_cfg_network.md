# basic.cfg network values for a high-bandwidth, heavily modded Arma 3 dedicated server (Windows x64, game 2.20/2.22)

Source tiers used in these notes:
- **[BI-fetched]**: an official Bohemia page I fetched and read in full (dev.arma3.com SPOTREP/SITREP posts).
- **[BI-snippet]**: Bohemia wiki (community.bistudio.com) or BI forum text seen only as search-engine snippets. Every direct fetch of community.bistudio.com, community.bohemia.net and forums.bohemia.net returned HTTP 403, and web.archive.org was blocked, so I never read these pages in full. Treat wording as approximate.
- **[Community-fetched]**: community pages I fetched (GitHub gists and repos, host KBs, Steam and NFO threads).
- **[Snippet-only]**: non-BI pages seen only as search snippets (Medium article, some host KBs).
- I found **no Reddit evidence**: Reddit search via the search engine returned no threads, and old.reddit.com could not be fetched. I also found **no published FASTER or TADST defaults**. GitHub code search was unavailable because the gh CLI is not installed.

---

## Q1. What values do people actually run on high-bandwidth servers, and what did they observe?

### Takeaway
Real-world values are spread across a 30x range (MaxMsgSend 48 to 2048), and the evidence runs against the biggest numbers. The only operator data that matches this profile (milsim/Zeus PvE, 1 Gbit, 10–100 players) comes from a single community gist. Seven named communities there converged on **MaxMsgSend 48–64**, and they report that **128 or higher made the server "yellow chain" and not recover**. Host knowledge bases suggest 256–384. The 2048 figure appears in LinuxGSM's template and in a 2014 thread whose server was rubber-banding. Nobody published #monitor numbers or measured before/after server FPS.

### Cited Findings
**Bohemia (official) statements on MaxMsgSend and MinBandwidth**
- [BI-snippet] The wiki says the biggest optimisation gains come from MaxMsgSend and MinBandwidth. Its worked example is a server with a 1024 kbps uplink: **MaxMsgSend = 256, MinBandwidth = 768000**. If the server is not using bandwidth it could use, the wiki says to try raising both — [BI wiki: Arma 3 Basic Server Config File](https://community.bistudio.com/wiki/Arma_3:_Basic_Server_Config_File); [basic.cfg alias](https://community.bistudio.com/wiki/basic.cfg)
- [BI-snippet] MaxMsgSend is the maximum number of packets (aggregate messages) sent per simulation cycle, default **128**. Higher values can cause desync if the server's CPU or bandwidth cannot support them — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg). The fetched GitHub copy of the wiki-style descriptions says the same: raising it "can decrease lag on high upload bandwidth servers" — [VincentDercourt/arma3 basic.cfg](https://raw.githubusercontent.com/VincentDercourt/arma3/master/basic.cfg)
- [BI-snippet] The wiki's example file block reportedly uses MinBandwidth=800000, MaxBandwidth=25000000, MaxMsgSend=384. The search engine described this as an example, not a recommendation — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg) (from a search summary; I could not verify it on the page)

**Milsim/Zeus operator data (closest match to the target profile)**
- [Community-fetched] The "Arma useful things" gist marks MaxMsgSend **64** as "IMPORTANT". It warns that **128 or more** caused the 16AA and Karmakut servers to yellow-chain and not recover. Testing 48 caused more lag while players were connecting — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
- [Community-fetched] The same gist has a "Tested By" table. The tables are undated except one entry, which Crowdedlight edited on 10 Oct 2023:
  - 16AA: 10–80 players, 1 Gbit, Hetzner AX41/EX44, PvE/Zeus → 48, then 64
  - Karmakut: 100 players, 1 Gbit, i9-12900K, Liberation → 48, then 64
  - Digby's War Room: 92 players, 1 Gbit, AX41 R5-3600 → 48
  - UNITAF: 20–75 players, 1 Gbit, AX41 → 64
  - IBC: 60 players, i7-7700 → 64
  - B.R.O: 10–25 players → 64
  - armasrbija: ~50 players → 48
  - [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
- [Community-fetched] The gist's full block is:
  - MaxBandwidth 2147483647; the gist says it "doesn't do anything" and can be commented out
  - MinBandwidth 25600000 (~25.6 Mbit); "not really relevant", and should never exceed real bandwidth; 256000000 was also tested
  - MaxMsgSend 64
  - MaxSizeGuaranteed 512
  - **MaxSizeNonguaranteed 1024**
  - MinErrorToSend 0.001
  - MinErrorToSendNear 0.01
  - No maxPacketSize
  - [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)

**Host knowledge bases (secondary)**
- [Community-fetched] LOW.MS (dated 28 May 2026) uses:
  - MaxMsgSend **384** on busy servers, with the warning that "higher is not always better because it can starve the sim". If players desync, the first lever is MaxMsgSend, not bandwidth.
  - MinBandwidth **15,000,000** (15 Mbit), described as "a sensible floor".
  - MaxBandwidth 100 Mbit, so one server cannot starve its neighbours.
  - MaxSizeGuaranteed 512 and MaxSizeNonguaranteed 256, which "rarely need changing".
  - MinErrorToSend 0.001, MinErrorToSendNear 0.01 and maxPacketSize 1400 in the example.
  - It calls BI defaults conservative and "usually fine up to about 40 players on a stock mission".
  - [LOW.MS config guide](https://low.ms/knowledgebase/arma-3-server-configuration)
- [Community-fetched] The LinuxGSM template ships:
  - MaxMsgSend **2048**; its comment says this may reduce lag but can increase desync.
  - MinBandwidth 5,120,000 and MaxBandwidth 10,240,000.
  - MaxSizeGuaranteed 512 and MaxSizeNonguaranteed 256.
  - MinErrorToSend 0.01 and MinErrorToSendNear 0.02.
  - maxPacketSize commented out.
  - [GameServerManagers network.cfg](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)
- [Snippet-only] One host guide reportedly suggests 256–384 MaxMsgSend for high player counts and warns that pushing past what the network can carry makes desync worse. Another reportedly says to start at 2048 and lower by 256 until performance is good. I could not pin either claim to one specific URL among the [NexoraHost](https://docs.nexorahost.com/en/gameserver/arma3/optimize), [Fatality Servers](https://fatalityservers.com/kb/arma-3-server-setup-guide/) and [bestarmahosting](https://bestarmahosting.com/guides/how-to-configure-your-arma-3-server-all-options-explained/) results, so treat them as unattributed.

**Other data points**
- [Community-fetched] veteran29's "Arma 3 Basic.cfg experiments" gist (created 2020, last active Apr 2026):
  - MaxMsgSend 640 is commented out, with the note that this matches "official BI servers".
  - maxPacketSize 1430 is commented out; the comment says 1444 is used on official BI servers.
  - The only active value is MinBandwidth 8,000,000 (8 Mbit).
  - These "official BI server" claims are the gist author's, not a Bohemia statement.
  - [veteran29 gist](https://gist.github.com/veteran29/ef08068262df808780fbcd4aeda0c03a)
- [Community-fetched] A 2014 Altis Life server ran 70–90 players on a Xeon E3-1271v3. Above ~60 players it saw constant rubber-banding and desync. Its config was MaxMsgSend 2048, MinBandwidth 107374182 (~107 Mbit), MaxBandwidth 1073741824, MaxSizeGuaranteed 958, MaxSizeNonguaranteed 224 and MinErrorToSend 0.003. Lowering MinBandwidth on advice helped only "very little". Another reply said the server finds suitable min/max values by itself, that setting them only narrows the range sooner, and that basic.cfg would not fix a 100-player load. This predates the 64-bit server — [NFOservers thread, Oct–Nov 2014](https://forums.nfoservers.com/viewtopic.php?t=11674)
- [BI-snippet] A BI forum tutorial reportedly says too large a MaxMsgSend is likely to cause lag and desync. It also says a server running below 15 FPS can be considered overloaded. This is a 2013-era thread — [BI forums: Server bandwidth & Optimisation tutorial](https://forums.bohemia.net/forums/topic/147591-tutorial-server-bandwidth-optimisation/)
- [Snippet-only] A Linux-server article reportedly says the non-guaranteed packet size is the biggest factor in desync. It also reports AI (especially air) desync and attributes it mostly to network config — [Medium: ARMA 3 Linux Server Network Settings](https://medium.com/arma-3-server-administration/arma-3-linux-server-network-settings-4064eaf17779)

### Inferences
- **"Maximum" should not mean the largest numbers.** Every source that warns about MaxMsgSend warns about values that are too high, and none reports a measurable gain above 256–384. The operators closest to the 13th's profile (ACE/Zeus milsim, 1 Gbit, AX41-class CPUs) went down from 128, not up. This is one undated document with seven named operators, from before the 2.22 netcode changes, so it is one strong anecdote rather than consensus.
- A plausible reading, which I have not verified, is that the gist's MaxMsgSend 64 + MaxSizeNonguaranteed 1024 combination sends fewer but larger aggregate packets per frame. Fewer, larger packets would reduce per-message CPU work on a server whose main thread is already loaded by ACE/CBA and AI. If so, the two values should be treated as a pair, not copied independently.
- **Per-setting candidates for a "Maximum" preset** (the report writer should present these with their confidence):

| Setting | Engine default (source) | Candidate value | Confidence | Main risk / note |
|---|---|---|---|---|
| MaxMsgSend | 128 (BI wiki) | **256** (BI's own example value and the low end of the host-KB range). Step down to 128 or 64 if yellow chains appear under load. | Low | Evidence conflicts: milsim operators run 48–64; hosts run 384; 2048 appears only in configs associated with problems. |
| MaxSizeGuaranteed | 512 | **512** | High | Almost every source keeps the default. Must stay below maxPacketSize. |
| MaxSizeNonguaranteed | 256 | **256**. Use 1024 only together with MaxMsgSend 64, as in the gist pairing. | Medium | BI says larger values may reduce bandwidth but add lag. Must stay below maxPacketSize. |
| MinBandwidth (top level, bits/s) | 131072 (BI wiki; possibly stale since 2.06) | **50,000,000** (anything from 25–100 Mbit is defensible). **Not** 75% of 1 Gbit. | Medium-low | Too optimistic means lag and CPU load (BI). The 2.18+ profiling branch ignores it. |
| MaxBandwidth (top level, bits/s) | Unknown on the BI wiki | **Omit**, or ≤ 2,147,483,647 (e.g. 1,000,000,000) | Low | The gist says it does nothing. LOW.MS uses it as a 100 Mbit neighbour cap. |
| MinErrorToSend | 0.001 | **0.001**. Use 0.0005 only as an opt-in for sniper- or air-heavy ops. | Medium for 0.001; low for 0.0005 | Smaller is smoother at range but costs more traffic. I found no measurements for 0.0005 or 0.0001. |
| MinErrorToSendNear | 0.01 | **0.01** | Medium | Larger values save traffic; it also governs client→server traffic. |
| class sockets maxPacketSize | 1400 | **1400**. Use 1430 only if the RPT shows "too large non-guaranteed message" errors, and only on builds after the 2.18 hotfix. | Medium | Values above the path MTU cause fragmentation. Before the 2.18 hotfix, a changed value hid modded servers from the server list. |
| MaxCustomFileSize | See Q5 | No performance-based number found | Low | Only performance angle: the 2.22 client stops sending files over the limit. |

### Gaps
- No source published #monitor output, server FPS or bandwidth use before and after a MaxMsgSend change. All observations are qualitative ("yellow chain", "rubber-banding", "helped very little").
- No Reddit data could be retrieved (see the source tiers above).
- I could not verify the "official BI servers use MaxMsgSend 640 / maxPacketSize 1444" claim beyond the gist author's comment.
- I could not confirm whether MaxMsgSend counts per client or per server in total. This matters for how the value scales with 40 vs 80 players. No fetched source states it.

---

## Q2. Is raising MaxSizeGuaranteed / MaxSizeNonguaranteed above 512/256 ever recommended? What does Bohemia say about maxPacketSize (1400) and MTU?

### Takeaway
Bohemia documents only the downside: if either MaxSize value exceeds maxPacketSize, desync can occur. The default maxPacketSize was cut from 1490 to 1400 for exotic MTU compatibility. MaxSizeGuaranteed stays at 512 in practically every template. MaxSizeNonguaranteed is raised in one notable case: the milsim gist, which runs 1024 paired with MaxMsgSend 64. A Bohemia engine programmer (dedmen) said maxPacketSize 1430 was needed to stop a "too large non-guaranteed message" error. A 2.18 hotfix fixed a bug where a modified maxPacketSize hid servers with large mod lists from the server browser.

### Cited Findings
- [BI-snippet] MaxSizeGuaranteed is the maximum payload of a guaranteed packet, in bytes and excluding headers, with a default of 512. Small messages are packed into larger aggregate packets. Guaranteed packets carry one-off events such as shooting — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg); same wording in [VincentDercourt copy](https://raw.githubusercontent.com/VincentDercourt/arma3/master/basic.cfg)
- [BI-snippet] MaxSizeNonguaranteed has a default of 256 and carries repetitive updates such as soldier and vehicle positions. Raising it may lower the bandwidth requirement but may increase lag — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg); [VincentDercourt copy](https://raw.githubusercontent.com/VincentDercourt/arma3/master/basic.cfg)
- [BI-snippet] class sockets { maxPacketSize } is the maximum size of a packet sent over the network, default **1400**. Desync might happen if MaxSizeGuaranteed or MaxSizeNonguaranteed exceed maxPacketSize — [BI wiki: Arma 2 Basic Server Config File](https://community.bistudio.com/wiki/Arma_2:_Basic_Server_Config_File); [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg)
- [BI-snippet] Arma 2: OA 1.60 reduced the default maxPacketSize from 1490 to 1400 to improve compatibility with exotic MTU settings — [BI wiki: A2OA Patch v1.60](https://community.bistudio.com/wiki/A2OA:_Patch_v1.60)
- [Community-fetched] LinuxGSM's template comment says maxPacketSize is only needed if an ISP forces a smaller packet size. It also says to set MaxSizeNonguaranteed to about half of MaxSizeGuaranteed — [GameServerManagers network.cfg](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)
- [Community-fetched] In April 2020, dedmen wrote that maxPacketSize **1430** was needed to get rid of the server error "NetServer: trying to send a too large non-guaranteed message (len=1348/1372)". The gist's own comment says to keep maxPacketSize under 1500 to avoid fragmentation. dedmen's Bohemia employment is background knowledge and is not stated on the gist page — [veteran29 gist + comments](https://gist.github.com/veteran29/ef08068262df808780fbcd4aeda0c03a)
- [BI-fetched] **Hotfix 2.18 (23 Oct 2024):** fixed servers with a modified maxPacketSize in basic.cfg and large mod lists not appearing in the server list — [SPOTREP #00116](https://dev.arma3.com/post/spotrep-00116)
- [Community-fetched] Milsim gist: MaxSizeGuaranteed 512, **MaxSizeNonguaranteed 1024**, no maxPacketSize override — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
- [Community-fetched] LOW.MS says MaxSize values "rarely need changing" and keeps 512/256 — [LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration)
- [BI-snippet, forum] A BI forum user warned that, depending on the route and the headers added, a larger packet may fragment and become more susceptible to loss. Another post put Ethernet MTU at 1500 and internet path MTUs at roughly 1400–1472 — [BI forums: All about MaxSizeNonguaranteed](https://forums.bohemia.net/forums/topic/147599-all-about-maxsizenonguaranteed/); [BI forums tutorial](https://forums.bohemia.net/forums/topic/147591-tutorial-server-bandwidth-optimisation/)
- [BI-snippet, forum] The same "All about MaxSizeNonguaranteed" thread reportedly says the value must not exceed 1300. A reply reportedly argues the setting has little performance effect. I could not read the thread, so the author and context are unknown — [BI forums thread 147599](https://forums.bohemia.net/forums/topic/147599-all-about-maxsizenonguaranteed/)
- Background: IP fragmentation increases loss sensitivity and retransmissions, which is why path-MTU discovery is preferred — [Wikipedia: IP fragmentation](https://en.wikipedia.org/wiki/IP_fragmentation)

### Inferences
- With standard Ethernet MTU 1500, the largest unfragmented UDP payload is 1472 bytes (1500 − 20 IP − 8 UDP). PPPoE, VPN or tunnel paths are lower. 1400 leaves headroom for those paths. 1430 or 1444 still fits a clean 1500 path, but the margin is thinner for clients on PPPoE or VPN links. Raising maxPacketSize is therefore a fix for a specific RPT error, not a performance knob.
- dedmen's error shows that the engine can produce single non-guaranteed messages near 1350–1370 bytes. Large modded loadouts (ACE) are a plausible source, but that is unverified. That is why heavily modded servers sometimes need maxPacketSize above 1400, and why MaxSize values must stay below it.
- For a panel preset, the safe rule is: MaxSizeGuaranteed ≤ maxPacketSize and MaxSizeNonguaranteed ≤ maxPacketSize. The panel should validate this and refuse values that break it.

### Gaps
- I could not read Dwarden's or other BI staff posts on MaxSize values. Every BI forum thread returned 403, and search found no Dwarden quote on this topic.
- I found no source on whether 2.22's "fast retransmit on packet loss" changes the cost of fragmentation.
- I found no measurement of the bandwidth or desync effect of MaxSizeNonguaranteed 512 or 1024 vs 256.

---

## Q3. Does lowering MinErrorToSend (0.0005, 0.0001) or changing MinErrorToSendNear help distant-unit smoothness, and at what cost?

### Takeaway
Bohemia's own description supports the direction: a smaller MinErrorToSend makes distant units seen through binoculars or scopes move more smoothly, in exchange for more network traffic. A larger MinErrorToSendNear cuts traffic for nearby units and also controls client→server traffic. I found no published measurements for 0.0005 or 0.0001. Popular templates mostly move the other way (0.003–0.01). The documented default itself is disputed: 0.001 per the BI wiki versus 0.003 in LinuxGSM's comment.

### Cited Findings
- [BI-snippet] MinErrorToSend is the minimal error before an update is sent across the network. A smaller value makes units observed through binoculars or a sniper rifle move more smoothly, at the cost of more traffic. **Default 0.001.** It was 0.01 until Arma 2: OA 1.60 — [BI wiki: Arma 2 Basic Server Config File](https://community.bistudio.com/wiki/Arma_2:_Basic_Server_Config_File); [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg); wording also in [VincentDercourt copy](https://raw.githubusercontent.com/VincentDercourt/arma3/master/basic.cfg)
- [BI-snippet] MinErrorToSendNear is the minimal error before an update for **near** units is sent. A larger value reduces traffic for near units, and it is also used to control client→server traffic. **Default 0.01** — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg); [VincentDercourt copy](https://raw.githubusercontent.com/VincentDercourt/arma3/master/basic.cfg)
- [BI-snippet] The BI "Arma 3: Dedicated Server" page's example file uses MinErrorToSend **0.003**. This is an example, not the documented default — [BI wiki: Arma 3 Dedicated Server](https://community.bistudio.com/wiki/Arma_3:_Dedicated_Server)
- [Community-fetched] LinuxGSM claims the default is 0.003 (and 0.01 for Near). It then sets **0.01 / 0.02**, coarser than default, saying that reduces traffic but makes long-range movement less smooth — [GameServerManagers network.cfg](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)
- [Community-fetched] The milsim gist and LOW.MS both keep 0.001 / 0.01 — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3); [LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration)
- [Community-fetched] The 2014 Altis Life config used 0.003 — [NFOservers thread](https://forums.nfoservers.com/viewtopic.php?t=11674)
- [BI-fetched] Since 2014 the netcode sends only the difference from the previous object state, which lowers send volume, unless `guaranteedUpdates = false` is set in server.cfg — [SITREP #00058 (27 May 2014)](https://dev.arma3.com/post/sitrep-00058)

### Inferences
- Lowering MinErrorToSend raises the update rate for every distant moving entity: AI, vehicles and aircraft. Traffic, and the server's per-frame message work, therefore grow roughly with the number of distant movers. Heavy-AI ACE missions are exactly where the gist operators were already saturating at MaxMsgSend 128+, so 0.0005 could make yellow chains worse. This is an inference; nobody published numbers.
- 0.0001 is 10x finer than the default. No source supports it. Keep it out of any preset.
- Because MinErrorToSendNear also governs client→server traffic, raising it (LinuxGSM's 0.02) degrades how remote players see each other up close. That is a bad trade for CQB milsim.
- Recommended preset values: MinErrorToSend 0.001 and MinErrorToSendNear 0.01 (the BI defaults). Offer 0.0005 only as an opt-in for sniper- or air-heavy operations, with a warning to watch #monitor out-bandwidth and yellow chains.

### Gaps
- I found no source measuring smoothness, bandwidth or server FPS for MinErrorToSend 0.0005 or 0.0001.
- The units and meaning of the "error" metric (for example, whether it is distance-weighted) are not described in any fetched source.
- I found no Bohemia statement on whether 2.x netcode changes alter how these thresholds behave.

---

## Q4. Is there a formula or rule of thumb (MinBandwidth ≈ 75–80% of upload), and does it still hold on 2.x / 2.18+ / 2.20+?

### Takeaway
The "75% of upload" rule comes straight from Bohemia's own example: 768000 bps for a 1024 kbps uplink. That example dates from the low-bandwidth era. On a 1 Gbit host it would mean ~750 Mbit, which BI's own warning about "too optimistic" values argues against. Community per-player rules suggest far lower needs: 0.5 Mbit/s per player (flagged as possibly outdated) or about 256 kbit per player. Since 2.06 Bohemia has raised the default bandwidth settings. On the **profiling branch since 2.18 (April 2025), MinBandwidth is no longer used** and Min/MaxBandwidth estimation behaviour changed. I found no main-branch changelog confirming the same change on stable 2.20 or 2.22.

### Cited Findings
- [BI-snippet] BI's worked example is MaxMsgSend 256 and MinBandwidth 768000 for a 1024 kbps uplink (75%) — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg)
- [BI-snippet] MinBandwidth is the bandwidth the server is guaranteed to have, in bps, and helps the server estimate available bandwidth. Setting it **too optimistically can increase lag and CPU load**, because too many messages are sent and then discarded. Default **131072** — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg); quoted from BI docs in the [NFOservers thread](https://forums.nfoservers.com/viewtopic.php?t=11674) and [VincentDercourt copy](https://raw.githubusercontent.com/VincentDercourt/arma3/master/basic.cfg)
- [BI-snippet] MaxBandwidth is the bandwidth the server is guaranteed never to have and also feeds the estimate. The Arma 3 page lists its default as UNKNOWN — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg)
- [BI-fetched] **Game Update 2.06 (5 Oct 2021):** "Increased default network bandwidth settings". The post does not say which values changed — [SPOTREP #00100](https://dev.arma3.com/post/spotrep-00100)
- [BI-snippet] **Profiling branch v2.18, 07-04-2025:** changed Min/MaxBandwidth bandwidth estimation behaviour; MinBandwidth is now unused on the profiling branch — [BI wiki: Prof-Branch Change Log v2.18](https://community.bistudio.com/wiki/Arma_3:_Prof-Branch_Change_Log_v2.18)
- [BI-snippet] **Dev branch v2.19, 09-04-2025:** same change, Experimental builds only — [BI wiki: Dev-Branch Change Log v2.19](https://community.bistudio.com/wiki/Arma_3:_Dev-Branch_Change_Log_v2.19)
- [BI-fetched] Main-branch SPOTREPs I checked for networking lines:
  - **2.18 (8 Oct 2024):** server network anti-flood protections; fix for a server freeze when too many network messages are received. Nothing on bandwidth — [SPOTREP #00115](https://dev.arma3.com/post/spotrep-00115)
  - **2.18 hotfix:** maxPacketSize server-list fix only — [SPOTREP #00116](https://dev.arma3.com/post/spotrep-00116)
  - **2.18 mini-update:** no networking lines — [SPOTREP #00117](https://dev.arma3.com/post/spotrep-00117)
  - **2.20 (17 Jun 2025):** "Some of basic.cfg network parameters can now be provided as strings"; improved server performance when sending a large JIP queue; fixed network spam when deleting units. No mention of Min/MaxBandwidth — [SPOTREP #00118](https://dev.arma3.com/post/spotrep-00118)
  - **2.20 hotfix:** no networking lines — [SPOTREP #00119](https://dev.arma3.com/post/spotrep-00119)
  - **2.22 (11 Aug 2026):** "Netcode fast retransmit on packet loss"; "Improved netcode performance"; the client no longer sends files larger than the server's maxCustomFileSize; new idleFPSLimit server config — [SPOTREP #00120](https://dev.arma3.com/post/spotrep-00120)
- [Community-fetched] The milsim gist calls MinBandwidth "not really relevant" and says never to set it above real available bandwidth. It runs 25.6 Mbit and also tested 256 Mbit. The page's rule of thumb is 0.5 Mbit/s upload per player, flagged on 17 Jul 2024 as possibly outdated because of later netcode improvements — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
- [Community-fetched] LinuxGSM's comment suggests about 256 kb per player for MinBandwidth (5,120,000 in its template) — [GameServerManagers network.cfg](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)
- [Community-fetched] LOW.MS calls 15 Mbit "a sensible floor". It says the stock default (~256 kbit/s) is an Arma 2-era leftover, and that ceilings only matter when a link is genuinely saturated — [LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration)
- [Community-fetched] In 2014 a reply said the server finds suitable min/max values by itself and that setting them only narrows the range sooner. Lowering an over-high MinBandwidth (107 Mbit) helped "very little" — [NFOservers thread](https://forums.nfoservers.com/viewtopic.php?t=11674)
- **There is a second, separate set of bandwidth keys** inside class sockets: initBandwidth, MinBandwidth and MaxBandwidth, in **bytes/s per client**. These are distinct from the top-level server keys in bits/s.
  - [Community-fetched] veteran29's gist documents them; per its comments, initBandwidth defaults to 32000 bytes.
  - [Community-fetched] dedmen (2020/2021) confirmed that his warning applies to the **sockets** MinBandwidth: it should be lower than the slowest client that might ever connect, or you risk packet drops, lag or desync. He also said MaxBandwidth caps initBandwidth.
  - [Community-fetched] severgun (Apr 2025) relayed that dedmen gave the engine's sockets default as 32 KB/s (256 kbit/s). He warned that a very low sockets MinBandwidth can let the message backlog grow faster than it drains.
  - [veteran29 gist + comments](https://gist.github.com/veteran29/ef08068262df808780fbcd4aeda0c03a)
- [Community-fetched] `-bandwidthAlg=2` is a related server startup option, not a basic.cfg key. In March 2015 Dwarden (dev-flagged on Steam) called it "just different and experimental". He said it changes how the game prioritises queued updates in traffic channels and does nothing for router problems. It was introduced in beta rev. 96888 and affects only the server — [Steam discussion](https://steamcommunity.com/app/107410/discussions/0/617330406665084468/)
- [BI-snippet] The BI Startup Parameters page still calls `-bandwidthAlg=2` experimental — [BI wiki: Arma 3 Startup Parameters](https://community.bistudio.com/wiki/Arma_3:_Startup_Parameters)
- [Community-fetched] The milsim gist lists `-bandwidthAlg=2` among startup parameters without explanation — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
- [Snippet-only] A BI forum user reported their server ran better without `-bandwidthAlg=2` — [BI forums: Yellow chains, high CPU utilization](https://forums.bohemia.net/forums/topic/227729-yellow-chains-high-cpu-utilization/)
- [Snippet-only] FireDaemon recommends `-bandwidthAlg=2` — [FireDaemon KB](https://kb.firedaemon.com/support/solutions/articles/4000086692-arma-3-as-a-windows-service)

### Inferences
- **The 75% rule does not transfer to a 1 Gbit host.** It describes a link the server can saturate. 80 players at 0.5 Mbit/s each is about 40 Mbit, or roughly 20 Mbit at 256 kbit each, which is 2–4% of 1 Gbit. Setting MinBandwidth to ~750 Mbit tells the estimator to assume far more headroom than any client path has. That is the "too optimistic" case BI warns causes lag and CPU load. A sane "Maximum" range is about 25–100 Mbit (bits/s), with 50,000,000 as a middle value.
- On the 2.18+ profiling binary, top-level MinBandwidth is reportedly ignored. If the panel launches the profiling build, the field is cosmetic there. On stable builds its status after 2.18 is unconfirmed.
- 2.20's "network parameters can now be provided as strings" may exist so that values beyond 32-bit config number precision or range can be expressed, such as MaxBandwidth above 2,147,483,647. That is an inference; the changelog gives no reason. Until confirmed, keep numeric values ≤ 2,147,483,647.
- The panel should treat the class sockets bandwidth keys (bytes/s, per client) as an advanced or hidden group, and never auto-raise the sockets MinBandwidth. dedmen's warning makes a high value a desync risk for slow clients.

### Gaps
- **Stable-branch status of the Min/MaxBandwidth estimation change:** I found no main-branch SPOTREP (2.18, 2.20, 2.22) that mentions it. Check the full Prof-Branch v2.20/2.22 logs or ask BI.
- I could not find which defaults 2.06 raised, so the wiki's 131072 / 128 defaults may be stale.
- I found no current (2.20+) per-player bandwidth figure from Bohemia.

---

## Q5. Which widely shared copy-paste basic.cfg templates are myths or harmful, and why?

### Takeaway
The recurring harmful patterns are:
- MaxMsgSend set to 2048 or similar "big number" values.
- MinBandwidth set to 100 Mbit–1 Gbit, or to 75% of a gigabit link (BI's "too optimistic" warning).
- MaxSize values that ignore maxPacketSize.
- Inverted smoothness settings (MinErrorToSend 0.01 / Near 0.02) shipped by a popular template, with a mis-stated default.
- Bits vs bytes confusion, especially between the top-level keys and the class sockets keys.

MaxCustomFileSize's only performance angle is the 2.22 change that stops clients uploading oversize custom faces or sounds.

### Cited Findings
- **MaxMsgSend 2048:**
  - [Community-fetched] Shipped by LinuxGSM's template, whose own comment admits it may increase desync — [GameServerManagers network.cfg](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)
  - [Community-fetched] Used by the 2014 Altis Life server with constant rubber-banding — [NFOservers thread](https://forums.nfoservers.com/viewtopic.php?t=11674)
  - [Community-fetched] Contradicted by milsim operators reporting yellow chains at ≥128 — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
  - [BI-snippet] Contradicted by BI's warning that higher values can cause desync if CPU or bandwidth can't support them — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg)
  - [Community-fetched] Contradicted by LOW.MS's "can starve the sim" — [LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration)
- **MinBandwidth 107374182 / MaxBandwidth 1073741824** (the "100 Mbit / 1 Gbit" copy-paste pair):
  - [Community-fetched] Called far too high in 2014, quoting BI's "too optimistic → lag and CPU load" text — [NFOservers thread](https://forums.nfoservers.com/viewtopic.php?t=11674)
  - [Community-fetched] The milsim gist says never to exceed real bandwidth and calls the value not very relevant — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
  - [Snippet-only] One host guide reportedly suggests 107374182 as a "good starting point" on gigabit. I could not attribute this to one URL.
- **Wrong default and inverted smoothness in LinuxGSM:** it states the MinErrorToSend default as 0.003, while BI documents 0.001. It then sets 0.01 / 0.02, which is coarser and worse for long-range smoothness — [GameServerManagers network.cfg](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg) vs [BI wiki: Arma 2 Basic Server Config File](https://community.bistudio.com/wiki/Arma_2:_Basic_Server_Config_File) / [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg). The LinuxGSM MaxBandwidth of 10,240,000 (~10 Mbit) is also low for a modern host.
- **MaxSize values above maxPacketSize:** BI warns this can cause desync — [BI wiki: Arma 2 Basic Server Config File](https://community.bistudio.com/wiki/Arma_2:_Basic_Server_Config_File). Odd values such as MaxSizeGuaranteed 958 appear in old public-server templates with no rationale — [NFOservers thread](https://forums.nfoservers.com/viewtopic.php?t=11674)
- **Raising maxPacketSize as a "tweak" on old builds:** before Hotfix 2.18, a modified maxPacketSize together with a large mod list made the server disappear from the server list — [SPOTREP #00116](https://dev.arma3.com/post/spotrep-00116)
- **Out-of-range MaxBandwidth:** one public template sets MaxBandwidth = 10000000000 (10 Gbit), above the 32-bit integer maximum of 2,147,483,647 used by others — [VincentDercourt basic.cfg](https://raw.githubusercontent.com/VincentDercourt/arma3/master/basic.cfg); compare [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
- **"MaxBandwidth does nothing":** the milsim gist's claim — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3). This conflicts with BI saying it feeds bandwidth estimation, and with the 2.18 profiling change to Min/MaxBandwidth estimation — [BI wiki basic.cfg](https://community.bistudio.com/wiki/basic.cfg); [Prof-Branch Change Log v2.18](https://community.bistudio.com/wiki/Arma_3:_Prof-Branch_Change_Log_v2.18)
- **Bits vs bytes:** top-level bandwidth keys are bits/s and packet sizes are bytes — [LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration). The class sockets bandwidth keys are bytes/s per client — [veteran29 gist](https://gist.github.com/veteran29/ef08068262df808780fbcd4aeda0c03a). Copying a sockets value into the top-level key, or the reverse, is off by 8x plus a scope change.
- **Leftover display keys:** templates still carry client display keys such as `3D_Performance`, `Resolution_Bpp`, `Windowed` and `adapter` — [NFOservers thread](https://forums.nfoservers.com/viewtopic.php?t=11674); [GameServerManagers network.cfg](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg). LinuxGSM's own comment notes that `viewDistance=10000` may not take effect.
- **MaxCustomFileSize:**
  - [BI-snippet / wiki-style copy] Users with a custom face or sound larger than this size are kicked when connecting — [VincentDercourt copy](https://raw.githubusercontent.com/VincentDercourt/arma3/master/basic.cfg)
  - [Community-fetched] LinuxGSM sets 0 and describes that as disallowing custom faces and sounds — [GameServerManagers network.cfg](https://github.com/GameServerManagers/Game-Server-Configs/blob/master/Arma3/network.cfg)
  - [BI-fetched] 2.22: the client no longer sends files larger than the server's maxCustomFileSize limit — [SPOTREP #00120](https://dev.arma3.com/post/spotrep-00120)
- **Low MaxMsgSend has its own floor:** 48 caused more lag while players were connecting — [MildlyInterested gist](https://gist.github.com/MildlyInterested/5ecefd1ad75522eae1fa5e8f4b839fd3)
- **Player-count reality check:** a 2014 admin argued basic.cfg tweaks don't fix 80–100-player AI COOP load, and suggested headless clients — [NFOservers thread](https://forums.nfoservers.com/viewtopic.php?t=11674). LOW.MS says defaults are fine up to about 40 players on stock missions — [LOW.MS](https://low.ms/knowledgebase/arma-3-server-configuration)

### Inferences
- Most harmful templates share one mistake: they treat basic.cfg as "set to your pipe size". For a CPU-bound, heavily modded milsim server the bottleneck is the server main thread, not the 1 Gbit link. Values that make the engine attempt more sends per frame (high MaxMsgSend, optimistic MinBandwidth, lower error thresholds) add CPU work and can turn into desync. That is consistent with BI's warning text and the gist reports, though nobody has proven it with profiling data.
- Validation rules for the panel, derived from the sources above:
  - MaxSize* ≤ maxPacketSize.
  - maxPacketSize ≤ 1472. Warn above 1400.
  - Numeric bandwidth ≤ 2,147,483,647.
  - Warn on MaxMsgSend > 512.
  - Warn on top-level MinBandwidth > 200 Mbit.
  - Warn on MinErrorToSend < 0.0005 or > 0.003.
  - Warn on MinErrorToSendNear > 0.01.
- MaxCustomFileSize has no tuning value for a "Maximum" preset beyond capping join-time transfers. Whether 0 means "no limit" or "no custom files" is disputed (see Gaps), so the panel should not change the user's existing value as part of a performance preset.

### Gaps
- Whether MaxCustomFileSize = 0 means unlimited or kicks anyone with a custom face or sound. LinuxGSM says it disallows custom files; I found no BI text confirming either reading.
- I found no community consensus document or Reddit thread debunking templates (Reddit was unreachable). The myth list above is assembled from the conflicts between sources, not from a single published rebuttal.
- I found no FASTER or TADST default basic.cfg values (GitHub code search was unavailable).
