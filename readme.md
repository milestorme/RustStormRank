# 🌩️ RustStormRank

A fully featured, production-quality ranking system for Rust servers built on Oxide/uMod — featuring **live stat tracking, polished UI, player inspection, and premium Discord integration**.

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
* Dynamic scoring system with tier progression

---

## 🧠 Player Stats Tracking

Tracks:

* Kills / Deaths / KDR
* Damage dealt / taken
* Resources gathered
* Nodes hit
* Structures built / upgraded / repaired
* Playtime / survival metrics

---

## 🏆 Leaderboards

* Real-time **Top Rankings**
* Sorted by category
* Top 10 display
* Rank movement tracking (↑ ↓ new)
<img width="1701" height="1076" alt="top" src="https://github.com/user-attachments/assets/4d64ecc3-a458-4ef2-812b-a1d37eb75266" />

---

## 👤 Player Inspection System

* New **Players tab**
* View stats of any player
* Click players from:

  * Players tab
  * Top leaderboard
* Fully synced across tabs:

  * Overview
  * PvP
  * Farm
  * Build
  * Survival
  * Lifetime
<img width="1650" height="1035" alt="players" src="https://github.com/user-attachments/assets/73462b89-d2ad-4573-bbd5-e1b7b5fb7a66" />

---

## 🎨 Premium UI

* Dark **RustStorm-themed design**
* Clean layout (no clutter)
* Tier progression bar
* Highlighted leaderboard rows
* Responsive stat panels
* Visual polish across all tabs
<img width="1651" height="1027" alt="overall" src="https://github.com/user-attachments/assets/b9c1c3b6-ffd0-46d8-a9fc-4fc70cb58fc8" />

---

## 🏅 Tier System

* Progression system:

  * Unranked → Bronze → (expandable)
* Tier-based UI styling
* Progress bar tied to score
* Next-tier preview

---

## 🏷 Configurable Branding (NEW)

* UI title is now configurable via config
* Allows full rebranding for any server

```json
"General": {
  "UiTitle": "YourServer Rank"
}
```

👉 No code editing required — just change config

---

# 🌐 Discord Integration (S-Tier)

## ✨ Features

* Branded embeds with:

  * Server name (webhook username)
  * Banner image
  * Thumbnail icon
  * Custom avatar
* Clean leaderboard formatting
* Top 10 players
* Category-specific embeds
* Clickable player names (Steam profiles)

---

## 🔥 Advanced Features

* 👑 **Player of the Day**
* 🔥 **Dominating Leader** (lead % detection)
* 📈 **Rank movement tracking**
* 🥇🥈🥉 medals for top 3

---

## ⏱ Daily Auto Post

* Optional scheduled leaderboard post
* Configurable:

  * Time (hour/minute)
  * Category
  * Scope (current/lifetime)
* Posts **once per day only** (anti-spam)
* Supports **timezone control**
<img width="494" height="434" alt="discordleaderboard" src="https://github.com/user-attachments/assets/cc2a1dc3-8a86-4962-b811-d77848262759" />

---

# ⚙️ Commands

## 🛠 Admin Commands

```
/rankadmin rebuild
```

Rebuilds all ranking data.

```
/rankadmin wipe
```

Resets ranking data.

```
/rankadmin rebuild
/rankadmin wipe
/rankadmin webhook overall
/rankadmin webhook pvp
/rankadmin webhook farm
/rankadmin webhook build
/rankadmin webhook survival
/rankadmin webhook team
```

Manually sends leaderboard to Discord.

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

## ⚙️ Discord Configuration (WITH TIMEZONE)

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

## 🧠 Notes

* `UiTitle` → controls in-game UI title
* `AuthorName` → Discord webhook name
* `AvatarUrl` → webhook profile image
* `ThumbnailUrl` → embed icon (top right)
* `BannerImageUrl` → large image at bottom
* Time uses **server local time**

---

# 📌 Usage Guide

### Open UI

Triggered via your UI system or bound command.

---

### Navigate Tabs

* Overview → summary + tier
* PvP / Farm / Build / Survival → category stats
* Top → leaderboard
* Players → inspect others
* Lifetime → long-term stats

---

### Inspect Player

1. Open **Players tab**
2. Click a player
3. Navigate tabs → view their stats

---

### Discord Testing

```
/rankadmin webhook overall
```

---

## 🌍 TimeZoneId (IMPORTANT)

The plugin now uses a **timezone-aware scheduler**.

### ✔ Valid Format

Use **IANA timezone names**:

```
Australia/Perth
Australia/Sydney
UTC
America/New_York
Europe/London
```

---

## 📌 Behaviour

- `TimeZoneId` controls WHEN the daily post fires
- Uses the specified timezone instead of server local time
- If missing → defaults to `Australia/Perth`
- If invalid → falls back to `UTC`
- Works regardless of server host location

---

## 🔎 Find Valid Timezones

https://www.iana.org/time-zones

---

## 💡 Examples

Perth server:
```json
"TimeZoneId": "Australia/Perth"
```

Global server:
```json
"TimeZoneId": "UTC"
```

US server:
```json
"TimeZoneId": "America/New_York"
```

---

# ⚠️ Requirements

* Oxide/uMod
* Rust server (latest)
* Newtonsoft JSON (included in Oxide)

---

# 🧼 Performance

* Cached leaderboards
* Timed recalculations
* Safe async Discord requests
* Minimal UI redraw overhead

---

# 🔮 Future Expansion Ideas

* Tier rewards system
* Seasonal resets
* Rank decay
* Clan leaderboards
* UI animations
* Cross-server stats

---

# 🏁 Summary

RustStormRank delivers:

* 🎯 competitive ranking system
* 🎨 polished UI experience
* 🌐 premium Discord presence
* 🧠 deep stat tracking
* ⚡ high performance

---

# 💬 Credits

* Author ***Milestorme***.

---

**RustStorm — Where rankings actually matter.** 🌩️
