# 🌩️ RustStormRank

A fully featured, production-ready ranking system for Rust servers built on Oxide/uMod — featuring **live stat tracking, advanced team support, polished UI, and premium Discord integration**.

---

# 🚀 Features

## 📊 Ranking System

* Tracks player performance across multiple categories:

  * **Overall**
  * **PvP**
  * **Farm**
  * **Build**
  * **Survival**

* Separate scopes:

  * **Current Wipe**
  * **Lifetime**

* Dynamic scoring system with:

  * Weighted categories
  * Confidence scaling
  * Tier progression system

---

## 👥 Team System (NEW)

* Full **team leaderboard support**

* Teams ranked by **aggregated member performance**

* Team UI includes:

  * Leader
  * Top player (highest score)
  * Member count
  * Team rank & score

* Smart team naming:

  * `LeaderName's Team`
  * `LeaderName's Team | Top: Player`

* 🏷 Optional **Clan Tag Integration** (via Clans plugin)

  * Displays as: `[TAG] LeaderName's Team`
  * Only shown if **all members share the same clan**
  * Disabled by default (configurable)

* Automatically filters:

  * ❌ Empty teams
  * ❌ Stale / invalid teams

---

## 🧠 Player Stats Tracking

Tracks:

* Kills / Deaths / KDR
* Headshot kills
* Damage dealt / taken
* Resources gathered (wood, stone, metal, sulfur)
* Nodes harvested
* Structures built / upgraded / repaired
* Playtime & survival stats
* Distance travelled
* Longest life tracking

---

## 🏆 Leaderboards

* Real-time **Top Rankings**
* Supports:

  * Players
  * Teams
* Sorted by category
* Rank movement tracking (↑ ↓ new)
* Click-to-inspect players & teams

<img width="1707" height="1083" alt="top" src="https://github.com/user-attachments/assets/d37717c7-84ad-411a-b686-5fe7b39c5e82" />

---

## 👤 Player Inspection System

* Dedicated **Players tab**

* View stats of any player instantly

* Click players from:

  * Players tab
  * Leaderboard

* Fully synced across all tabs:

  * Overview
  * PvP
  * Farm
  * Build
  * Survival
  * Lifetime

<img width="1697" height="1064" alt="players" src="https://github.com/user-attachments/assets/f9e5c55d-aee9-4e51-b72c-37f882195523" />

---

## 🎨 Premium UI

* Clean **RustStorm-themed dark UI**

* Smooth navigation with tab system:

  * Overview
  * PvP / Farm / Build / Survival
  * Top
  * Players
  * Teams
  * Lifetime

* Features:

  * Tier progression bar
  * Highlighted leaderboard rows
  * Rank color styling
  * Responsive stat panels

<img width="1687" height="1070" alt="overall" src="https://github.com/user-attachments/assets/7f7bbb06-e6b0-4676-901f-e266a38fde3f" />

---

## 🏅 Tier System

* Progression tiers:

  * Unranked → Bronze → Silver → Gold → Platinum → Diamond → RustGod

* Features:

  * Tier-based UI colors
  * Progress bar
  * Next-tier preview
  * Tier-up notifications (chat + optional effect)

---

## 🏷 Configurable Branding

* Fully customizable UI title

```json
"General": {
  "UiTitle": "YourServer Rank"
}
```

---

# 🌐 Discord Integration (S-Tier)

## ✨ Features

* Branded Discord embeds:

  * Custom webhook name
  * Banner image
  * Thumbnail icon
  * Avatar support

* Clean leaderboard formatting

* Top players display

* Category-specific leaderboards

* Clickable Steam profiles

---

## 🔥 Advanced Features

* 👑 Player of the Day
* 🔥 Dominating Leader detection
* 📈 Rank movement tracking
* 🥇🥈🥉 Medal system

---

## ⏱ Daily Auto Post

* Fully automated daily leaderboard posting

* Configurable:

  * Time
  * Category
  * Scope
  * Timezone

* Anti-spam protection (posts once per day)

  <img width="494" height="434" alt="discordleaderboard" src="https://github.com/user-attachments/assets/cc2a1dc3-8a86-4962-b811-d77848262759" />

---

# ⚙️ Commands

## 🎮 Player Commands
```
/rank
```
Opens ranking UI
```
/rank top
```
Opens leaderboard directly
```
/rank players
```
Opens player browser
```
/rank team
```
Opens team leaderboard

---

## 🛠 Admin Commands

```
/rankadmin rebuild
```

Rebuild all ranking data

```
/rankadmin wipe
```

Force wipe reset

```
/rankadmin webhook <category>
```

Send leaderboard to Discord

Available categories:

```
overall
pvp
farm
build
survival
team
```

---

## 🔐 Permissions

```
ruststormrank.admin
```

Grant:

```
oxide.grant group admin ruststormrank.admin
```

---

# ⚙️ Configuration

## General

```json
"General": {
  "UiTitle": "RustStorm Rank",
  "ChatCommand": "rank",
  "AdminPermission": "ruststormrank.admin"
}
```

---

## Teams

```json
"Teams": {
  "UseClanTagsInTeamNames (Requires Clans mod)": false
}
```

---

## Discord

```json
"Discord": {
  "Enabled": true,
  "WebhookURL": "https://discord.com/api/webhooks/...",
  "EmbedColor": 5620223,
  "AuthorName": "RustStorm",
  "BannerImageUrl": "https://your-banner.png",
  "ThumbnailUrl": "https://your-icon.png",
  "AvatarUrl": "https://your-icon.png",
  "DailyPost": {
    "Enabled": true,
    "PostHour": 18,
    "PostMinute": 0,
    "Category": "overall",
    "Scope": "current",
    "TimeZoneId": "Australia/Perth"
  }
}
```

---

# 🌍 TimeZoneId (IMPORTANT)

Uses **IANA timezone format**:

```
Australia/Perth
UTC
America/New_York
Europe/London
```

Find more:
https://www.iana.org/time-zones

---

# 🧼 Performance

* Cached leaderboards
* Dirty-state recalculation system
* Optimized activity tracking
* Minimal UI overhead
* Safe Discord requests

---

# ⚠️ Requirements

* Oxide/uMod
* Rust server (latest)
* Newtonsoft JSON (included)

---

# 🏁 Summary

RustStormRank delivers:

* 🎯 Competitive ranking system
* 👥 Advanced team & clan integration
* 🎨 Premium UI experience
* 🌐 Professional Discord integration
* ⚡ High performance & scalability

---

# 💬 Credits

* Author **Milestorme**

---

**RustStorm — Where rankings actually matter.** 🌩️
