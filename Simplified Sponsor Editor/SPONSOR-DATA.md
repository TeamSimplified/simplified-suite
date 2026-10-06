# Sponsor data in F1 25

What the game stores about sponsors, found by reading `gamedatabases.2025.bin` and
`language_eng.lng`. Everything here was measured, not inferred, unless it says otherwise.

Sources:

- `Simplified Team Editor/original/gamedatabases.2025.bin` — 742,857 records
- `References/Language Files/language_eng.lng` — 55,991 entries, 1,798 of them about sponsors

---

## 1. The tables

| table | rows | addressable by | columns |
| --- | --- | --- | --- |
| `sponsors.sponsors` | 128 | name | 3 |
| `sponsors.sponsor_goals` | 116 | name | 9 |
| `sponsors.sponsor_rewards` | 143 | name | 3 |
| `sponsors.sponsor_slots` | 5 | name | 4 |
| title-sponsor table | 15 | hash only | 8 |
| secondary-sponsor table | 72 | hash only | 5 |
| decal/texture table | 400 | hash only | 5 |
| smaller secondary sets | 8–10 each | hash only | 5 |

The last four have no `_instancecount_` record that any name guess reached, so their
textual paths are still unknown. That does not stop editing: the format addresses every
record by the FNV-1a hash of its path, so a known hash is as good as a known name for
reading and writing. It does stop *adding rows*, which needs the path to compute new keys.

The row-name (`item`) of each named table:

| table | row name | first row |
| --- | --- | --- |
| `sponsors.sponsors` | `sponsor` | `sponsor_0` |
| `sponsors.sponsor_goals` | `sponsor_goal` | `sponsor_goal_0` |
| `sponsors.sponsor_rewards` | `sponsor_reward` | `sponsor_reward_0` |
| `sponsors.sponsor_slots` | `sponsor_slots` | `sponsor_slots_1` — **numbered from 1** |

---

## 2. Columns

### `sponsors.sponsors` — the master list

| column | example | meaning |
| --- | --- | --- |
| `_metadata_` | `jingshen` | internal id |
| `display_name` | `lng_sponsor_jingshen` | language key for the shown name |
| `texture_name` | `seishin_s1_da` | decal texture |

`texture_name` is what resolves the four misspelled ids: **jingshen → seishin**,
**satellite → satelitte**, **bellezza → belezza**, **deishin → seishin**. The id is wrong;
the texture is right.

### `sponsors.sponsor_goals`

| column | example | meaning |
| --- | --- | --- |
| `_metadata_` | `duotone_sgoal_medium` | goal id |
| `goal_bio` | `lng_primary_goal_results` | language key for the wording |
| `goal_type` | `Team` / `Driver` | who it applies to |
| `remember_until` | `Season_Ended` | lifetime — also `Race_Ended`, `Weekend_Ended`, `Next_Session`, `Career_Ended` |
| `sponsor_display_condition` | `constructor_championship_season_15_points` | what is shown as the target |
| `sponsor_value_condition` | same | what is actually measured |
| `sponsor_goal_id` | `0` | index |
| `sponsor_reward` | `duotone_sgoalreward_standard` | reference into `sponsor_rewards` |
| `goal_game_value` | *(empty)* | boolean-tagged, empty on every row read |

The German community editor's "Darstellung der Bedingung: Boolean" is not a record — it is
the *type tag* the format stores alongside each value.

### `sponsors.sponsor_rewards`

| column | example | meaning |
| --- | --- | --- |
| `_metadata_` | `duotone_sgoalreward_standard` | reward id |
| `sponsor_reward` | `5300000` | cash |
| `loyalty_xp` | `100` | loyalty XP |

### `sponsors.sponsor_slots` — the whole table

| row | `slot_id` | `sponsor_type` | `unlock_level` |
| --- | --- | --- | --- |
| `sponsor_slots_1` | 0 | eTitle | 0 |
| `sponsor_slots_2` | 0 | eSecondary | 5 |
| `sponsor_slots_3` | 1 | eSecondary | 10 |
| `sponsor_slots_4` | 2 | eSecondary | 20 |
| `sponsor_slots_5` | 3 | eSecondary | 35 |

One title slot from the start; four secondary slots unlocking at levels 5, 10, 20 and 35.

### Title-sponsor table (hash only) — the whole table

Columns: `_metadata_`, `sponsor`, `sponsor_id`, `sponsor_type` (always `eTitle`), `tier`,
`signing_bonus`, `sponsor_goals`, `default_decals`.

| sponsor | tier | signing_bonus |
| --- | --- | --- |
| duotone | 1 | 8,089,000 |
| rgb | 1 | 8,085,000 |
| quest | 1 | 8,082,000 |
| xenon | 0 | 8,070,000 |
| xformig | 0 | 8,050,000 |
| seren | 0 | 7,950,000 |
| xxy | 2 | 7,850,000 |
| psd | 2 | 7,800,000 |
| moon | 2 | 7,740,000 |
| kompass | 3 | 7,720,000 |
| atlantic | 3 | 7,700,000 |
| pacifico | 3 | 7,650,000 |
| butler_global | 3 | 7,550,000 |
| dsboptics | 4 | 7,105,000 |
| monnaie_paris | 4 | 7,095,000 |

**This table exists twice in the file** — almost certainly the 2025 and 2026 sets. Every
signing-bonus value appears exactly twice. Any edit has to be made to both copies.

### Secondary-sponsor table (hash only)

72 rows, 70 of them sponsors. Columns: `_metadata_`, `sponsor`, `sponsor_id`,
`sponsor_type` (always `eSecondary`), `default_decals`. **No money columns at all.**

### Decal/texture table (hash only)

400 rows. Columns: `_metadata_`, `id`, `material_name`, `texture_name`, `decal_group`
(empty on every row read).

---

## 3. What the data says

| finding | value |
| --- | --- |
| sponsor tiers | 2 — title and secondary; the help text says "A title sponsor is your primary sponsor" |
| title sponsors | 15 |
| secondary sponsors | 70 in the main pool |
| ranked-division entries | 24 of the 128 rows in `sponsors.sponsors` |
| decal groups `dg_<brand>` | 127 |
| goals | 116 = 45 brand (15 brands × 3) + 71 generic |
| goal difficulty scaling | base × 0.95 / 1.00 / 1.05, with 50 / 100 / 150 loyalty XP |
| difficulty naming quirk | middle tier is `medium` for 6 brands and `standard` for 9; rewards always say `standard` |
| rewards | 143 — 115 referenced by a goal, 28 orphaned |
| the orphans | mostly `_global` brands (apeture, asiatic, distort, exotiger, rokk, savant, zenith), 11.6m–17.1m, unused content |
| brand goal payouts | 4.18m–16.96m |
| generic goal payouts | 25k–300k, nearly all 0 XP — the secondary-sponsor pool |
| loyalty levels | 9 (`sponsor_loyalty_level_1`…`_9`, plus `sponsor_loyalty_track`) |
| display rounding | the game **truncates**: 8,089,000 shows as "8.08m", not 8.09m |

Worked example, Duotone:

```
duotone_sgoal_easy    constructor_championship_season_10_points   5.04m   50 xp
duotone_sgoal_medium  constructor_championship_season_15_points   5.30m  100 xp
duotone_sgoal_hard    constructor_championship_season_25_points   5.57m  150 xp
signing_bonus         8,089,000  (tier 1)
```

A player reported the medium goal as worth 5.45m in game against 5.30m stored, so a
runtime multiplier of roughly 1.028 applies — consistent with
`MyTeam_PrimarySponsorLoyaltyMult_SeasonalGoal`.

---

## 4. The language file

1,798 sponsor-related entries across 671 key shapes.

| family | what it is |
| --- | --- |
| `lng_sponsor_<brand>` | the shown name |
| `lng_sponsor_<brand>_bio` | the blurb |
| `lng_sponsor_<brand>_s1`…`_s4` | **loyalty-gated decal variants** |
| `lng_sponsor_slot_1`…`_11` | slot labels |
| `lng_titlesponsor_livery<n>` | title-sponsor liveries |
| `lng_decal<n>`, `lng_badgeemblem<n>` | decal and emblem names |
| `lng_sponsor_ranked<n>_season<n>` | ranked-division sponsors |
| `lng_deptevent_sponsordiscovery*` | the discovery events |

Settled from the text itself:

- **Two tiers only.** `lng_sponsor_loyalty_unlock_desc` — "Reach Loyalty Level [LEVEL] with
  this sponsor in My Team to unlock it." — confirms the `s1`–`s4` variants are loyalty-gated.
- **Loyalty pays out in five things**: `+[AMOUNT] Weekly Income`, Decal Unlock, Livery
  Unlock, Activity Unlock, Owner Experience.
- **Four core values**: Fan Engagement, Innovation & Technology, Performance Excellence,
  Youth Development (`lng_core_value_*`). Duotone is Innovation & Technology.
- **Decal naming**: `_n` and `_p` are white/black monochrome pairs (identical opaque pixel
  counts; `#EFEFEF` vs `#101010`).
- Income is multiplied, not fixed: `lng_rewards_primarysponsor_weeklyincome_mult` —
  "Increase the income from your title sponsor by [0]%".

---

## 5. What is *not* in the database

| missing | how we know |
| --- | --- |
| **weekly income** | 1,468 income-style column spellings tested against all 17,006 money-sized records; the only `income` column in the entire file is a 4-row driver-rating-band table (1–20 → 25,000, 21–40 → 15,000, 41–70 → 10,000, 71–90 → 5,000). The figure 556,970 appears **zero** times at any scale or spelling; nearest values are 540,000 and 550,000. Control: the signing bonuses are all present exactly. |
| **core value assignment** | the four values exist as language keys; no column on any sponsor table carries which sponsor has which |
| **loyalty reward amounts** | the modifier *ids* exist (`CashReward_OnTitleSponsorLoyaltyUp_Lv1–3`, `OXPReward_SponsorLoyalty_Small/Medium/Large`, `MyTeam_Sponsors_LoyaltyGainRate`) but the table defining their values has not been located |

Weekly income is therefore computed at runtime. Getting the figure for a second title
sponsor would likely pin down the formula.

---

## 6. How this was found

Worth recording, because the same technique reaches the remaining unknowns.

`gamedatabases.bin` **stores no paths and no table of contents** — each record is keyed by
the FNV-1a (64-bit) hash of its dotted path, and records are sorted by hash, so there is no
locality to exploit. A record can only be found by hashing a guess.

Three things mattered:

1. **Guessing forwards has a blind spot.** Path components can contain underscores
   (`sponsor_goals`, `entrant_lists`, `driver_statistics`). Sweeps that built components
   from single dictionary words could never spell those — 4.6 billion probes across 410
   roots found only the tables whose names happen to be one word. Widening components to
   two words is what found `sponsors.sponsor_goals` and `sponsors.sponsor_rewards`.

2. **FNV-1a is invertible.** It multiplies by an odd constant, so the hash can be run
   *backwards*: `h *= inverse(prime); h ^= byte`. Given a key and the end of its path, the
   state before that ending comes back exactly. That turns "does this path exist" into a
   lookup and makes two-word components affordable.

3. **Convergence finds tables with no name at all.** Rows of a table share everything up to
   the row number, so unwinding `_<n>.<column>` from several records of one column lands
   them all on the same state — the hash of `<table>.<item>`, whatever it is called. A
   random key lands on nothing, which is the control. This is how the title-sponsor table
   and its `signing_bonus` column were found after name-guessing had failed completely.

Validity test that matters: a genuine `<table>.<item>` state has numbered rows carrying
`_metadata_` under it. Without that check the convergence returns hundreds of plausible-
looking states; with it, the survivors were all real tables.

---

## 7. Open threads

- Name the four hash-only tables, so rows can be added and not just edited.
- Find the loyalty reward table (the `CashReward_*` / `OXPReward_*` values).
- Work out the weekly-income formula — needs one more sponsor's figure from a save.
- Confirm whether the duplicated title table really is 2025 vs 2026.
