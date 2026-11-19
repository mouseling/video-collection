# Video Collection Manager — Requirements

Status: **settled and implemented**. Target platform: **Windows only**.
See `README.md` for the build, and `CLAUDE.md` for implementation notes.

## Problem

Videos are scattered across many folders on several drives (HDDs and SSDs), all permanently
installed in the PC. Finding something to watch means re-navigating the filesystem every time.
The app should remember every location once and become the single entry point for browsing and
playing the collection.

## Content

Local video files only. Mixed container formats / extensions. Mostly downloaded clips, a few
minutes up to ~2 hours each. Collection size: a few hundred GB in total (file count not measured,
but on the order of hundreds to low thousands).

## Core model

- User registers **root folders** ("add path": paste a path or browse to it). Roots are scanned
  recursively — subfolders may contain videos too.
- Everything persists in a local database file so the collection survives restarts.
- Rescanning picks up newly added files.
- Per-video statistics accumulate over time and drive the rating / most-viewed views.

### Video identity

Videos are identified by a **content hash** (partial: size + head and tail chunks), not by path,
so statistics survive a rename or a move to another drive. Moving files is expected to be rare
once the app is in use, but must not lose data when it happens.

**Duplicate detection:** files sharing a hash are surfaced in the app so they can be reviewed.
Deletion is done manually by the user — the app only reports.

## Features

### 1. Locations tab
Mirrors the **real on-disk folder tree** (expandable hierarchy, as it exists on disk). Browse the
registered roots and pick a video to play.

### 2. Playback
The app plays video itself — no handing off to an external player. Must cope with arbitrary
containers and codecs.

**Playback always starts from the beginning.** No resume-where-you-left-off — this is explicitly
unwanted. Seeking mid-video is done manually via the seek bar.

### 3. Tags
- Add tags to a video **while watching it**.
- Create a tag on the fly, or pick from tags already defined.
- **Tags tab**: select a tag, see every file carrying it, play from there.

### 4. Play queue
Starting playback from a context (a location, or a tag) builds a queue of the remaining files in
that context. When the current video ends, the next one plays automatically — from its beginning.

### 5. Like / dislike
- Like and dislike buttons available during playback.
- **One vote per viewing session**: within a single playback of a video, like or dislike may be
  pressed once. Navigating to another video and coming back starts a new session and allows
  another vote.
- Votes **accumulate** across sessions — e.g. a video sits at 8 likes and 3 dislikes.
- **Rating tab**: sortable, with an **on-page switch to change the sorting rule**. Raw like and
  dislike counts are visible alongside whatever ranking is active.

### 6. Most viewed
- While a video plays, the app counts how long it is actually watched. **Paused time does not
  count.**
- **10-second rule**: a session under 10 seconds does not count — playback started and was then
  abandoned, so it must not inflate the counter.
- Both **total watch time** and **view count** are stored.
- Sessions accumulate: 10 min + 15 min + 8 min → 33 min total across 3 views.
- **Most-viewed tab**: the grid displays total time; **hovering shows the detail** — how many
  times the video was watched and how long. An **on-page switch** toggles the ranking between
  total time and total view count.

### 7. Most viewed parts (within a video)
- Seeking to a timestamp (e.g. 10:38) and watching from there records a **region** — where
  watching started and where it stopped.
- Same 10-second rule: a region is only saved if it was watched for more than 10 seconds, since
  that is what marks the part as interesting.
- Regions **accumulate and overlap**: watching 10:30–12:00 twice and 11:00–11:30 once makes
  11:00–11:30 the hottest stretch, and that difference must be visible.
- Heat is drawn as a **coloured highlight on the seek bar** (the YouTube "most replayed"
  pattern), with a **legend in the app** explaining what the colouring means.

## Non-requirements

- Resume playback from last position — explicitly rejected.
- Handling disconnected drives — all drives are permanently installed.
- Online metadata lookup, poster art, movie/TV library features.
