-- Anonymous daily usage counters. There is no per-user row anywhere: every report only adds 1 to
-- aggregate buckets, and nothing that identifies a person or a computer is ever stored.

-- One row per day and combination of version, Windows build, country, language and install kind.
-- "users" counts reports that day (one per active copy and day); "new_week"/"new_month" count the copies
-- reporting for the first time that week/month, so weekly and monthly active users add up without an id.
CREATE TABLE IF NOT EXISTS daily (
  day       TEXT NOT NULL,             -- yyyy-mm-dd (UTC)
  version   TEXT NOT NULL,
  os        TEXT NOT NULL,             -- "10" / "11" plus build, e.g. "11 26100"
  country   TEXT NOT NULL,             -- two letters from Cloudflare, "XX" when unknown
  lang      TEXT NOT NULL,             -- UI language of Windows, e.g. "es"
  install   TEXT NOT NULL,             -- "setup" | "portable"
  users     INTEGER NOT NULL DEFAULT 0,
  new_week  INTEGER NOT NULL DEFAULT 0,
  new_month INTEGER NOT NULL DEFAULT 0,
  first_run INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, version, os, country, lang, install)
);

-- How many times each feature was used that day, summed over everybody, and on how many copies.
CREATE TABLE IF NOT EXISTS features (
  day     TEXT NOT NULL,
  version TEXT NOT NULL,
  feature TEXT NOT NULL,
  uses    INTEGER NOT NULL DEFAULT 0,
  users   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, version, feature)
);
